using System.Data.Common;
using System.Reflection;
using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Execution;

/// <summary>Turns the <c>args</c> object of a procedure or function call into named arguments.</summary>
/// <remarks>
///     Accepts an anonymous (or any) object, whose readable public properties become arguments in
///     declaration order, or an <see cref="IReadOnlyDictionary{TKey, TValue}" />, whose entries do. A
///     value that is already a <see cref="DbParameter" /> is passed through untouched, which is how a
///     caller gets an output parameter back.
/// </remarks>
internal static class RoutineArguments
{
    public static IReadOnlyList<(string Name, object? Value)> From(object? args)
    {
        var arguments = args switch
        {
            null => [],
            IReadOnlyDictionary<string, object?> dictionary => dictionary.Select(e => (e.Key, e.Value)).ToList(),
            IEnumerable<KeyValuePair<string, object?>> pairs => pairs.Select(e => (e.Key, e.Value)).ToList(),
            string or DbParameter or System.Collections.IEnumerable => throw new ArgumentException(
                $"Routine arguments are passed as an object whose properties name them (new {{ tenantId = 1 }}) "
                + $"or as an IReadOnlyDictionary<string, object?>; a {args.GetType().Name} cannot name its values.",
                nameof(args)),
            _ => FromProperties(args),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < arguments.Count; i++)
        {
            var (name, value) = arguments[i];
            name = SqlSyntax.RequireSimpleIdentifier(
                name is not null && name.StartsWith('@') ? name[1..] : name!,
                nameof(args));

            if (!seen.Add(name))
            {
                throw new ArgumentException($"Argument '{name}' is given twice.", nameof(args));
            }

            arguments[i] = (name, value);
        }

        return arguments;
    }

    private static List<(string, object?)> FromProperties(object args)
    {
        // GetProperties does not promise declaration order, but the metadata token does: properties
        // are emitted in source order, which is the order a positional engine (MySQL, SQL Server
        // functions) needs.
        return args.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken)
            .Select(p => (p.Name, p.GetValue(args)))
            .ToList();
    }
}
