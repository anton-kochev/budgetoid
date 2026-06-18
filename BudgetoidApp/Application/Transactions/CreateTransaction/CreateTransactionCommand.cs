namespace Application.Transactions.CreateTransaction;

public sealed record CreateTransactionCommand(decimal Amount, DateOnly Date, string? Description);
