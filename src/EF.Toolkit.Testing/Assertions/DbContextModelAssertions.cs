using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using EFToolkit.Testing;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

// Deliberately in EF's own namespace so these are visible with the using any EF Core application
// already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Assertions over a context's model, for tests that pin down the mapping.</summary>
/// <remarks>
///     Each reads the model alone — no database is touched — and throws
///     <see cref="EFToolkitAssertionException" /> with what was expected and what the model has.
/// </remarks>
public static class DbContextModelAssertions
{
    /// <summary>Asserts that the model matches the latest migration's snapshot.</summary>
    /// <param name="context">The context.</param>
    /// <exception cref="EFToolkitAssertionException">
    ///     The model has changed since the last migration — or there are no migrations at all. The
    ///     message lists the operations a new migration would contain.
    /// </exception>
    /// <remarks>
    ///     The test that catches a model change merged without its migration: run it in CI and the
    ///     forgotten <c>dotnet ef migrations add</c> fails the build instead of the deployment.
    /// </remarks>
    public static void ShouldHaveNoPendingModelChanges(this DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Database.HasPendingModelChanges())
        {
            return;
        }

        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var contextName = context.GetType().Name;

        if (migrationsAssembly.ModelSnapshot is null)
        {
            throw new EFToolkitAssertionException(
                $"'{contextName}' has no migrations: no model snapshot was found in "
                + $"'{migrationsAssembly.Assembly.GetName().Name}'. "
                + "Add the first one with: dotnet ef migrations add InitialCreate");
        }

        var message = new StringBuilder(
            $"'{contextName}' has model changes that are not in a migration.");

        try
        {
            var operations = Differences(context, migrationsAssembly);
            if (operations.Count > 0)
            {
                message.AppendLine().Append("A new migration would contain:");
                foreach (var operation in operations)
                {
                    message.AppendLine().Append("  - ").Append(Describe(operation));
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The diff is only there to improve the message; EF has already answered the question.
        }

        message.AppendLine().Append("Add a migration with: dotnet ef migrations add <Name>");
        throw new EFToolkitAssertionException(message.ToString());
    }

    /// <summary>Asserts that <typeparamref name="TEntity" /> maps to the table <paramref name="table" />.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="table">The expected table name.</param>
    /// <param name="schema">The expected schema; <see langword="null" /> leaves the schema unchecked.</param>
    /// <exception cref="EFToolkitAssertionException">It maps elsewhere, or to no table.</exception>
    public static void ShouldMapTable<TEntity>(this DbContext context, string table, string? schema = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var entityType = FindEntityType<TEntity>(context);
        var actualTable = entityType.GetTableName();
        var actualSchema = entityType.GetSchema();
        var expected = Qualified(schema, table);

        if (actualTable is null)
        {
            var view = entityType.GetViewName();
            throw new EFToolkitAssertionException(
                $"Expected '{typeof(TEntity).Name}' to map to table '{expected}', but it maps to "
                + (view is not null ? $"the view '{Qualified(entityType.GetViewSchema(), view)}'." : "no table."));
        }

        if (!string.Equals(actualTable, table, StringComparison.Ordinal)
            || (schema is not null && !string.Equals(actualSchema, schema, StringComparison.Ordinal)))
        {
            throw new EFToolkitAssertionException(
                $"Expected '{typeof(TEntity).Name}' to map to table '{expected}', "
                + $"but it maps to '{Qualified(actualSchema, actualTable)}'.");
        }
    }

    /// <summary>Asserts that <typeparamref name="TEntity" /> has an index over exactly these properties, in this order.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="properties">
    ///     The properties: <c>x => x.Email</c>, or <c>x => new { x.TenantId, x.Email }</c> for a
    ///     composite index.
    /// </param>
    /// <param name="unique">
    ///     Whether the index must be unique (<see langword="true" />), must not be
    ///     (<see langword="false" />), or either (<see langword="null" />, the default).
    /// </param>
    /// <exception cref="EFToolkitAssertionException">No such index, or its uniqueness differs.</exception>
    /// <remarks>
    ///     Order matters, because it does to the database: an index on <c>(A, B)</c> serves a filter
    ///     on <c>A</c> alone and one on <c>(B, A)</c> does not. Primary keys are not indexes here.
    /// </remarks>
    public static void ShouldHaveIndex<TEntity>(
        this DbContext context,
        Expression<Func<TEntity, object?>> properties,
        bool? unique = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(properties);

        var entityType = FindEntityType<TEntity>(context);
        var names = PropertyNames(properties);
        var described = $"({string.Join(", ", names)})";

        var index = entityType.GetIndexes()
            .FirstOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(names, StringComparer.Ordinal));

        if (index is null)
        {
            var existing = entityType.GetIndexes().ToList();
            throw new EFToolkitAssertionException(
                $"Expected '{typeof(TEntity).Name}' to have an index on {described}, but "
                + (existing.Count == 0
                    ? "it has no indexes."
                    : "its indexes are:" + string.Concat(existing.Select(i => Environment.NewLine + "  - " + Describe(i)))));
        }

        if (unique is { } expectedUnique && index.IsUnique != expectedUnique)
        {
            throw new EFToolkitAssertionException(
                $"Expected the index on '{typeof(TEntity).Name}' {described} to be "
                + $"{(expectedUnique ? "unique" : "non-unique")}, but it is {(index.IsUnique ? "unique" : "non-unique")}.");
        }
    }

    private static IEntityType FindEntityType<TEntity>(DbContext context)
        => context.Model.FindEntityType(typeof(TEntity))
            ?? throw new EFToolkitAssertionException(
                $"Expected '{typeof(TEntity).Name}' to be an entity type in '{context.GetType().Name}', but it is not mapped.");

    /// <remarks>The same comparison <c>HasPendingModelChanges</c> makes, kept for its answer rather than a bool.</remarks>
    private static IReadOnlyList<MigrationOperation> Differences(DbContext context, IMigrationsAssembly migrationsAssembly)
    {
        // The snapshot's model is built once and cached, and HasPendingModelChanges has just
        // finalized it — finalizing again would throw, so only a model still mutable is finalized.
        var snapshot = migrationsAssembly.ModelSnapshot?.Model;
        if (snapshot is IMutableModel mutable and not Annotatable { IsReadOnly: true })
        {
            snapshot = mutable.FinalizeModel();
        }

        if (snapshot is not null)
        {
            snapshot = context.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        }

        return context.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshot?.GetRelationalModel(),
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel());
    }

    /// <remarks>
    ///     Read by name off whichever of <c>Schema</c>, <c>Table</c> and <c>Name</c> the operation has,
    ///     so every operation type — including providers' own — gets a description without a case
    ///     of its own here.
    /// </remarks>
    internal static string Describe(MigrationOperation operation)
    {
        var type = operation.GetType();
        var kind = type.Name.EndsWith("Operation", StringComparison.Ordinal) ? type.Name[..^"Operation".Length] : type.Name;

        string? Read(string name)
            => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(operation) as string;

        var schema = Read("Schema");
        var table = Read("Table");
        var name = Read("Name");

        var target = (table, name) switch
        {
            (not null, not null) => $"{Qualified(schema, table)}.{name}",
            (not null, null) => Qualified(schema, table),
            (null, not null) => Qualified(schema, name),
            _ => null,
        };

        return target is null ? kind : $"{kind} {target}";
    }

    private static string Describe(IIndex index)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{(index.IsUnique ? "unique " : "")}{index.GetDatabaseName() ?? index.Name ?? "(unnamed)"} ({string.Join(", ", index.Properties.Select(p => p.Name))})");

    private static string Qualified(string? schema, string name) => schema is null ? name : $"{schema}.{name}";

    private static List<string> PropertyNames(LambdaExpression expression)
    {
        var body = StripConvert(expression.Body);

        if (body is NewExpression { Arguments: var arguments })
        {
            return [.. arguments.Select(a => MemberName(StripConvert(a), expression))];
        }

        return [MemberName(body, expression)];
    }

    private static string MemberName(Expression expression, LambdaExpression properties)
        => expression is MemberExpression { Expression: ParameterExpression, Member: var member }
            ? member.Name
            : throw new ArgumentException(
                $"'{properties}' must select properties of the entity: x => x.A, or x => new {{ x.A, x.B }}.",
                nameof(properties));

    private static Expression StripConvert(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? StripConvert(unary.Operand)
            : expression;
}
