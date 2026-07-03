using Domain.Groups;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.ToTable("groups");
        builder.HasKey(group => group.Id);

        builder.Property(group => group.Id).HasColumnName("id");
        builder.Property(group => group.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(group => group.Name).HasColumnName("name").HasMaxLength(200).IsRequired()
            .UseCollation("case_insensitive");
        builder.Property(group => group.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(group => group.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // Unique per user, case-insensitively: the name column's case_insensitive collation makes
        // this plain index fold case in PostgreSQL, so "Groceries" and "groceries" collide.
        builder.HasIndex(group => new { group.UserId, group.Name }).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(group => group.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
