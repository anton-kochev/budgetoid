using Domain.Transactions;

namespace Application.Transactions;

public sealed record TransactionDto(
    Guid Id,
    decimal Amount,
    DateOnly Date,
    string Description,
    DateTime CreatedAtUtc,
    Guid AccountId,
    string AccountName,
    string CurrencyCode,
    string CurrencySymbol,
    Guid? PayeeId,
    string? PayeeName,
    Guid? GroupId = null,
    string? GroupName = null)
{
    public static TransactionDto FromTransaction(
        Transaction transaction,
        string accountName,
        string currencyCode,
        string currencySymbol,
        string? payeeName = null,
        string? groupName = null) => new(
        transaction.Id,
        transaction.Amount,
        transaction.Date,
        transaction.Description ?? string.Empty,
        transaction.CreatedAtUtc,
        transaction.AccountId,
        accountName,
        currencyCode,
        currencySymbol,
        transaction.PayeeId,
        payeeName,
        transaction.GroupId,
        groupName);
}

public sealed record TransactionListResponse(IReadOnlyList<TransactionDto> Items);
