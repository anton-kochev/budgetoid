using Domain.Transactions;

namespace Application.Transactions;

public sealed record TransactionDto(
    Guid Id,
    decimal Amount,
    DateOnly Date,
    string Description,
    DateTime CreatedAtUtc,
    Guid? PayeeId,
    string? PayeeName)
{
    public static TransactionDto FromTransaction(Transaction transaction, string? payeeName = null) => new(
        transaction.Id,
        transaction.Amount,
        transaction.Date,
        transaction.Description,
        transaction.CreatedAtUtc,
        transaction.PayeeId,
        payeeName);
}

public sealed record TransactionListResponse(IReadOnlyList<TransactionDto> Items);
