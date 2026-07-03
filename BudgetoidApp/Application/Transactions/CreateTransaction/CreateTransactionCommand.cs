namespace Application.Transactions.CreateTransaction;

public sealed record CreateTransactionCommand(
    decimal Amount,
    DateOnly Date,
    Guid AccountId,
    string? Description,
    string? PayeeName = null,
    Guid? GroupId = null);
