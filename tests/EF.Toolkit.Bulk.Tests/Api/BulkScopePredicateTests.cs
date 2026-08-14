using System.Linq.Expressions;
using EFToolkit.Bulk.Api;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Api;

/// <summary>
///     Translation of the <c>WithinScope</c> predicate that fences a synchronise's delete arm.
/// </summary>
/// <remarks>
///     Two properties matter here and are tested for directly. Values must never reach the SQL —
///     they are what a caller is most likely to take from outside the application — and an
///     expression the translator does not understand must be refused rather than dropped, because a
///     dropped condition widens a delete rather than narrowing it.
/// </remarks>
public class BulkScopePredicateTests
{
    [Fact]
    public void Translates_an_equality_against_a_captured_value()
    {
        var tenant = 42;
        var scope = Translate(r => r.TenantId == tenant);

        scope.Sql.ShouldBe("t.[TenantId] = @__efbulk_scope_0");
        scope.Parameters.ShouldBe([42]);
    }

    [Fact]
    public void Translates_a_conjunction_of_comparisons()
    {
        var cutoff = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var scope = Translate(r => r.TenantId == 7 && r.RecordedAt >= cutoff);

        scope.Sql.ShouldBe(
            "(t.[TenantId] = @__efbulk_scope_0 AND t.[RecordedAt] >= @__efbulk_scope_1)");
        scope.Parameters.ShouldBe([7, cutoff]);
    }

    [Fact]
    public void Turns_the_operator_when_the_property_is_on_the_right()
    {
        var scope = Translate(r => 100 < r.TenantId);

        // '100 < TenantId' is 'TenantId > 100'; emitting the operator unturned would fence the
        // opposite set of rows, which is the failure mode with no visible symptom.
        scope.Sql.ShouldBe("t.[TenantId] > @__efbulk_scope_0");
        scope.Parameters.ShouldBe([100]);
    }

    [Fact]
    public void Translates_null_comparisons_without_a_parameter()
    {
        Translate(r => r.Region == null).Sql.ShouldBe("t.[Region] IS NULL");

        var notNull = Translate(r => r.Region != null);
        notNull.Sql.ShouldBe("t.[Region] IS NOT NULL");
        notNull.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Translates_a_bare_boolean_property_and_its_negation()
    {
        Translate(r => r.Archived).Sql.ShouldBe("t.[Archived] = @__efbulk_scope_0");

        var negated = Translate(r => !r.Archived);
        negated.Sql.ShouldBe("t.[Archived] = @__efbulk_scope_0");
        negated.Parameters.ShouldBe([false]);
    }

    [Fact]
    public void Applies_the_column_s_value_converter()
    {
        var scope = Translate(r => r.Status == Status.Retired);

        // Stored as a string. Binding the enum's numeric value would compare against a column that
        // never holds one, so the scope would silently match nothing -- and a scope that matches
        // nothing deletes nothing, which looks like success.
        scope.Parameters.ShouldBe(["Retired"]);
    }

    [Fact]
    public void Refuses_an_expression_it_cannot_translate()
    {
        var thrown = Should.Throw<BulkNotSupportedException>(
            () => Translate(r => r.Region!.StartsWith("eu-")));

        thrown.Message.ShouldContain("WithinScope cannot translate");
        thrown.Message.ShouldContain("interpolated string");
    }

    [Fact]
    public void Refuses_a_disjunction()
    {
        // Not a translation gap so much as a safety one: 'OR' widens the fenced set, and every
        // reason to have a scope at all is a reason not to widen it by accident.
        Should.Throw<BulkNotSupportedException>(() => Translate(r => r.TenantId == 1 || r.Archived));
    }

    [Fact]
    public void Refuses_a_comparison_between_two_properties()
    {
        Should.Throw<BulkNotSupportedException>(() => Translate(r => r.TenantId == r.OwnerId))
            .Message.ShouldContain("another property");
    }

    [Fact]
    public void Refuses_an_unmapped_member()
    {
        Should.Throw<BulkNotSupportedException>(() => Translate(r => r.NotMapped == 1));
    }

    [Fact]
    public void Binds_the_holes_of_an_interpolated_scope()
    {
        var tenant = "acme";
        var scope = BulkScopePredicate.Translate(
            $"t.tenant = {tenant} AND t.archived_at IS NULL");

        // The value is a parameter, not part of the statement -- the whole point of the overload
        // being FormattableString rather than string.
        scope.Sql.ShouldBe("t.tenant = @__efbulk_scope_0 AND t.archived_at IS NULL");
        scope.Parameters.ShouldBe(["acme"]);
    }

    private static BulkScope Translate(Expression<Func<Record, bool>> predicate)
    {
        using var context = new ScopeContext();

        return BulkScopePredicate.Translate(
            context.Model.FindEntityType(typeof(Record))!,
            predicate,
            context.GetService<ISqlGenerationHelper>(),
            alias: "t");
    }

    private sealed class Record
    {
        public int Id { get; set; }
        public int TenantId { get; set; }
        public int OwnerId { get; set; }
        public string? Region { get; set; }
        public bool Archived { get; set; }
        public DateTime RecordedAt { get; set; }
        public Status Status { get; set; }

        public int NotMapped { get; set; }
    }

    private enum Status
    {
        Active,
        Retired
    }

    private sealed class ScopeContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            // Never opened: translation works from the model and the provider's quoting rules.
            => optionsBuilder.UseSqlServer("Server=none;Database=none");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Record>(b =>
            {
                b.Ignore(x => x.NotMapped);
                b.Property(x => x.Status).HasConversion<string>();
            });
    }
}
