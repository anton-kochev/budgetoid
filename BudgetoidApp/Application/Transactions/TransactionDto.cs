using Domain.Transactions;

namespace Application.Transactions;

public sealed record TransactionDto(
    Guid Id,
    decimal Amount,
    DateOnly Date,
    string Description,
    DateTime CreatedAtUtc)
{
    public static TransactionDto FromTransaction(Transaction transaction) => new(
        transaction.Id,
        transaction.Amount,
        transaction.Date,
        transaction.Description,
        transaction.CreatedAtUtc);
}

public sealed record TransactionListResponse(IReadOnlyList<TransactionDto> Items);
