using EFToolkit.Sql.Dialects;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EFToolkit.Sql.Sessions;

/// <summary>Carries the session-context configuration through <c>DbContextOptions</c>.</summary>
/// <remarks>
///     <para>
///         The values are applied by an interceptor added alongside this extension, which reads them
///         back from the context's own options each time a connection opens. Nothing is registered in
///         EF's internal service provider, which is what lets the configuration hold per-request
///         delegates: EF caches internal service providers by the options that shape them, and a
///         delegate capturing a request's state would otherwise build a new provider per request until
///         EF refused to build more.
///     </para>
/// </remarks>
public sealed class SessionContextOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    internal SessionContextOptionsExtension(IReadOnlyList<SessionContextValue> values)
    {
        Values = values;
    }

    /// <summary>The configured keys, in the order they are applied.</summary>
    public IReadOnlyList<string> Keys => [.. Values.Select(v => v.Key)];

    internal IReadOnlyList<SessionContextValue> Values { get; }

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services) => ArgumentNullException.ThrowIfNull(services);

    /// <inheritdoc />
    /// <exception cref="SqlFeatureNotSupportedException">
    ///     The configured provider has no session context, or is not one EF.Toolkit.Sql knows.
    /// </exception>
    /// <remarks>
    ///     Checked here as well as on connection open so that a context configured for SQLite fails
    ///     when it is first used, not on the first query that happens to open a connection.
    /// </remarks>
    public void Validate(IDbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // EF names a provider after the assembly of its options extension, which is exactly the name
        // DatabaseFacade.ProviderName reports once the context exists.
        var provider = options.Extensions.FirstOrDefault(e => e.Info.IsDatabaseProvider);
        if (provider is null)
        {
            return;
        }

        var dialect = SqlDialectDetector.FromProviderName(provider.GetType().Assembly.GetName().Name);
        foreach (var value in Values)
        {
            SessionContextSql.ValidateKey(dialect, value.Key);
        }
    }

    private sealed class ExtensionInfo(SessionContextOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new SessionContextOptionsExtension Extension => (SessionContextOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment
            => $"using EF.Toolkit.Sql session context (keys={string.Join(",", Extension.Keys)}) ";

        // Registers nothing in the internal service provider, so no setting here may split it.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["EF.Toolkit.Sql:SessionContextKeys"] = string.Join(",", Extension.Keys);
        }
    }
}
