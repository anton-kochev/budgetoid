using Domain.Common;

namespace Domain.Currencies;

public sealed class Currency
{
    private Currency()
    {
    }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Symbol { get; private set; } = string.Empty;
    public int MinorUnit { get; private set; }

    public static Currency Create(string code, string name, string symbol, int minorUnit)
    {
        ValidateOrThrow(code, name, symbol, minorUnit);

        return new Currency
        {
            Code = NormalizeCode(code),
            Name = name.Trim(),
            Symbol = symbol.Trim(),
            MinorUnit = minorUnit,
        };
    }

    private static void ValidateOrThrow(string? code, string? name, string? symbol, int minorUnit)
    {
        var errors = new Dictionary<string, string[]>();
        string normalizedCode = NormalizeCode(code);
        string trimmedName = name?.Trim() ?? string.Empty;
        string trimmedSymbol = symbol?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            errors[nameof(Code)] = ["Code is required."];
        }
        else if (normalizedCode.Length != 3 || normalizedCode.Any(character => character is < 'A' or > 'Z'))
        {
            errors[nameof(Code)] = ["Code must be exactly 3 ASCII letters."];
        }

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            errors[nameof(Name)] = ["Name is required."];
        }
        else if (trimmedName.Length > 100)
        {
            errors[nameof(Name)] = ["Name must be 100 characters or fewer."];
        }

        if (string.IsNullOrWhiteSpace(trimmedSymbol))
        {
            errors[nameof(Symbol)] = ["Symbol is required."];
        }
        else if (trimmedSymbol.Length > 8)
        {
            errors[nameof(Symbol)] = ["Symbol must be 8 characters or fewer."];
        }

        if (minorUnit is < 0 or > 4)
        {
            errors[nameof(MinorUnit)] = ["Minor unit must be between 0 and 4."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    private static string NormalizeCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();
}
