using Domain.Common;

namespace Domain.Accounts;

public sealed class Account
{
    private const int MaxMinorUnit = 4;

    private Account()
    {
    }

    public Guid Id { get; private set; }
    public Guid BudgetId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AccountType Type { get; private set; }
    public decimal OpeningBalance { get; private set; }
    public string CurrencyCode { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public static Account Create(
        Guid budgetId,
        string name,
        AccountType type,
        decimal openingBalance,
        string currencyCode,
        int minorUnit,
        DateTime createdAtUtc)
    {
        ValidateOrThrow(budgetId, name, type, openingBalance, currencyCode, minorUnit);

        return new Account
        {
            Id = Guid.CreateVersion7(),
            BudgetId = budgetId,
            Name = name.Trim(),
            Type = type,
            OpeningBalance = openingBalance,
            CurrencyCode = NormalizeCurrencyCode(currencyCode),
            CreatedAtUtc = createdAtUtc,
        };
    }

    public void Update(string name, AccountType type, decimal openingBalance, int minorUnit)
    {
        ValidateOrThrow(BudgetId, name, type, openingBalance, CurrencyCode, minorUnit);

        Name = name.Trim();
        Type = type;
        OpeningBalance = openingBalance;
    }

    private static void ValidateOrThrow(
        Guid budgetId,
        string? name,
        AccountType type,
        decimal openingBalance,
        string? currencyCode,
        int minorUnit)
    {
        // The minor unit comes from the account's currency, which the database already bounds to
        // 0..4, so an out-of-range value is a broken caller rather than user input - and a bad one
        // makes the precision check below meaningless, so it fails fast instead of joining errors.
        ArgumentOutOfRangeException.ThrowIfNegative(minorUnit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minorUnit, MaxMinorUnit);

        var errors = new Dictionary<string, string[]>();
        string trimmedName = name?.Trim() ?? string.Empty;
        string normalizedCurrencyCode = NormalizeCurrencyCode(currencyCode);

        if (budgetId == Guid.Empty)
        {
            errors[nameof(BudgetId)] = ["Budget id is required."];
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

        if (decimal.Round(openingBalance, minorUnit) != openingBalance)
        {
            errors[nameof(OpeningBalance)] = [minorUnit == 0
                ? "Opening balance must be a whole number."
                : $"Opening balance must have no more than {minorUnit} decimal places."];
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
