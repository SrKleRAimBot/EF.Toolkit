using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing;

/// <summary>Records the SQL EF executes while it is active, and asserts on it.</summary>
/// <remarks>
///     <para>
///         Recording is scoped twice over. In time, by the capture's lifetime: only commands between
///         creation and <see cref="Dispose" /> are kept. And in flow, by the asynchronous call chain
///         that created it: a capture sees the commands run on its own flow and the tasks that flow
///         starts, never those of a test running alongside it — even one using the same database.
///     </para>
///     <para>
///         Commands are seen through <see cref="SqlCaptureInterceptor" />, which has to be on the
///         context's options. The test databases always register it; elsewhere call
///         <c>UseSqlCapture()</c>.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     using var sql = context.CaptureSql();
///     var orders = await context.Orders.Include(o => o.Lines).ToListAsync();
///     sql.ShouldHaveExecuted(1);
///     </code>
/// </example>
public sealed class SqlCapture : IDisposable
{
    private static readonly AsyncLocal<ImmutableList<SqlCapture>?> Active = new();

    private readonly Func<DbContext?, SqlCaptureInterceptor, bool> _accepts;
    private readonly List<CapturedCommand> _commands = [];
    private readonly Lock _lock = new();
    private volatile bool _disposed;

    private SqlCapture(Func<DbContext?, SqlCaptureInterceptor, bool> accepts)
    {
        _accepts = accepts;
        Active.Value = (Active.Value ?? []).Add(this);
    }

    /// <summary>The commands recorded so far, in execution order.</summary>
    public IReadOnlyList<CapturedCommand> Commands
    {
        get
        {
            lock (_lock)
            {
                return [.. _commands];
            }
        }
    }

    /// <summary>The number of commands recorded so far.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _commands.Count;
            }
        }
    }

    /// <summary>
    ///     Starts capturing every command on this flow, from any context that has
    ///     <see cref="SqlCaptureInterceptor" /> registered.
    /// </summary>
    /// <returns>The capture. Dispose it to stop recording.</returns>
    public static SqlCapture Start() => new((_, _) => true);

    internal static SqlCapture ForContext(DbContext context)
        => new((c, _) => ReferenceEquals(c, context));

    internal static SqlCapture ForInterceptor(SqlCaptureInterceptor interceptor)
        => new((_, i) => ReferenceEquals(i, interceptor));

    /// <summary>Discards everything recorded so far and keeps recording.</summary>
    /// <remarks>For the arrange-then-act test: seed, clear, then assert on the act alone.</remarks>
    public void Clear()
    {
        lock (_lock)
        {
            _commands.Clear();
        }
    }

    /// <summary>Asserts that exactly <paramref name="expected" /> commands were executed.</summary>
    /// <param name="expected">The expected number of commands.</param>
    /// <returns>This capture, for chaining.</returns>
    /// <exception cref="EFToolkitAssertionException">A different number was executed.</exception>
    public SqlCapture ShouldHaveExecuted(int expected)
    {
        var commands = Commands;
        if (commands.Count != expected)
        {
            throw new EFToolkitAssertionException(
                $"Expected {Plural(expected)} to be executed, but {Plural(commands.Count, "was", "were")}."
                + Describe(commands));
        }

        return this;
    }

    /// <summary>Asserts that no more than <paramref name="maximum" /> commands were executed.</summary>
    /// <param name="maximum">The most commands allowed — the guard against an N+1 query.</param>
    /// <returns>This capture, for chaining.</returns>
    /// <exception cref="EFToolkitAssertionException">More were executed.</exception>
    public SqlCapture ShouldHaveExecutedAtMost(int maximum)
    {
        var commands = Commands;
        if (commands.Count > maximum)
        {
            throw new EFToolkitAssertionException(
                $"Expected at most {Plural(maximum)} to be executed, but {Plural(commands.Count, "was", "were")}."
                + Describe(commands));
        }

        return this;
    }

    /// <summary>Asserts that at least one command's text contains <paramref name="fragment" />.</summary>
    /// <param name="fragment">The text to look for.</param>
    /// <param name="comparison">How to compare. Case-insensitive by default, as SQL keywords are.</param>
    /// <returns>This capture, for chaining.</returns>
    /// <exception cref="EFToolkitAssertionException">No command contains it.</exception>
    public SqlCapture ShouldContain(string fragment, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        var commands = Commands;
        if (!commands.Any(c => c.CommandText.Contains(fragment, comparison)))
        {
            throw new EFToolkitAssertionException(
                $"Expected a command containing \"{fragment}\", but none of the {Plural(commands.Count)} did."
                + Describe(commands));
        }

        return this;
    }

    /// <summary>Asserts that no command's text contains <paramref name="fragment" />.</summary>
    /// <param name="fragment">The text that must not appear.</param>
    /// <param name="comparison">How to compare. Case-insensitive by default, as SQL keywords are.</param>
    /// <returns>This capture, for chaining.</returns>
    /// <exception cref="EFToolkitAssertionException">A command contains it.</exception>
    public SqlCapture ShouldNotContain(string fragment, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        var offending = Commands.Where(c => c.CommandText.Contains(fragment, comparison)).ToList();
        if (offending.Count > 0)
        {
            throw new EFToolkitAssertionException(
                $"Expected no command containing \"{fragment}\", but {Plural(offending.Count, "did", "did")}."
                + Describe(offending));
        }

        return this;
    }

    /// <summary>Stops recording. What was recorded stays readable.</summary>
    public void Dispose()
    {
        _disposed = true;

        // Only removable from the flow that holds it; anywhere else, the flag alone stops recording.
        if (Active.Value is { } active && active.Contains(this))
        {
            Active.Value = active.Remove(this);
        }
    }

    /// <summary>Whether this flow has any capture started — the one read made while there is none.</summary>
    internal static bool AnyActive => Active.Value is { Count: > 0 };

    internal static void Record(DbContext? context, SqlCaptureInterceptor interceptor, Func<CapturedCommand> snapshot)
    {
        if (Active.Value is not { Count: > 0 } active)
        {
            return;
        }

        // Taken for the first capture that wants it, and shared by the rest.
        CapturedCommand? command = null;
        foreach (var capture in active)
        {
            if (!capture._disposed && capture._accepts(context, interceptor))
            {
                command ??= snapshot();
                lock (capture._lock)
                {
                    capture._commands.Add(command);
                }
            }
        }
    }

    private static string Plural(int count, string? singularVerb = null, string? pluralVerb = null)
    {
        var noun = count == 1 ? "1 command" : $"{count} commands";
        var verb = count == 1 ? singularVerb : pluralVerb;
        return verb is null ? noun : $"{noun} {verb}";
    }

    private static string Describe(IReadOnlyList<CapturedCommand> commands)
    {
        if (commands.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        for (var i = 0; i < commands.Count; i++)
        {
            builder.AppendLine().Append(CultureInfo.InvariantCulture, $"[{i + 1}] {commands[i].CommandText}");
        }

        return builder.ToString();
    }
}
