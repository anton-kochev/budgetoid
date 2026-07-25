using Domain.Budgets;
using Domain.Currencies;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class BudgetConfiguration : IEntityTypeConfiguration<Budget>
{
    public void Configure(EntityTypeBuilder<Budget> builder)
    {
        builder.ToTable("budgets");
        builder.HasKey(budget => budget.Id);

        builder.Property(budget => budget.Id).HasColumnName("id");
        builder.Property(budget => budget.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(budget => budget.Name).HasColumnName("name").HasMaxLength(200).IsRequired()
            .UseCollation("case_insensitive");
        builder.Property(budget => budget.BaseCurrencyCode).HasColumnName("base_currency_code").HasMaxLength(3);
        builder.Property(budget => budget.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // Deliberately no unique index or key over user_id alone: the schema is multi-budget-ready
        // from day one, and "exactly one budget per user" is a release-scope property (no code path
        // creates a second one), not a schema invariant. This composite index is still what makes
        // provisioning race-safe, because the default budget's name is the constant
        // Budget.DefaultName, so both racers insert (userId, "My Budget") and one gets a genuine
        // unique violation. Its leading column also serves WHERE user_id = ?, so no separate index
        // on user_id is needed.
        builder.HasIndex(budget => new { budget.UserId, budget.Name }).IsUnique();

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
