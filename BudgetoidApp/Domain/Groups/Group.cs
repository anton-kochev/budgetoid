using Domain.Common;

namespace Domain.Groups;

public sealed class Group
{
    private Group()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Group Create(Guid userId, string name, string? description, DateTime createdAtUtc)
    {
        ValidateOrThrow(userId, name, description);

        return new Group
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name.Trim(),
            Description = NormalizeDescription(description),
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void Update(string name, string? description)
    {
        ValidateOrThrow(UserId, name, description);

        Name = name.Trim();
        Description = NormalizeDescription(description);
    }

    private static void ValidateOrThrow(Guid userId, string? name, string? description)
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

        // Description is optional: a blank value becomes null. Only the length cap is enforced.
        string? normalizedDescription = NormalizeDescription(description);
        if (normalizedDescription is { Length: > 500 })
        {
            errors[nameof(Description)] = ["Description must be 500 characters or fewer."];
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
