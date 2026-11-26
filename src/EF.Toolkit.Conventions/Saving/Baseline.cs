using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Conventions.Saving;

/// <summary>Accepts an entry's current values as its baseline, so EF's next change detection finds nothing to write.</summary>
/// <remarks>
///     <para>
///         A soft delete writes the soft-delete column and nothing else. Edits the application made
///         before <c>Remove()</c> would otherwise ride along: EF's own change detection runs after the
///         conversion and compares every member against its original snapshot — scalars, complex
///         values, optional complex values set to <see langword="null" />, complex collections. A real
///         delete would have discarded them all, so the soft delete does too.
///     </para>
///     <para>
///         The whole snapshot is replaced at once through <c>OriginalValues.SetValues</c>, which EF
///         itself defines over every kind of member, rather than member by member, where each kind of
///         member left out is an edit that still gets written. Concurrency tokens keep their original
///         values: those are what the <c>UPDATE</c>'s <c>WHERE</c> clause has to check.
///     </para>
/// </remarks>
internal static class Baseline
{
    internal static void Accept(EntityEntry entry)
    {
        var tokens = entry.Properties
            .Where(p => p.Metadata.IsConcurrencyToken)
            .Select(p => (Property: p, Original: p.OriginalValue))
            .ToList();

        // A token inside a complex value is not among entry.Properties, but SetValues copies it all the
        // same: the UPDATE would check the edited value, failing against an unchanged row and passing
        // against one another writer had moved on. Its original is kept, and its current value put back
        // to match — the delete discards the edit, as it does every other.
        var complexTokens = entry.OriginalValues.Properties
            .Where(p => p.IsConcurrencyToken && p.DeclaringType is IComplexType)
            .Select(p => (Property: p, Original: entry.OriginalValues[p]))
            .ToList();

        RestoreClearedComplexValues(entry.Entity, entry.ComplexProperties, entry.OriginalValues);
        entry.OriginalValues.SetValues(entry.CurrentValues);

        foreach (var (property, original) in tokens)
        {
            property.OriginalValue = original;
        }

        foreach (var (property, original) in complexTokens)
        {
            entry.OriginalValues[property] = original;

            // Through a copy of its own: the same complex instance may also be another entity's (an
            // application that assigned one entity's value to another), and putting the token back on
            // the shared instance would undo that entity's change too. EF compares complex values by
            // value, so the swap itself writes nothing.
            if (OwnCopy(entry.Entity, (IComplexType)property.DeclaringType))
            {
                entry.CurrentValues[property] = original;
            }
        }

        foreach (var property in entry.Properties)
        {
            property.IsModified = false;
        }

    }

    // An optional complex value set to null before Remove() is an edit like any other. Comparing a null
    // value against its snapshot is where EF Core versions differ — 11 writes the nulls even with the
    // snapshot replaced — so the value is rebuilt from its members' original values instead, before the
    // snapshot is. The entity is being deleted, so reverting it in memory costs the application nothing.
    //
    // A nested value is put back on the entity's own copy of the values containing it: the containing
    // instance may also be another entity's, whose own edit — clearing the same nested value — would be
    // undone by writing into it.
    private static void RestoreClearedComplexValues(object entity, IEnumerable<ComplexPropertyEntry> complexProperties, PropertyValues originals)
    {
        foreach (var complex in complexProperties)
        {
            if (complex.CurrentValue is not null)
            {
                RestoreClearedComplexValues(entity, complex.ComplexProperties, originals);
                continue;
            }

            if (complex.Metadata.IsNullable && FromOriginals(complex.Metadata, originals) is { } value)
            {
                if (complex.Metadata.DeclaringType is IComplexType containing)
                {
                    OwnCopy(entity, containing);
                }

                complex.CurrentValue = value;
            }
        }
    }

    // The value a complex property had when loaded, rebuilt member by member and level by level from the
    // original snapshot — or null for an optional value that was absent. Presence is decided from the
    // snapshot alone, before anything is built: constructing a required child first would make an
    // absent parent look present, and invent a value the row never had.
    private static object? FromOriginals(IComplexProperty complexProperty, PropertyValues originals)
    {
        if (complexProperty.IsNullable && !HadAnyValue(complexProperty.ComplexType, originals))
        {
            return null;
        }

        var value = Create(complexProperty.ClrType);
        foreach (var property in complexProperty.ComplexType.GetProperties())
        {
            Set(property, value, originals[property]);
        }

        foreach (var nested in complexProperty.ComplexType.GetComplexProperties().Where(c => !c.IsCollection))
        {
            Set(nested, value, FromOriginals(nested, originals));
        }

        return value;
    }

    // EF keeps no presence flag for an optional complex value in the snapshot: the members of an absent
    // one read back as their CLR defaults — 0, not null, for an int. So a value counts as having been
    // there only if some member held something other than its default. The one value this misreads is
    // a present one made entirely of defaults, cleared before Remove(): it is not rebuilt, and on
    // EF Core 11 the clear is written. Inventing a value for every absent one would be worse.
    private static bool HadAnyValue(IComplexType type, PropertyValues originals)
        => type.GetProperties().Any(p => !IsDefault(originals[p], p.ClrType))
            || type.GetComplexProperties().Where(c => !c.IsCollection).Any(c => HadAnyValue(c.ComplexType, originals));

    private static bool IsDefault(object? value, Type clrType)
        => value is null
            || (clrType.IsValueType && Nullable.GetUnderlyingType(clrType) is null && value.Equals(Activator.CreateInstance(clrType)));

    // Replaces each complex value on the way from the entity down to complexType with a shallow copy, so
    // the entity alone holds the instance a member of complexType is then written to. False when one on
    // the way is absent, and there is no current value to correct.
    private static bool OwnCopy(object entity, IComplexType complexType)
    {
        var path = new Stack<IComplexProperty>();
        for (IComplexType? type = complexType; type is not null; type = type.ComplexProperty.DeclaringType as IComplexType)
        {
            path.Push(type.ComplexProperty);
        }

        var owner = entity;
        foreach (var complexProperty in path)
        {
            if (Get(complexProperty, owner) is not { } value)
            {
                return false;
            }

            var copy = MemberwiseCloneMethod.Invoke(value, null)!;
            Set(complexProperty, owner, copy);
            owner = copy;
        }

        return true;
    }

    private static readonly System.Reflection.MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
        "MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static object? Get(IPropertyBase member, object target)
        => member.FieldInfo is { } field ? field.GetValue(target) : member.PropertyInfo?.GetValue(target);

    // Value objects often hide their parameterless constructor, or have none: EF materializes them
    // either way, and so must this. Without one, the instance starts uninitialized and every mapped
    // member is written below.
    private static object Create(Type type)
    {
        try
        {
            return Activator.CreateInstance(type, nonPublic: true)!;
        }
        catch (MissingMethodException)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        }
    }

    // Through the backing field where EF maps one — its default, and the only way into a get-only or
    // privately set member — and through the property otherwise.
    private static void Set(IPropertyBase member, object target, object? value)
    {
        if (member.FieldInfo is { } field)
        {
            field.SetValue(target, value);
        }
        else
        {
            member.PropertyInfo?.SetValue(target, value);
        }
    }
}
