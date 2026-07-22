using Domain.CategoryGroups;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CategoryGroupConfiguration : IEntityTypeConfiguration<CategoryGroup>
{
    public void Configure(EntityTypeBuilder<CategoryGroup> builder)
    {
        builder.ToTable("category_groups", table =>
            table.HasCheckConstraint("CK_category_groups_position", "position >= 0"));
        builder.HasKey(categoryGroup => categoryGroup.Id);
        builder.HasAlternateKey(categoryGroup => new { categoryGroup.Id, categoryGroup.UserId });

        builder.Property(categoryGroup => categoryGroup.Id).HasColumnName("id");
        builder.Property(categoryGroup => categoryGroup.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(categoryGroup => categoryGroup.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired()
            .UseCollation("case_insensitive");
        builder.Property(categoryGroup => categoryGroup.Description)
            .HasColumnName("description")
            .HasMaxLength(500);
        builder.Property(categoryGroup => categoryGroup.Position)
            .HasColumnName("position")
            .IsRequired();
        builder.Property(categoryGroup => categoryGroup.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(categoryGroup => new { categoryGroup.UserId, categoryGroup.Name })
            .IsUnique();
        builder.HasIndex(categoryGroup => new { categoryGroup.UserId, categoryGroup.Position });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(categoryGroup => categoryGroup.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
