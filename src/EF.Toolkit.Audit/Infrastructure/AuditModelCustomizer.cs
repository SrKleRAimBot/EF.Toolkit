using EFToolkit.Audit.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Audit.Infrastructure;

/// <summary>
///     Adds the audit table to the model after the application has finished building it.
/// </summary>
/// <remarks>
///     <para>
///         Installed by the provider package with
///         <c>optionsBuilder.ReplaceService&lt;IModelCustomizer, …&gt;()</c>, the same public
///         mechanism EF.Toolkit.Bulk uses for its batch factory. Deriving from
///         <see cref="RelationalModelCustomizer" /> and calling through keeps everything EF's own
///         customizer does — <c>OnModelCreating</c> included — so this only ever adds.
///     </para>
///     <para>
///         An application that replaces <see cref="IModelCustomizer" /> itself would displace this
///         one. That is unusual, and the symptom is unmistakable: there is no audit table.
///     </para>
/// </remarks>
public class AuditModelCustomizer(ModelCustomizerDependencies dependencies, AuditOptions options)
    : RelationalModelCustomizer(dependencies)
{
    /// <inheritdoc />
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        AuditModelBuilder.Apply(modelBuilder, options);
    }
}
