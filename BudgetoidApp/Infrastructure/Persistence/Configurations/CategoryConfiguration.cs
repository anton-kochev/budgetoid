using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    // Pinned to the name EF's convention already produces, so the schema does not move: CategoryRepository
    // matches it against PostgresException.ConstraintName so a 23505 raised elsewhere is not rendered
    // against the name field the user just typed.
    public const string NameIndexName = "IX_categories_budget_id_name";

    // The sharper of the two: categories carries two foreign keys that both raise 23503 — this one and
    // budget_id to budgets — so the name is the only thing that makes "Category group was not found."
    // a statement about the group rather than about whichever check PostgreSQL happened to reach first.
    // It is also the single inbound foreign key on category_groups, which is what lets
    // CategoryGroupRepository read a 23503 on delete as "this group still holds categories".
    public const string CategoryGroupForeignKeyName =
        "FK_categories_category_groups_category_group_id_budget_id";

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

        builder.HasIndex(category => new { category.BudgetId, category.Name }).IsUnique()
            .HasDatabaseName(NameIndexName);
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
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(CategoryGroupForeignKeyName);
    }
}
