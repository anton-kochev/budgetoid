namespace Budgetoid.Application.Transactions.Queries.GetTransaction;

public record TransactionDto
{
    public Guid AccountId { get; init; }
    public decimal Amount { get; init; }
    public Guid CategoryId { get; init; }
    public string Comment { get; init; } = string.Empty;
    public DateOnly Date { get; init; }
    public Guid Id { get; init; }
    public string Payee { get; init; } = string.Empty;
    public string[] Tags { get; init; } = Array.Empty<string>();
}
