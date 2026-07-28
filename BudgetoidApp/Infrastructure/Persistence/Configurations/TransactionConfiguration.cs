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
        builder.ToTable("transactions", table =>
            // Magnitude only: a numeric scale rounds an over-precise amount rather than rejecting
            // it, so the decimal-places half of the domain rule stays domain-owned.
            table.HasCheckConstraint("CK_transactions_amount", "abs(amount) <= 1000000000"));
        builder.HasKey(transaction => transaction.Id);

        builder.Property(transaction => transaction.Id).HasColumnName("id");
        builder.Property(transaction => transaction.BudgetId).HasColumnName("budget_id").IsRequired();
        builder.Property(transaction => transaction.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(transaction => transaction.Amount)
            .HasColumnName("amount")
            .HasColumnType("numeric(14,4)")
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

        // Restrict here while accounts, category groups, categories and payees cascade from the same
        // budget_id: the asymmetry is the rule, not an oversight. Recorded money movement is the one
        // thing a budget must not lose, so a budget holding any transaction cannot be deleted at all;
        // a budget with no movement was created by mistake and its structure follows it out.
        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(transaction => transaction.BudgetId)
            .OnDelete(DeleteBehavior.Restrict);

        // The three references below are composite on purpose: a single-column foreign key lets the
        // database accept a transaction pointing at another budget's row, and no query filter can
        // enforce tenancy on a write. The reference id leads so the foreign-key index EF derives is
        // also the more selective one. Optionality is left to property nullability - calling
        // IsRequired(false) on a composite relationship turns the whole reference optional, so the
        // budget half stops being an enforced part of it. PostgreSQL already skips a multi-column
        // foreign key check when any column is NULL (MATCH SIMPLE), which is what makes an absent
        // payee or category work without any extra configuration.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.AccountId, transaction.BudgetId })
            .HasPrincipalKey(account => new { account.Id, account.BudgetId })
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, not SetNull: ON DELETE SET NULL cannot coexist with a composite key containing
        // the NOT NULL budget_id column - PostgreSQL would attempt budget_id = NULL, and EF's
        // change tracker fails the same fixup on the non-nullable Guid. A referenced payee cannot be
        // deleted at all - the same guard accounts and categories have. Refusing forces an explicit
        // decision about historical rows instead of silently erasing the counterparty from past
        // transactions. No application code path deletes a payee (IPayeeRepository exposes only
        // GetOrCreateAsync), so nothing in the app depends on the delete succeeding.
        builder.HasOne<Payee>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.PayeeId, transaction.BudgetId })
            .HasPrincipalKey(payee => new { payee.Id, payee.BudgetId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(transaction => new { transaction.CategoryId, transaction.BudgetId })
            .HasPrincipalKey(category => new { category.Id, category.BudgetId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
