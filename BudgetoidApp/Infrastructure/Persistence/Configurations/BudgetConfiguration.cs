using Domain.Budgets;
using Domain.Currencies;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    // Pinned to the name EF's convention already produces, so the schema does not move: this index is
    // the only 23505 BudgetRepository.TryAddAsync is allowed to report as a lost race, because that is
    // the only one whose winner left a budget behind for provisioning to re-read.
    public const string UserNameIndexName = "IX_budgets_user_id_name";

    public void Configure(EntityTypeBuilder<Budget> builder)
    {
        builder.ToTable("budgets");
        builder.HasKey(budget => budget.Id);

        builder.Property(budget => budget.Id).HasColumnName("id");
        builder.Property(budget => budget.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(budget => budget.Name).HasColumnName("name").HasMaxLength(200)
            .UseCollation("case_insensitive");
        builder.Property(budget => budget.BaseCurrencyCode).HasColumnName("base_currency_code").HasMaxLength(3);
        builder.Property(budget => budget.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // Deliberately no unique index or key over user_id alone: the schema is multi-budget-ready
        // from day one, and "exactly one budget per user" is a release-scope property (no code path
        // creates a second one), not a schema invariant. The leading column of the composite index
        // below also serves WHERE user_id = ?, so no separate index on user_id is needed.
        //
        // NULLS NOT DISTINCT (PG15+) is what states the invariant: at most one unnamed budget per
        // user, plus any number of named ones. Without it PostgreSQL treats each NULL as distinct,
        // both racers in provisioning insert (userId, NULL), and a user silently ends up owning two
        // budgets. Keying race safety on the absence of a name is stronger than the literal it
        // replaces: collision used to require both racers to write the same string, and a constant
        // two callers must agree on can drift; nothing about "no name" can.
        builder.HasIndex(budget => new { budget.UserId, budget.Name }).IsUnique().AreNullsDistinct(false)
            .HasDatabaseName(UserNameIndexName);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(budget => budget.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Currency>()
            .WithMany()
            .HasForeignKey(budget => budget.BaseCurrencyCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
