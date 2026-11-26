namespace EFToolkit.Sql.Sessions;

/// <summary>One session-context key and how to resolve its value when a connection opens.</summary>
internal sealed class SessionContextValue
{
    private readonly Func<IServiceProvider, object?>? _fromServices;
    private readonly Func<object?>? _fromDelegate;

    public SessionContextValue(string key, Func<IServiceProvider, object?> fromServices)
    {
        Key = key;
        _fromServices = fromServices;
    }

    public SessionContextValue(string key, Func<object?> fromDelegate)
    {
        Key = key;
        _fromDelegate = fromDelegate;
    }

    public string Key { get; }

    public bool RequiresServices => _fromServices is not null;

    public object? Resolve(IServiceProvider? services)
    {
        if (_fromDelegate is not null)
        {
            return _fromDelegate();
        }

        if (services is null)
        {
            throw new InvalidOperationException(
                $"The session-context value for '{Key}' is resolved from the application's services, "
                + "but this context has no application service provider. Register the context with "
                + "AddDbContext, call UseApplicationServiceProvider on the options, or use the "
                + "Set(key, () => value) overload.");
        }

        return _fromServices!(services);
    }
}
