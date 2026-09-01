using Domain.Transactions;

namespace Application.Transactions;

/// <summary>
/// One transaction as every transaction-reading route hands it back, with the account, payee, category
/// and currency it names already denormalized onto it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two of the four name members are envelopes and two are still text, and no rule covers all
/// four.</b> <see cref="AccountName"/> and <see cref="PayeeName"/> are still typed
/// <see cref="string"/> and no longer hold names — each is its column's AEAD envelope as unpadded
/// base64url, the treatment <see cref="Accounts.AccountDto.Name"/>, <see cref="Payees.PayeeDto.Name"/>
/// and <c>ExportedBudget.Name</c> carry, because this server holds no key for either.
/// <see cref="CategoryName"/> and <see cref="CategoryGroupName"/> are readable text: those columns are
/// not sealed yet. A reader must not fold the four into one rule in either direction — neither
/// decoding a category name as base64url nor rendering a payee name as a caption.
/// </para>
/// <para>
/// <b>An envelope on this record is bound to a different row than the record is about, and that is a
/// property of the DTO rather than a note on one member.</b> Associated data is rebuilt from wherever a
/// ciphertext was found, so opening <see cref="PayeeName"/> needs the binding for <c>payees.name</c>
/// under the <em>payee's</em> row id — which the client rebuilds from <see cref="PayeeId"/> — and
/// opening <see cref="AccountName"/> needs <c>accounts.name</c> under <see cref="AccountId"/>. A client
/// that reached for this transaction's own binding gets an authentication failure, not garbage: the tag
/// check fails and the value is unreadable, with nothing naming the cause. Each envelope travels beside
/// the identifier it was sealed against, which is why both identifiers are on the wire.
/// </para>
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
    /// already read — <c>accountName</c> and <c>payeeName</c> among them, and both are the
    /// <b>sealed</b> names as unpadded base64url rather than text.
    /// </summary>
    /// <remarks>
    /// The two sealed parameters are typed <see cref="string"/> like the two text ones, so nothing in
    /// this signature tells a caller which is which: passing <c>payee.Name.ToString()</c> or
    /// <c>System.Text.Json</c>'s own <see cref="byte"/><c>[]</c> handling compiles and ships padded
    /// standard base64 the client's strict decoder refuses. Every caller encodes through
    /// <c>PasskeyEncoding.Encode</c>, which is the one alphabet every binary member of this API crosses
    /// JSON in.
    /// </remarks>
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
