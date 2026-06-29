using Application.Currencies;
using Domain.Accounts;

namespace Application.Accounts;

public sealed record AccountDto(
    Guid Id,
    string Name,
    AccountType Type,
    decimal OpeningBalance,
    DateTime CreatedAtUtc,
    string CurrencyCode,
    string CurrencyName,
    string CurrencySymbol,
    int CurrencyMinorUnit)
{
    public static AccountDto FromAccount(Account account, CurrencyDto currency) => new(
        account.Id,
        account.Name,
        account.Type,
        account.OpeningBalance,
        account.CreatedAtUtc,
        account.CurrencyCode,
        currency.Name,
        currency.Symbol,
        currency.MinorUnit);
}

public sealed record AccountListResponse(IReadOnlyList<AccountDto> Items);
