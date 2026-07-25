using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.Payees;
using Domain.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions");
        builder.HasKey(transaction => transaction.Id);

        builder.Property(transaction => transaction.Id).HasColumnName("id");
        builder.Property(transaction => transaction.BudgetId).HasColumnName("budget_id").IsRequired();
        builder.Property(transaction => transaction.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(transaction => transaction.Amount)
            .HasColumnName("amount")
            .HasColumnType("numeric(14,2)")
            .IsRequired();
        builder.Property(transaction => transaction.Date)
            .HasColumnName("date")
            .HasColumnType("date")
            .IsRequired();
        builder.Property(transaction => transaction.Description)
            .HasColumnName("description")
            .HasMaxLength(500);
        builder.Property(transaction => transaction.PayeeId).HasColumnName("payee_id");
        builder.Property(transaction => transaction.CategoryId).HasColumnName("category_id");
        builder.Property(transaction => transaction.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(transaction => new
        {
            transaction.BudgetId,
            transaction.Date,
            transaction.CreatedAtUtc,
        })
            .IsDescending(false, true, true);
        builder.HasIndex(transaction => transaction.PayeeId);
        builder.HasIndex(transaction => transaction.CategoryId);
        builder.HasIndex(transaction => transaction.AccountId);

        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(transaction => transaction.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => transaction.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Payee>()
            .WithMany()
            .HasForeignKey(transaction => transaction.PayeeId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(transaction => transaction.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
