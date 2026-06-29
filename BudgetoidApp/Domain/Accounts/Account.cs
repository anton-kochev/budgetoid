using Domain.Common;

namespace Domain.Accounts;

public sealed class Account
{
    private Account()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AccountType Type { get; private set; }
    public decimal OpeningBalance { get; private set; }
    public string CurrencyCode { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public static Account Create(
        Guid userId,
        string name,
        AccountType type,
        decimal openingBalance,
        string currencyCode,
        DateTime createdAtUtc)
    {
        ValidateOrThrow(userId, name, type, openingBalance, currencyCode);

        return new Account
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name.Trim(),
            Type = type,
            OpeningBalance = openingBalance,
            CurrencyCode = NormalizeCurrencyCode(currencyCode),
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void Update(string name, AccountType type, decimal openingBalance)
    {
        ValidateOrThrow(UserId, name, type, openingBalance, CurrencyCode);

        Name = name.Trim();
        Type = type;
        OpeningBalance = openingBalance;
    }

    private static void ValidateOrThrow(Guid userId, string? name, AccountType type, decimal openingBalance, string? currencyCode)
    {
        var errors = new Dictionary<string, string[]>();
        string trimmedName = name?.Trim() ?? string.Empty;
        string normalizedCurrencyCode = NormalizeCurrencyCode(currencyCode);

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

        if (!Enum.IsDefined(type))
        {
            errors[nameof(Type)] = ["Account type is invalid."];
        }

        if (string.IsNullOrWhiteSpace(normalizedCurrencyCode))
        {
            errors[nameof(CurrencyCode)] = ["Currency code is required."];
        }
        else if (normalizedCurrencyCode.Length != 3 || normalizedCurrencyCode.Any(character => character is < 'A' or > 'Z'))
        {
            errors[nameof(CurrencyCode)] = ["Currency code must be exactly 3 ASCII letters."];
        }

        if (decimal.Round(openingBalance, 2) != openingBalance)
        {
            errors[nameof(OpeningBalance)] = ["Opening balance must have no more than 2 decimal places."];
        }
        else if (Math.Abs(openingBalance) > 1_000_000_000m)
        {
            errors[nameof(OpeningBalance)] = ["Opening balance must be less than or equal to 1000000000 in absolute value."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    private static string NormalizeCurrencyCode(string? currencyCode) => (currencyCode ?? string.Empty).Trim().ToUpperInvariant();
}
