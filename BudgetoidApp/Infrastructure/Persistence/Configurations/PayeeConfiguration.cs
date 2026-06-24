using Domain.Payees;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class PayeeConfiguration : IEntityTypeConfiguration<Payee>
{
    public void Configure(EntityTypeBuilder<Payee> builder)
    {
        builder.ToTable("payees");
        builder.HasKey(payee => payee.Id);

        builder.Property(payee => payee.Id).HasColumnName("id");
        builder.Property(payee => payee.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(payee => payee.Name).HasColumnName("name").HasMaxLength(200).IsRequired()
            .UseCollation("case_insensitive");
        builder.Property(payee => payee.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // Unique per user, case-insensitively: the name column's case_insensitive collation makes
        // this plain index fold case in PostgreSQL, so "Starbucks" and "starbucks" collide.
        builder.HasIndex(payee => new { payee.UserId, payee.Name }).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(payee => payee.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
