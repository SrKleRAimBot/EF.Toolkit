using System.Linq.Expressions;
using EFToolkit.Bulk.Api;
using Microsoft.EntityFrameworkCore.Query;

// Deliberately in EF's own namespace so these are visible with the using any EF Core application
// already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Set-based updates and deletes: one statement for every row matching a condition, with no
///     entities loaded.
/// </summary>
/// <remarks>
///     <para>
///         These are EF Core's <c>ExecuteUpdate</c> and <c>ExecuteDelete</c> with the condition and the
///         assignments as arguments of their own, so that a call reads the way the SQL does —
///         <c>UPDATE … SET … WHERE …</c> — and both lambdas are typed against the entity:
///     </para>
///     <code>
///     await db.Orders.UpdateWhereAsync(
///         o =&gt; o.Status == OrderStatus.Pending &amp;&amp; o.PlacedAt &lt; cutoff,
///         o =&gt; new Order { Status = OrderStatus.Expired, Total = o.Total * 0.9m });
///
///     await db.Orders.DeleteWhereAsync(o =&gt; o.Status == OrderStatus.Expired);
///     </code>
///     <para>
///         The assignments are written as an object initializer over the row, so the editor lists the
///         assignable members inside the braces, and a value can refer to the row's current values
///         (<c>o.Total * 0.9m</c>). Nested initializers set columns of a complex property:
///         <c>new Customer { Address = { City = "Oslo" } }</c>. Where an initializer cannot be written
///         — an entity with <c>required</c> members, or no accessible setters — the
///         <see cref="UpdateSettersBuilder{TSource}" /> overload takes EF's own setter syntax.
///     </para>
///     <para>
///         Like <c>ExecuteUpdate</c>, these run immediately, outside <c>SaveChanges()</c>: they
///         neither read nor update the change tracker, global query filters apply to the condition,
///         and an ambient transaction is honoured. None of them needs <c>UseBulkOperations()</c>.
///     </para>
/// </remarks>
public static class SetBasedWriteExtensions
{
    // ---------------------------------------------------------------------------------------
    // UpdateWhere — object initializer
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     Updates every row of <paramref name="source" /> matching <paramref name="condition" />,
    ///     setting the members assigned in <paramref name="valueUpdates" />.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="source">The rows to consider — a <c>DbSet</c>, or a query already narrowed further.</param>
    /// <param name="condition">Which rows to update.</param>
    /// <param name="valueUpdates">
    ///     An object initializer naming each column to set and its new value, e.g.
    ///     <c>o =&gt; new Order { Status = OrderStatus.Shipped, Version = o.Version + 1 }</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows updated.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="valueUpdates" /> is not a plain object initializer of
    ///     <typeparamref name="TEntity" /> assigning at least one member.
    /// </exception>
    public static Task<int> UpdateWhereAsync<TEntity>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, bool>> condition,
        Expression<Func<TEntity, TEntity>> valueUpdates,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        var setters = Setters(source, condition, valueUpdates);
        return source.Where(condition).ExecuteUpdateAsync(setters, cancellationToken);
    }

    /// <inheritdoc cref="UpdateWhereAsync{TEntity}(IQueryable{TEntity}, Expression{Func{TEntity, bool}}, Expression{Func{TEntity, TEntity}}, CancellationToken)" />
    public static int UpdateWhere<TEntity>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, bool>> condition,
        Expression<Func<TEntity, TEntity>> valueUpdates)
        where TEntity : class
    {
        var setters = Setters(source, condition, valueUpdates);
        return source.Where(condition).ExecuteUpdate(setters);
    }

    /// <summary>
    ///     Updates every <typeparamref name="TEntity" /> row matching <paramref name="condition" />,
    ///     setting the members assigned in <paramref name="valueUpdates" />.
    /// </summary>
    /// <typeparam name="TEntity">The entity type. Name it explicitly: <c>db.UpdateWhereAsync&lt;Order&gt;(…)</c>.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="condition">Which rows to update.</param>
    /// <param name="valueUpdates">
    ///     An object initializer naming each column to set and its new value, e.g.
    ///     <c>o =&gt; new Order { Status = OrderStatus.Shipped }</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows updated.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="valueUpdates" /> is not a plain object initializer of
    ///     <typeparamref name="TEntity" /> assigning at least one member.
    /// </exception>
    public static Task<int> UpdateWhereAsync<TEntity>(
        this DbContext context,
        Expression<Func<TEntity, bool>> condition,
        Expression<Func<TEntity, TEntity>> valueUpdates,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => Set<TEntity>(context).UpdateWhereAsync(condition, valueUpdates, cancellationToken);

    /// <inheritdoc cref="UpdateWhereAsync{TEntity}(DbContext, Expression{Func{TEntity, bool}}, Expression{Func{TEntity, TEntity}}, CancellationToken)" />
    public static int UpdateWhere<TEntity>(
        this DbContext context,
        Expression<Func<TEntity, bool>> condition,
        Expression<Func<TEntity, TEntity>> valueUpdates)
        where TEntity : class
        => Set<TEntity>(context).UpdateWhere(condition, valueUpdates);

    // ---------------------------------------------------------------------------------------
    // UpdateWhere — EF's setter builder
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     Updates every row of <paramref name="source" /> matching <paramref name="condition" />,
    ///     applying EF Core's <c>SetProperty</c> calls.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="source">The rows to consider.</param>
    /// <param name="condition">Which rows to update.</param>
    /// <param name="setters">
    ///     The assignments, e.g. <c>s =&gt; s.SetProperty(o =&gt; o.Status, OrderStatus.Shipped)</c>.
    ///     For entities that cannot be written as an object initializer.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows updated.</returns>
    public static Task<int> UpdateWhereAsync<TEntity>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, bool>> condition,
        Action<UpdateSettersBuilder<TEntity>> setters,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        Check(source, condition);
        ArgumentNullException.ThrowIfNull(setters);
        return source.Where(condition).ExecuteUpdateAsync(setters, cancellationToken);
    }

    /// <inheritdoc cref="UpdateWhereAsync{TEntity}(IQueryable{TEntity}, Expression{Func{TEntity, bool}}, Action{UpdateSettersBuilder{TEntity}}, CancellationToken)" />
    public static int UpdateWhere<TEntity>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, bool>> condition,
        Action<UpdateSettersBuilder<TEntity>> setters)
        where TEntity : class
    {
        Check(source, condition);
        ArgumentNullException.ThrowIfNull(setters);
        return source.Where(condition).ExecuteUpdate(setters);
    }

    /// <inheritdoc cref="UpdateWhereAsync{TEntity}(IQueryable{TEntity}, Expression{Func{TEntity, bool}}, Action{UpdateSettersBuilder{TEntity}}, CancellationToken)" />
    /// <param name="context">The context.</param>
    /// <param name="condition">Which rows to update.</param>
    /// <param name="setters">The assignments.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static Task<int> UpdateWhereAsync<TEntity>(
        this DbContext context,
        Expression<Func<TEntity, bool>> condition,
        Action<UpdateSettersBuilder<TEntity>> setters,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => Set<TEntity>(context).UpdateWhereAsync(condition, setters, cancellationToken);

    /// <inheritdoc cref="UpdateWhereAsync{TEntity}(IQueryable{TEntity}, Expression{Func{TEntity, bool}}, Action{UpdateSettersBuilder{TEntity}}, CancellationToken)" />
    /// <param name="context">The context.</param>
    /// <param name="condition">Which rows to update.</param>
    /// <param name="setters">The assignments.</param>
    public static int UpdateWhere<TEntity>(
        this DbContext context,
        Expression<Func<TEntity, bool>> condition,
        Action<UpdateSettersBuilder<TEntity>> setters)
        where TEntity : class
        => Set<TEntity>(context).UpdateWhere(condition, setters);

    // ---------------------------------------------------------------------------------------
    // DeleteWhere
    // ---------------------------------------------------------------------------------------

    /// <summary>Deletes every row of <paramref name="source" /> matching <paramref name="condition" />.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="source">The rows to consider — a <c>DbSet</c>, or a query already narrowed further.</param>
    /// <param name="condition">Which rows to delete.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows deleted.</returns>
    public static Task<int> DeleteWhereAsync<TEntity>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, bool>> condition,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        Check(source, condition);
        return source.Where(condition).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc cref="DeleteWhereAsync{TEntity}(IQueryable{TEntity}, Expression{Func{TEntity, bool}}, CancellationToken)" />
    public static int DeleteWhere<TEntity>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, bool>> condition)
        where TEntity : class
    {
        Check(source, condition);
        return source.Where(condition).ExecuteDelete();
    }

    /// <summary>Deletes every <typeparamref name="TEntity" /> row matching <paramref name="condition" />.</summary>
    /// <typeparam name="TEntity">The entity type. Name it explicitly: <c>db.DeleteWhereAsync&lt;Order&gt;(…)</c>.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="condition">Which rows to delete.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows deleted.</returns>
    public static Task<int> DeleteWhereAsync<TEntity>(
        this DbContext context,
        Expression<Func<TEntity, bool>> condition,
        CancellationToken cancellationToken = default)
        where TEntity : class
        => Set<TEntity>(context).DeleteWhereAsync(condition, cancellationToken);

    /// <inheritdoc cref="DeleteWhereAsync{TEntity}(DbContext, Expression{Func{TEntity, bool}}, CancellationToken)" />
    public static int DeleteWhere<TEntity>(
        this DbContext context,
        Expression<Func<TEntity, bool>> condition)
        where TEntity : class
        => Set<TEntity>(context).DeleteWhere(condition);

    // ---------------------------------------------------------------------------------------

    private static Action<UpdateSettersBuilder<TEntity>> Setters<TEntity>(
        IQueryable<TEntity> source,
        Expression<Func<TEntity, bool>> condition,
        Expression<Func<TEntity, TEntity>> valueUpdates)
        where TEntity : class
    {
        Check(source, condition);

        // Translated before anything runs, so a malformed initializer fails as an argument error at
        // the call site rather than as a query translation failure from inside EF.
        return ValueUpdateTranslator.ToSetterCallback<TEntity>(ValueUpdateTranslator.Translate(valueUpdates));
    }

    private static void Check<TEntity>(IQueryable<TEntity> source, Expression<Func<TEntity, bool>> condition)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(condition);
    }

    private static DbSet<TEntity> Set<TEntity>(DbContext context)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Set<TEntity>();
    }
}
