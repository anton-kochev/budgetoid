using Domain.Common;

namespace Domain.Categories;

public sealed class Category
{
    private Category()
    {
    }

    public Guid Id { get; private set; }
    public Guid BudgetId { get; private set; }
    public Guid CategoryGroupId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public int Position { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Category Create(
        Guid budgetId,
        Guid categoryGroupId,
        string name,
        string? description,
        int position,
        DateTime createdAtUtc)
    {
        ValidateOrThrow(budgetId, categoryGroupId, name, description, position);

        return new Category
        {
            Id = Guid.CreateVersion7(),
            BudgetId = budgetId,
            CategoryGroupId = categoryGroupId,
            Name = name.Trim(),
            Description = NormalizeDescription(description),
            Position = position,
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void Update(string name, string? description)
    {
        ValidateOrThrow(BudgetId, CategoryGroupId, name, description, Position);

        Name = name.Trim();
        Description = NormalizeDescription(description);
    }

    public void Place(Guid categoryGroupId, int position)
    {
        var errors = new Dictionary<string, string[]>();

        if (categoryGroupId == Guid.Empty)
        {
            errors[nameof(CategoryGroupId)] = ["Category group id is required."];
        }

        if (position < 0)
        {
            errors[nameof(Position)] = ["Position must be zero or greater."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        CategoryGroupId = categoryGroupId;
        Position = position;
    }

    private static void ValidateOrThrow(
        Guid budgetId,
        Guid categoryGroupId,
        string? name,
        string? description,
        int position)
    {
        var errors = new Dictionary<string, string[]>();
        string trimmedName = name?.Trim() ?? string.Empty;

        if (budgetId == Guid.Empty)
        {
            errors[nameof(BudgetId)] = ["Budget id is required."];
        }

        if (categoryGroupId == Guid.Empty)
        {
            errors[nameof(CategoryGroupId)] = ["Category group id is required."];
        }

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            errors[nameof(Name)] = ["Name is required."];
        }
        else if (trimmedName.Length > 200)
        {
            errors[nameof(Name)] = ["Name must be 200 characters or fewer."];
        }

        if (NormalizeDescription(description) is { Length: > 500 })
        {
            errors[nameof(Description)] = ["Description must be 500 characters or fewer."];
        }

        if (position < 0)
        {
            errors[nameof(Position)] = ["Position must be zero or greater."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    private static string? NormalizeDescription(string? description)
    {
        string? trimmed = description?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
