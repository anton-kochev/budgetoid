using Domain.Transactions;

namespace Application.Transactions;

/// <summary>
/// One transaction as every transaction-reading route hands it back, with the account, payee, category
/// and currency it names already denormalized onto it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three of the four name members are envelopes and one is still text, and no rule covers all
/// four.</b> <see cref="AccountName"/>, <see cref="PayeeName"/> and <see cref="CategoryGroupName"/> are
/// still typed <see cref="string"/> and no longer hold names — each is its column's AEAD envelope as
/// unpadded base64url, the treatment <see cref="Accounts.AccountDto.Name"/>,
/// <see cref="Payees.PayeeDto.Name"/>, <see cref="CategoryGroups.CategoryGroupDto.Name"/> and
/// <c>ExportedBudget.Name</c> carry, because this server holds no key for any of them.
/// <see cref="CategoryName"/> alone is readable text: <c>categories.name</c> is not sealed yet, and it
/// is the next column to be. A reader must not fold the four into one rule in either direction —
/// neither decoding the category name as base64url nor rendering a group name as a caption. This
/// paragraph is provisional and is to be rewritten rather than patched when that column moves.
/// </para>
/// <para>
/// <b>An envelope on this record is bound to a different row than the record is about, and that is a
/// property of the DTO rather than a note on one member.</b> Associated data is rebuilt from wherever a
/// ciphertext was found, so opening <see cref="PayeeName"/> needs the binding for <c>payees.name</c>
/// under the <em>payee's</em> row id — which the client rebuilds from <see cref="PayeeId"/> — opening
/// <see cref="AccountName"/> needs <c>accounts.name</c> under <see cref="AccountId"/>, and opening
/// <see cref="CategoryGroupName"/> needs <c>category_groups.name</c> under
/// <see cref="CategoryGroupId"/>. The argument gets stronger with each member rather than weaker: a
/// client that reached for this transaction's own binding gets an authentication failure, not garbage —
/// the tag check fails and the value is unreadable, with nothing naming the cause. Each envelope travels
/// beside the identifier it was sealed against, which is why all three identifiers are on the wire.
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
    /// already read — <c>accountName</c>, <c>payeeName</c> and <c>categoryGroupName</c> among them, and
    /// all three are the <b>sealed</b> names as unpadded base64url rather than text.
    /// </summary>
    /// <remarks>
    /// The three sealed parameters are typed <see cref="string"/> like the one text parameter, so nothing
    /// in this signature tells a caller which is which: passing <c>group.Name.ToString()</c> or
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
