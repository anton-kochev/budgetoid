using Domain.Common;

namespace Domain.Users;

public sealed record Email
{
    /// <summary>The RFC 5321 path limit of 256 octets less the enclosing angle brackets.</summary>
    public const int MaxLength = 254;

    private Email(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Email Create(string value)
    {
        string trimmedValue = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedValue))
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(Email)] = ["Email is required."],
            });
        }

        if (trimmedValue.Length > MaxLength)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(Email)] = [$"Email must be {MaxLength} characters or fewer."],
            });
        }

        return new Email(trimmedValue);
    }

    public override string ToString() => Value;
}
