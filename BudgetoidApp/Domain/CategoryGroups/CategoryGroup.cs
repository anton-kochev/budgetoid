using Domain.Common;

namespace Domain.CategoryGroups;

public sealed class CategoryGroup
{
    private CategoryGroup()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public int Position { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static CategoryGroup Create(
        Guid userId,
        string name,
        string? description,
        int position,
        DateTime createdAtUtc)
    {
        ValidateOrThrow(userId, name, description, position);

        return new CategoryGroup
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name.Trim(),
            Description = NormalizeDescription(description),
            Position = position,
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void Update(string name, string? description)
    {
        ValidateOrThrow(UserId, name, description, Position);

        Name = name.Trim();
        Description = NormalizeDescription(description);
    }

    public void SetPosition(int position)
    {
        if (position < 0)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(Position)] = ["Position must be zero or greater."],
            });
        }

        Position = position;
    }

    private static void ValidateOrThrow(
        Guid userId,
        string? name,
        string? description,
        int position)
    {
        var errors = new Dictionary<string, string[]>();
        string trimmedName = name?.Trim() ?? string.Empty;

        if (userId == Guid.Empty)
        {
            errors[nameof(UserId)] = ["User id is required."];
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
