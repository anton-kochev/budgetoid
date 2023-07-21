namespace Budgetoid.Application.Accounts.Queries.GetAccount;

public record AccountDto(
    Guid Id,
    decimal Balance,
    string Currency,
    string Name
);
