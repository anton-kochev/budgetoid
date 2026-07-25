using Domain.Common;

namespace Domain.Budgets;

public sealed class Budget
{
    public const string DefaultName = "My Budget";

    private Budget()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? BaseCurrencyCode { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Budget Create(Guid userId, string name, DateTime createdAtUtc)
    {
        ValidateOrThrow(userId, name);

        return new Budget
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name.Trim(),
            CreatedAtUtc = createdAtUtc,
        };
    }

    public static Budget CreateDefault(Guid userId, DateTime createdAtUtc) =>
        Create(userId, DefaultName, createdAtUtc);

    private static void ValidateOrThrow(Guid userId, string? name)
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

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
