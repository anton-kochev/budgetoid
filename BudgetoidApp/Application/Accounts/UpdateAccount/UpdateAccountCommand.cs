using Domain.Accounts;

namespace Application.Accounts.UpdateAccount;

public sealed record UpdateAccountCommand(Guid Id, string Name, AccountType Type, decimal OpeningBalance);
