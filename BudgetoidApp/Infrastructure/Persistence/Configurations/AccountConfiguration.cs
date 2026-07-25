using Domain.Accounts;
using Domain.Budgets;
using Domain.Currencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts");
        builder.HasKey(account => account.Id);

        builder.Property(account => account.Id).HasColumnName("id");
        builder.Property(account => account.BudgetId).HasColumnName("budget_id").IsRequired();
        builder.Property(account => account.Name).HasColumnName("name").HasMaxLength(200).IsRequired()
            .UseCollation("case_insensitive");
        builder.Property(account => account.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(account => account.OpeningBalance).HasColumnName("opening_balance").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(account => account.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
        builder.Property(account => account.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasIndex(account => new { account.BudgetId, account.Name }).IsUnique();
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
