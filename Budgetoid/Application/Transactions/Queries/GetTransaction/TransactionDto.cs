namespace Budgetoid.Application.Transactions.Queries.GetTransaction;

public record TransactionDto(
    Guid AccountId,
    decimal Amount,
    Guid CategoryId,
    string Comment,
    DateOnly Date,
    Guid Id,
    string Payee,
    string[] Tags
);

