using Domain.Common;

namespace Domain.Users;

public sealed record Email
{
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

        return new Email(trimmedValue);
    }

    public override string ToString() => Value;
}
