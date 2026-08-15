using System.Globalization;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EFToolkit.Query.Configuration;

/// <summary>
///     Carries <see cref="QueryOptions" /> through <c>DbContextOptions</c> and registers them with
///     EF Core's internal service provider.
/// </summary>
/// <remarks>
///     The <c>IQueryContextFactory</c> replacement that makes ambient tracking scopes work is applied
///     by <c>UseQueryHelpers()</c> through <c>DbContextOptionsBuilder.ReplaceService</c> rather than
///     here, because a replacement is not a service registration and EF only honours it from the
///     options builder.
/// </remarks>
public sealed class QueryOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>Initializes a new instance carrying <see cref="QueryOptions.Default" />.</summary>
    public QueryOptionsExtension()
        : this(QueryOptions.Default)
    {
    }

    /// <summary>Initializes a new instance carrying <paramref name="options" />.</summary>
    /// <param name="options">The settings to carry.</param>
    public QueryOptionsExtension(QueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>The EF.Toolkit.Query settings configured for this context.</summary>
    public QueryOptions Options { get; }

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(Options);
    }

    /// <inheritdoc />
    /// <exception cref="QueryNotSupportedException">
    ///     The settings contradict each other in a way that would silently change what a caller asked
    ///     for.
    /// </exception>
    public void Validate(IDbContextOptions options)
    {
        if (Options.DefaultPageSize > Options.MaxPageSize)
        {
            throw new QueryNotSupportedException(
                $"DefaultPageSize ({Options.DefaultPageSize}) is larger than MaxPageSize "
                + $"({Options.MaxPageSize}), so every request that does not name a page size would be "
                + "clamped down to the ceiling and no caller would ever receive the configured "
                + "default. Raise MaxPageSize or lower DefaultPageSize.");
        }
    }

    private sealed class ExtensionInfo(QueryOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new QueryOptionsExtension Extension => (QueryOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment
        {
            get
            {
                var o = Extension.Options;
                return $"using EF.Toolkit.Query (defaultPageSize={o.DefaultPageSize}, "
                    + $"maxPageSize={o.MaxPageSize}, countStrategy={o.CountStrategy}, "
                    + $"trackingScopes={o.TrackingScopes}, checks={o.Diagnostics.Checks}) ";
            }
        }

        // EF caches one internal service provider per distinct hash, and QueryOptions is registered
        // as a singleton there, so every setting must contribute or two differently-configured
        // contexts would share the first one's options. QueryOptions is a record, so its structural
        // hash already covers the nested diagnostics settings.
        public override int GetServiceProviderHashCode() => Extension.Options.GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo o && Extension.Options == o.Extension.Options;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);

            var o = Extension.Options;
            var invariant = CultureInfo.InvariantCulture;
            debugInfo["EF.Toolkit.Query:DefaultPageSize"] = o.DefaultPageSize.ToString(invariant);
            debugInfo["EF.Toolkit.Query:MaxPageSize"] = o.MaxPageSize.ToString(invariant);
            debugInfo["EF.Toolkit.Query:Numbering"] = o.Numbering.ToString();
            debugInfo["EF.Toolkit.Query:CountStrategy"] = o.CountStrategy.ToString();
            debugInfo["EF.Toolkit.Query:MaxOffsetRows"] = o.MaxOffsetRows.ToString(invariant);
            debugInfo["EF.Toolkit.Query:BatchSize"] = o.BatchSize.ToString(invariant);
            debugInfo["EF.Toolkit.Query:MaxInClauseValues"] = o.MaxInClauseValues.ToString(invariant);
            debugInfo["EF.Toolkit.Query:TrackingScopes"] = o.TrackingScopes.ToString();
            debugInfo["EF.Toolkit.Query:Checks"] = o.Diagnostics.Checks.ToString();
            debugInfo["EF.Toolkit.Query:OnWarning"] = o.Diagnostics.Behavior.ToString();
        }
    }
}
