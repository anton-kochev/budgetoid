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

    public void UpdateProfile(string email, string? displayName)
    {
        Dictionary<string, string[]> errors = new();
        string? trimmedDisplayName = NormalizeDisplayName(displayName);
        Email? emailValue = ValidateProfile(email, trimmedDisplayName, errors);

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        Email = emailValue!;
        DisplayName = trimmedDisplayName;
    }

    /// <summary>
    /// Validates the two fields a profile refresh can change, appending any problems to
    /// <paramref name="errors"/> so <see cref="Create"/> can report them alongside its own.
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
