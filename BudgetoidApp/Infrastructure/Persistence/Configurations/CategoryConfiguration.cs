using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
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
        builder.HasAlternateKey(category => new { category.Id, category.BudgetId });

        builder.Property(category => category.Id).HasColumnName("id");
        builder.Property(category => category.BudgetId).HasColumnName("budget_id").IsRequired();
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

        builder.HasIndex(category => new { category.BudgetId, category.Name }).IsUnique();
        // Deliberately group-scoped, not budget-scoped: groups are themselves budget-scoped, so
        // per-budget ordering holds transitively.
        builder.HasIndex(category => new { category.CategoryGroupId, category.Position });

        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(category => category.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite on purpose: a category can only join a group in its own budget, and no query
        // filter can enforce that on a write.
        builder.HasOne<CategoryGroup>()
            .WithMany()
            .HasForeignKey(category => new { category.CategoryGroupId, category.BudgetId })
            .HasPrincipalKey(categoryGroup => new { categoryGroup.Id, categoryGroup.BudgetId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
