using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EFToolkit.Testing;

/// <summary>Hands every command EF executes to the <see cref="SqlCapture" />s active on its flow.</summary>
/// <remarks>
///     Interceptors can only be registered while the options are built, which is long before a test
///     knows it wants to capture anything. So this is registered unconditionally and does nothing
///     until a capture is started: with none active, each command costs one <see cref="AsyncLocal{T}" />
///     read.
/// </remarks>
public sealed class SqlCaptureInterceptor : DbCommandInterceptor
{
    /// <summary>The instance <c>UseSqlCapture()</c> registers.</summary>
    public static SqlCaptureInterceptor Shared { get; } = new();

    /// <inheritdoc />
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Record(command, eventData, true);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, true);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Record(command, eventData, true);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, true);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Record(command, eventData, true);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, true);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => Record(command, eventData, false);

    /// <inheritdoc />
    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, false);
        return Task.CompletedTask;
    }

    private void Record(DbCommand command, CommandEndEventData eventData, bool succeeded)
        => SqlCapture.Record(eventData.Context, this, Capture(command, eventData, succeeded));

    private static CapturedCommand Capture(DbCommand command, CommandEndEventData eventData, bool succeeded)
    {
        // Copied now: the command object is reused and its parameters rebound by the next execution.
        var parameters = command.Parameters
            .Cast<DbParameter>()
            .Select(p => new CapturedParameter(
                p.ParameterName,
                p.Value is DBNull ? null : p.Value,
                p.DbType,
                p.Direction))
            .ToList();

        var kind = eventData.ExecuteMethod switch
        {
            DbCommandMethod.ExecuteNonQuery => SqlCommandKind.NonQuery,
            DbCommandMethod.ExecuteScalar => SqlCommandKind.Scalar,
            _ => SqlCommandKind.Reader,
        };

        return new CapturedCommand(command.CommandText, parameters, kind, eventData.Duration, succeeded);
    }
}
