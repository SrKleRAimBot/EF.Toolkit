using System.Data.Common;
using EFToolkit.Sql.Dialects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Sql.Sessions;

/// <summary>Applies the configured session-context values every time EF opens a connection.</summary>
/// <remarks>
///     <para>
///         Every open, not once per context: connection pools reset session state when a connection is
///         returned (Npgsql's <c>DISCARD ALL</c>, SQL Server's <c>sp_reset_connection</c>), and EF opens
///         and closes the connection around each command unless told otherwise. A value set once would
///         be gone by the next query — and with row-level security failing closed, that shows up as
///         queries that suddenly return nothing.
///     </para>
///     <para>
///         Stateless, so one instance serves every context; the values come from the opening context's
///         own options.
///     </para>
/// </remarks>
internal sealed class SessionContextInterceptor : DbConnectionInterceptor
{
    public static readonly SessionContextInterceptor Instance = new();

    private SessionContextInterceptor()
    {
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(eventData);

        if (Prepare(eventData) is not { } plan)
        {
            return;
        }

        using var command = SessionContextSql.CreateCommand(connection, plan.Dialect, plan.Values);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(eventData);

        if (Prepare(eventData) is not { } plan)
        {
            return;
        }

        var command = SessionContextSql.CreateCommand(connection, plan.Dialect, plan.Values);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static (SqlDialect Dialect, IReadOnlyList<(string Key, object? Value)> Values)? Prepare(
        ConnectionEndEventData eventData)
    {
        var context = eventData.Context
            ?? throw new InvalidOperationException(
                "EF.Toolkit.Sql's session-context interceptor saw a connection opened outside a DbContext.");

        var options = context.GetService<IDbContextOptions>();
        var extension = options.FindExtension<SessionContextOptionsExtension>();
        if (extension is null || extension.Values.Count == 0)
        {
            return null;
        }

        var dialect = SqlDialectDetector.FromProviderName(context.Database.ProviderName);
        if (dialect == SqlDialect.Sqlite)
        {
            throw SessionContextSql.NotOnSqlite();
        }

        var services = options.FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
        var values = extension.Values.Select(v => (v.Key, v.Resolve(services))).ToList();

        return (dialect, values);
    }
}
