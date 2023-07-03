namespace Budgetoid.Application.Accounts.Queries.GetAccount;

public record AccountDto
{
    public Guid Id { get; init; }
    public string Currency { get; init; } = "USD";
    public string Name { get; init; } = string.Empty;
}
