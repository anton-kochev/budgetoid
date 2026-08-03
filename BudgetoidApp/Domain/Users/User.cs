using Domain.Common;

namespace Domain.Users;

public sealed class User
{
    public const int MaxDisplayNameLength = 200;

    private User()
    {
    }

    public Guid Id { get; private set; }
    public Email Email { get; private set; } = null!;
    public string? DisplayName { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static User Create(string email, string? displayName, DateTime createdAtUtc)
    {
        Dictionary<string, string[]> errors = new();
        string? trimmedDisplayName = NormalizeDisplayName(displayName);
        Email? emailValue = ValidateProfile(email, trimmedDisplayName, errors);

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new User
        {
            Id = Guid.CreateVersion7(),
            Email = emailValue!,
            DisplayName = trimmedDisplayName,
            CreatedAtUtc = createdAtUtc
        };
    }

    /// <summary>
    /// Validates the two caller-supplied fields <see cref="Create"/> accepts, appending any problems
    /// to <paramref name="errors"/> so they are reported together rather than one per attempt.
    /// </summary>
    private static Email? ValidateProfile(string email, string? trimmedDisplayName, Dictionary<string, string[]> errors)
    {
        Email? emailValue = null;

        try
        {
            emailValue = Email.Create(email);
        }
        catch (ValidationException exception)
        {
            foreach (KeyValuePair<string, string[]> error in exception.Errors)
            {
                errors[error.Key] = error.Value;
            }
        }

        if (trimmedDisplayName?.Length > MaxDisplayNameLength)
        {
            errors[nameof(DisplayName)] = [$"Display name must be {MaxDisplayNameLength} characters or fewer."];
        }

        return emailValue;
    }

    private static string? NormalizeDisplayName(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
}
