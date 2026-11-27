namespace EFToolkit.Sql.Sessions;

/// <summary>Configures the session-context values applied on every connection open.</summary>
/// <remarks>
///     Values are resolved each time a connection opens, not when the context is configured, so a
///     delegate that reads the current request's tenant sees the request that is actually running.
/// </remarks>
public sealed class SessionContextBuilder
{
    private readonly List<SessionContextValue> _values;

    internal SessionContextBuilder(IEnumerable<SessionContextValue> existing)
    {
        _values = [.. existing];
    }

    internal IReadOnlyList<SessionContextValue> Values => _values;

    /// <summary>Sets <paramref name="key" /> from the application's services on every connection open.</summary>
    /// <param name="key">
    ///     The session-context key. On PostgreSQL a dotted custom setting name such as
    ///     <c>app.tenant_id</c>; on MySQL a user variable name, without <c>@</c>.
    /// </param>
    /// <param name="value">
    ///     Resolves the value from the context's application service provider — the request scope's,
    ///     under <c>AddDbContext</c>. A <see langword="null" /> result clears the value.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    ///     Under <c>AddDbContextPool</c> the options, and with them the service provider, are shared by
    ///     every pooled context and come from the root provider, where scoped services cannot be resolved.
    ///     Use the <see cref="Set(string, Func{object})" /> overload with an ambient source of the tenant
    ///     there.
    /// </remarks>
    public SessionContextBuilder Set(string key, Func<IServiceProvider, object?> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        return Add(new SessionContextValue(key, value));
    }

    /// <summary>Sets <paramref name="key" /> from a delegate on every connection open.</summary>
    /// <param name="key">The session-context key; see <see cref="Set(string, Func{IServiceProvider, object})" />.</param>
    /// <param name="value">Produces the value. A <see langword="null" /> result clears the value.</param>
    /// <returns>This builder, for chaining.</returns>
    public SessionContextBuilder Set(string key, Func<object?> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        return Add(new SessionContextValue(key, value));
    }

    private SessionContextBuilder Add(SessionContextValue value)
    {
        // A key set twice keeps the last value, in its original position, so a second
        // UseSessionContext call can override a default configured by the first. Keys are compared
        // ignoring case, as PostgreSQL compares setting names and MySQL user variables: kept apart by
        // spelling, app.tenant_id and APP.TENANT_ID would both be sent, and whichever ran last would
        // decide the tenant whatever was set last. (SQL Server keeps keys that differ only in case
        // apart; here they are one key on every engine.)
        var index = _values.FindIndex(v => string.Equals(v.Key, value.Key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _values[index] = value;
        }
        else
        {
            _values.Add(value);
        }

        return this;
    }
}
