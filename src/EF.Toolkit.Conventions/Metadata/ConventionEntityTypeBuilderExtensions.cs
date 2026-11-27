using System.Linq.Expressions;
using EFToolkit.Conventions;
using EFToolkit.Conventions.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// Deliberately in EF's own namespace so these are visible wherever an entity type is configured.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Declares soft delete and timestamps on an entity type.</summary>
/// <remarks>
///     <para>
///         Declaring is half of it. The model records which property means what, and adds the soft
///         delete query filter; the save-time behaviour — turning a delete into an update, stamping
///         times — comes from <c>UseEntityConventions()</c> on the options. A model declared here and a
///         context without that call reads correctly but writes as stock EF would, so
///         <c>Remove()</c> deletes.
///     </para>
/// </remarks>
public static class ConventionEntityTypeBuilderExtensions
{
    // ---------------------------------------------------------------------------------------
    // Soft delete
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     Makes <see cref="DbContext.Remove{TEntity}(TEntity)" /> mark the row deleted by setting
    ///     <paramref name="isDeleted" /> to <see langword="true" />, and hides such rows from every
    ///     query.
    /// </summary>
    /// <typeparam name="TEntity">The entity type. Must be the root of its hierarchy.</typeparam>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="isDeleted">The flag, e.g. <c>p =&gt; p.IsDeleted</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static EntityTypeBuilder<TEntity> HasSoftDelete<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, bool>> isDeleted)
        where TEntity : class
        => SoftDelete(builder, MemberName(isDeleted, nameof(isDeleted)), typeof(bool));

    /// <summary>
    ///     Makes <see cref="DbContext.Remove{TEntity}(TEntity)" /> mark the row deleted by recording
    ///     the time in <paramref name="deletedAt" />, and hides such rows from every query.
    /// </summary>
    /// <typeparam name="TEntity">The entity type. Must be the root of its hierarchy.</typeparam>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="deletedAt">The timestamp, e.g. <c>p =&gt; p.DeletedAt</c>. Written in UTC.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static EntityTypeBuilder<TEntity> HasSoftDelete<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, DateTime?>> deletedAt)
        where TEntity : class
        => SoftDelete(builder, MemberName(deletedAt, nameof(deletedAt)), typeof(DateTime?));

    /// <inheritdoc cref="HasSoftDelete{TEntity}(EntityTypeBuilder{TEntity}, Expression{Func{TEntity, DateTime?}})" />
    public static EntityTypeBuilder<TEntity> HasSoftDelete<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, DateTimeOffset?>> deletedAt)
        where TEntity : class
        => SoftDelete(builder, MemberName(deletedAt, nameof(deletedAt)), typeof(DateTimeOffset?));

    /// <summary>
    ///     Soft delete through a property the entity class need not declare: a shadow <see cref="bool" />
    ///     named <paramref name="propertyName" /> is added if the model has no such property.
    /// </summary>
    /// <typeparam name="TEntity">The entity type. Must be the root of its hierarchy.</typeparam>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="propertyName">
    ///     The property. An existing one keeps its type, which must be <see cref="bool" /> or a nullable
    ///     <see cref="DateTime" /> / <see cref="DateTimeOffset" />.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static EntityTypeBuilder<TEntity> HasSoftDelete<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string propertyName = "IsDeleted")
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        var existing = builder.Metadata.FindProperty(propertyName);
        return SoftDelete(builder, propertyName, existing?.ClrType ?? typeof(bool));
    }

    // ---------------------------------------------------------------------------------------
    // Timestamps
    // ---------------------------------------------------------------------------------------

    /// <summary>Records when each row was inserted in <paramref name="createdAt" />.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TTime">
    ///     <see cref="DateTime" /> or <see cref="DateTimeOffset" />, optionally nullable.
    /// </typeparam>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="createdAt">The property, e.g. <c>p =&gt; p.CreatedAt</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     Set on insert unless the entity already carries a value, and never changed afterwards: a
    ///     save that modifies it has the modification reverted.
    /// </remarks>
    public static EntityTypeBuilder<TEntity> HasCreatedAt<TEntity, TTime>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, TTime>> createdAt)
        where TEntity : class
        => Timestamp(builder, ConventionMetadata.CreatedAtAnnotation, MemberName(createdAt, nameof(createdAt)), typeof(TTime));

    /// <summary>Records when each row was last written in <paramref name="updatedAt" />.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TTime">
    ///     <see cref="DateTime" /> or <see cref="DateTimeOffset" />, optionally nullable.
    /// </typeparam>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="updatedAt">The property, e.g. <c>p =&gt; p.UpdatedAt</c>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>Set on insert, on every update and on a soft delete.</remarks>
    public static EntityTypeBuilder<TEntity> HasUpdatedAt<TEntity, TTime>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, TTime>> updatedAt)
        where TEntity : class
        => Timestamp(builder, ConventionMetadata.UpdatedAtAnnotation, MemberName(updatedAt, nameof(updatedAt)), typeof(TTime));

    /// <summary>
    ///     Records insert and last-write times in shadow <see cref="DateTime" /> properties (UTC), so
    ///     the entity class need not declare them.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="builder">The entity type builder.</param>
    /// <param name="createdAt">The insert-time property. Existing properties keep their type.</param>
    /// <param name="updatedAt">The last-write property.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static EntityTypeBuilder<TEntity> HasTimestamps<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string createdAt = "CreatedAt",
        string updatedAt = "UpdatedAt")
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedAt);

        Timestamp(builder, ConventionMetadata.CreatedAtAnnotation, createdAt,
            builder.Metadata.FindProperty(createdAt)?.ClrType ?? typeof(DateTime));
        return Timestamp(builder, ConventionMetadata.UpdatedAtAnnotation, updatedAt,
            builder.Metadata.FindProperty(updatedAt)?.ClrType ?? typeof(DateTime));
    }

    // ---------------------------------------------------------------------------------------

    private static EntityTypeBuilder<TEntity> SoftDelete<TEntity>(
        EntityTypeBuilder<TEntity> builder, string name, Type clrType)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Metadata.BaseType is not null)
        {
            // EF only accepts query filters on a hierarchy root, and a filter on the root already
            // covers every derived type. Saying so here beats EF's message about filters.
            throw new InvalidOperationException(
                $"Soft delete must be configured on '{builder.Metadata.GetRootType().DisplayName()}', the root "
                + $"of the hierarchy '{builder.Metadata.DisplayName()}' belongs to. It then applies to every "
                + "type derived from it.");
        }

        ConventionMetadata.KindOf(clrType, name, builder.Metadata.DisplayName());

        builder.Property(clrType, name);
        builder.HasAnnotation(ConventionMetadata.SoftDeleteAnnotation, name);
        builder.HasQueryFilter(SoftDeleteFilter.Name, ConventionMetadata.IsLive<TEntity>(name, clrType));
        return builder;
    }

    private static EntityTypeBuilder<TEntity> Timestamp<TEntity>(
        EntityTypeBuilder<TEntity> builder, string annotation, string name, Type clrType)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!ConventionMetadata.IsTimestampType(clrType))
        {
            throw new InvalidOperationException(
                $"The timestamp property '{builder.Metadata.DisplayName()}.{name}' is a '{clrType.Name}'. "
                + "It must be a DateTime or DateTimeOffset, optionally nullable.");
        }

        builder.Property(clrType, name);
        builder.HasAnnotation(annotation, name);
        return builder;
    }

    private static string MemberName(LambdaExpression selector, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(selector, parameterName);

        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : selector.Body;

        if (body is MemberExpression { Expression: ParameterExpression } member)
        {
            return member.Member.Name;
        }

        throw new ArgumentException(
            $"Expected a property of the entity, such as 'e => e.DeletedAt', but got '{selector}'.",
            parameterName);
    }
}
