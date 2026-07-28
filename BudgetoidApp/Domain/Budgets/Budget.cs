using Domain.Common;

namespace Domain.Budgets;

public sealed class Budget
{
    private Budget()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>
    /// The name the user gave this budget, or <see langword="null"/> for the budget they never asked
    /// for. What a client shows in place of a missing name is presentation, so it stays in the
    /// client — the domain has no display string to keep in sync with it.
    /// </summary>
    public string? Name { get; private set; }

    public string? BaseCurrencyCode { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Budget Create(Guid userId, string name, DateTime createdAtUtc)
    {
        ValidateOrThrow(userId, name, nameRequired: true);

        return new Budget
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name.Trim(),
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Creates the nameless budget a user is provisioned with. This is the only path that produces a
    /// budget without a name; naming one is an explicit act, so it goes through <see cref="Create"/>.
    /// </summary>
    public static Budget CreateDefault(Guid userId, DateTime createdAtUtc)
    {
        ValidateOrThrow(userId, name: null, nameRequired: false);

        return new Budget
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = null,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Runs the owner rule for every creation path and the name rules only where a name is expected.
    /// The two paths share this method rather than restating the owner rule, so an ownerless budget
    /// cannot become creatable through one factory alone.
    /// </summary>
    private static void ValidateOrThrow(Guid userId, string? name, bool nameRequired)
    {
        var errors = new Dictionary<string, string[]>();

        if (userId == Guid.Empty)
        {
            errors[nameof(UserId)] = ["User id is required."];
        }

        if (nameRequired)
        {
            string trimmedName = name?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                errors[nameof(Name)] = ["Name is required."];
            }
            else if (trimmedName.Length > 200)
            {
                errors[nameof(Name)] = ["Name must be 200 characters or fewer."];
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
