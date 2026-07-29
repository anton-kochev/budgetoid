using Domain.Accounts;
using Domain.Budgets;
using Domain.Currencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    // Pinned to the name EF's convention already produces, so the schema does not move: AccountRepository
    // matches it against PostgresException.ConstraintName to decide whether a 23505 is the collision it
    // models. Declaring it here rather than as a literal in the repository keeps the two from drifting —
    // a name only the schema knows about stops the match and costs that 400 outright.
    public const string NameIndexName = "IX_accounts_budget_id_name";

    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", table =>
        {
            // A CHECK rather than a native PostgreSQL enum type: HasConversion<string>() already
            // stores the member name, so the check costs nothing extra, while a PG enum turns adding
            // a member into an ALTER TYPE dance. The honest price is that a new AccountType member
            // now needs a migration as well as a code change. That is acceptable because the enum is
            // fixed at compile time anyway, but the next person to add one deserves to learn it here
            // rather than from a failing deploy.
            table.HasCheckConstraint("CK_accounts_type", "type in ('Checking', 'Savings', 'Cash', 'CreditCard')");

            // Magnitude only, deliberately half of the domain rule. The column scale does not
            // enforce the decimal-places half: PostgreSQL rounds an over-precise value to the column
            // scale instead of rejecting it, storing 0.005 as 0.01 without raising anything.
            // Enforcement at the database means rejects, not coerces, so decimal places stay
            // domain-owned and no constraint here pretends to cover them.
            table.HasCheckConstraint("CK_accounts_opening_balance", "abs(opening_balance) <= 1000000000");
        });
        builder.HasKey(account => account.Id);
        builder.HasAlternateKey(account => new { account.Id, account.BudgetId });

        builder.Property(account => account.Id).HasColumnName("id");
        builder.Property(account => account.BudgetId).HasColumnName("budget_id").IsRequired();
        builder.Property(account => account.Name).HasColumnName("name").HasMaxLength(200).IsRequired()
            .UseCollation("case_insensitive");
        builder.Property(account => account.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(account => account.OpeningBalance).HasColumnName("opening_balance").HasColumnType("numeric(14,4)").IsRequired();
        builder.Property(account => account.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
        builder.Property(account => account.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasIndex(account => new { account.BudgetId, account.Name }).IsUnique().HasDatabaseName(NameIndexName);
        builder.HasIndex(account => account.CurrencyCode);

        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(account => account.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Currency>()
            .WithMany()
            .HasForeignKey(account => account.CurrencyCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
