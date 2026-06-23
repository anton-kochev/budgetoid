using Domain.Common;

namespace Domain.Users;

public sealed class User
{
    private User()
    {
    }

    public Guid Id { get; private set; }
    public string GoogleSubject { get; private set; } = string.Empty;
    public Email Email { get; private set; } = null!;
    public string? DisplayName { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static User Create(string googleSubject, string email, string? displayName, DateTime createdAtUtc)
    {
        Dictionary<string, string[]> errors = new();
        string trimmedGoogleSubject = googleSubject.Trim();
        Email? emailValue = null;
        string? trimmedDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

        if (string.IsNullOrWhiteSpace(trimmedGoogleSubject))
        {
            errors[nameof(GoogleSubject)] = ["Google subject is required."];
        }

        try
        {
            emailValue = Email.Create(email);
        }
        catch (ValidationException exception)
        {
            foreach (KeyValuePair<string, string[]> error in exception.Errors) errors[error.Key] = error.Value;
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new User
        {
            Id = Guid.CreateVersion7(),
            GoogleSubject = trimmedGoogleSubject,
            Email = emailValue!,
            DisplayName = trimmedDisplayName,
            CreatedAtUtc = createdAtUtc
        };
    }

    public void UpdateProfile(string email, string? displayName)
    {
        Email = Email.Create(email);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
    }
}
