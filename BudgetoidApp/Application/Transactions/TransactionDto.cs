using Domain.Transactions;

namespace Application.Transactions;

/// <summary>
/// One transaction as every transaction-reading route hands it back, with the account, payee, category
/// and currency it names already denormalized onto it.
/// </summary>
/// <remarks>
/// <b><see cref="AccountName"/> is still a <see cref="string"/> and no longer holds a name</b> — the
/// treatment <see cref="Accounts.AccountDto.Name"/> and <c>ExportedBudget.Name</c> carry. It is the
/// <c>accounts.name</c> envelope as unpadded base64url, because this server holds no key for it. The
/// three members beside it — <see cref="PayeeName"/>, <see cref="CategoryName"/> and
/// <see cref="CategoryGroupName"/> — are still readable text: those columns have not been sealed yet, so
/// a reader must not assume one rule across the four.
/// </remarks>
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
    Guid? CategoryId,
    string? CategoryName,
    Guid? CategoryGroupId,
    string? CategoryGroupName)
{
    /// <summary>
    /// Shapes one transaction together with the values its foreign keys point at, which the caller has
    /// already read — <c>accountName</c> among them, and it is the <b>sealed</b> name as unpadded
    /// base64url rather than text.
    /// </summary>
    public static TransactionDto FromTransaction(
        Transaction transaction,
        string accountName,
        string currencyCode,
        string currencySymbol,
        string? payeeName = null,
        string? categoryName = null,
        Guid? categoryGroupId = null,
        string? categoryGroupName = null) => new(
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
        transaction.CategoryId,
        categoryName,
        categoryGroupId,
        categoryGroupName);
}

public sealed record TransactionListResponse(IReadOnlyList<TransactionDto> Items);
