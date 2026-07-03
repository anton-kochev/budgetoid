using Domain.Accounts;
using Domain.Groups;
using Domain.Payees;
using Domain.Transactions;
using Domain.Users;
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
        builder.Property(transaction => transaction.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(transaction => transaction.AccountId).HasColumnName("account_id").IsRequired();
        builder.Property(transaction => transaction.Amount).HasColumnName("amount").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(transaction => transaction.Date).HasColumnName("date").HasColumnType("date").IsRequired();
        builder.Property(transaction => transaction.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(transaction => transaction.PayeeId).HasColumnName("payee_id");
        builder.Property(transaction => transaction.GroupId).HasColumnName("group_id");
        builder.Property(transaction => transaction.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasIndex(transaction => new { transaction.UserId, transaction.Date, transaction.CreatedAtUtc })
            .IsDescending(false, true, true);
        builder.HasIndex(transaction => transaction.PayeeId);
        builder.HasIndex(transaction => transaction.GroupId);
        builder.HasIndex(transaction => transaction.AccountId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(transaction => transaction.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(transaction => transaction.AccountId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<Payee>()
            .WithMany()
            .HasForeignKey(transaction => transaction.PayeeId)
            .OnDelete(DeleteBehavior.SetNull);

        // Restrict, not SetNull: a group with linked transactions must not be deletable. This is the
        // database backstop for the app-level HasTransactionsAsync guard in GroupRepository.
        builder.HasOne<Group>()
            .WithMany()
            .HasForeignKey(transaction => transaction.GroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
