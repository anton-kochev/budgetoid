using Domain.Budgets;
using Domain.CategoryGroups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Persistence.Configurations;

public sealed class CategoryGroupConfiguration : IEntityTypeConfiguration<CategoryGroup>
{
    // Pinned to the name EF's convention already produces, so the schema does not move:
    // CategoryGroupRepository matches it against PostgresException.ConstraintName so a 23505 from an
    // unrelated tracked row cannot come back as a duplicate group name nobody else holds.
    public const string NameIndexName = "IX_category_groups_budget_id_name";

    public void Configure(EntityTypeBuilder<CategoryGroup> builder)
    {
        builder.ToTable("category_groups", table =>
            table.HasCheckConstraint("CK_category_groups_position", "position >= 0"));
        builder.HasKey(categoryGroup => categoryGroup.Id);
        builder.HasAlternateKey(categoryGroup => new { categoryGroup.Id, categoryGroup.BudgetId });

        builder.Property(categoryGroup => categoryGroup.Id).HasColumnName("id");
        builder.Property(categoryGroup => categoryGroup.BudgetId).HasColumnName("budget_id").IsRequired();
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

        builder.HasIndex(categoryGroup => new { categoryGroup.BudgetId, categoryGroup.Name })
            .IsUnique()
            .HasDatabaseName(NameIndexName);
        builder.HasIndex(categoryGroup => new { categoryGroup.BudgetId, categoryGroup.Position });

        builder.HasOne<Budget>()
            .WithMany()
            .HasForeignKey(categoryGroup => categoryGroup.BudgetId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
