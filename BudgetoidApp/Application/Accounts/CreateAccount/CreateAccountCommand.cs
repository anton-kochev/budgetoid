using Domain.Accounts;

namespace Application.Accounts.CreateAccount;

public sealed record CreateAccountCommand(string Name, AccountType Type, decimal OpeningBalance, string CurrencyCode);
