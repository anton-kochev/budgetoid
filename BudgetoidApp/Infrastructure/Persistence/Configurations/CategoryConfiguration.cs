using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("categories", table =>
            table.HasCheckConstraint("CK_categories_position", "position >= 0"));
        builder.HasKey(category => category.Id);

        builder.Property(category => category.Id).HasColumnName("id");
        builder.Property(category => category.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(category => category.CategoryGroupId)
            .HasColumnName("category_group_id")
            .IsRequired();
        builder.Property(category => category.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired()
            .UseCollation("case_insensitive");
        builder.Property(category => category.Description)
            .HasColumnName("description")
            .HasMaxLength(500);
        builder.Property(category => category.Position).HasColumnName("position").IsRequired();
        builder.Property(category => category.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.HasIndex(category => new { category.UserId, category.Name }).IsUnique();
        builder.HasIndex(category => new { category.CategoryGroupId, category.Position });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(category => category.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<CategoryGroup>()
            .WithMany()
            .HasForeignKey(category => new { category.CategoryGroupId, category.UserId })
            .HasPrincipalKey(categoryGroup => new { categoryGroup.Id, categoryGroup.UserId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
