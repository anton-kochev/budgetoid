using Domain.Budgets;
using Domain.Payees;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class PayeeConfiguration : IEntityTypeConfiguration<Payee>
{
    public void Configure(EntityTypeBuilder<Payee> builder)
    {
        builder.ToTable("payees");
        builder.HasKey(payee => payee.Id);
        builder.HasAlternateKey(payee => new { payee.Id, payee.BudgetId });

        builder.Property(payee => payee.Id).HasColumnName("id");
        builder.Property(payee => payee.BudgetId).HasColumnName("budget_id").IsRequired();
        builder.Property(payee => payee.Name).HasColumnName("name").HasMaxLength(200).IsRequired()
            .UseCollation("case_insensitive");
        builder.Property(payee => payee.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // Unique per budget, case-insensitively: the name column's case_insensitive collation makes
        // this plain index fold case in PostgreSQL, so "Starbucks" and "starbucks" collide.
        builder.HasIndex(payee => new { payee.BudgetId, payee.Name }).IsUnique();

        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(payee => payee.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
