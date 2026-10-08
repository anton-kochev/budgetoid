using Application.Passkeys;
using Domain.Transactions;

namespace Application.Transactions;

/// <summary>
/// One transaction as every transaction-reading route hands it back, with the account, payee, category
/// and currency it names already denormalized onto it.
/// </summary>
/// <remarks>
/// <para>
/// <b>All five narrative members are envelopes and one rule finally covers every one of them.</b>
/// <see cref="Description"/>, <see cref="AccountName"/>, <see cref="PayeeName"/>,
/// <see cref="CategoryName"/> and <see cref="CategoryGroupName"/> are still typed <see cref="string"/>
/// and none of them holds text — each is its column's AEAD envelope as unpadded base64url, the treatment
/// <see cref="Accounts.AccountDto.Name"/>, <see cref="Payees.PayeeDto.Name"/>,
/// <see cref="CategoryGroups.CategoryGroupDto.Name"/> and <c>ExportedBudget.Name</c> carry, because this
/// server holds no key for any of them. This record used to say that three of its four name members were
/// envelopes and one was still readable text, and that a reader must not fold them into one rule; there
/// is nothing left to fold.
/// </para>
/// <para>
/// <b><see cref="CategoryName"/> stays on this record, and it was a decision rather than an
/// inheritance.</b> Dropping it would mean the transaction list cannot render a category until a second
/// request lands — the client-side join this shape exists to avoid. What it costs is one more envelope
/// bound to one more row id, which is the same argument the paragraph below already makes for three
/// members and which gets <em>stronger</em> with each one rather than weaker: a client reaching for the
/// wrong binding gets an authentication failure, not garbage.
/// </para>
/// <para>
/// <b><see cref="Description"/> is <see cref="string"/><c>?</c> and must never regain its
/// <c>?? string.Empty</c>.</b> That coercion shipped while the column held text and a screen needed
/// something to render; it is a defect now. <c>""</c> is not a legal envelope, so a client cannot tell
/// the coercion from a value it is expected to decode, and the difference being erased is real:
/// <see langword="null"/> is a note nobody filed, and a twenty-nine-byte envelope is a note somebody
/// wrote and then emptied.
/// </para>
/// <para>
/// <b>An envelope on this record is bound to a different row than the record is about, and that is a
/// property of the DTO rather than a note on one member.</b> Associated data is rebuilt from wherever a
/// ciphertext was found, so opening <see cref="PayeeName"/> needs the binding for <c>payees.name</c>
/// under the <em>payee's</em> row id — which the client rebuilds from <see cref="PayeeId"/> — opening
/// <see cref="AccountName"/> needs <c>accounts.name</c> under <see cref="AccountId"/>, and opening
/// <see cref="CategoryGroupName"/> needs <c>category_groups.name</c> under
/// <see cref="CategoryGroupId"/>, and opening <see cref="CategoryName"/> needs <c>categories.name</c>
/// under <see cref="CategoryId"/>. <see cref="Description"/> is the one that binds to
/// <see cref="Id"/> — this transaction's own row — which is exactly why the others cannot be opened with
/// it. The argument gets stronger with each member rather than weaker: a
/// client that reached for this transaction's own binding gets an authentication failure, not garbage —
/// the tag check fails and the value is unreadable, with nothing naming the cause. Each envelope travels
/// beside the identifier it was sealed against, which is why all four identifiers are on the wire.
/// </para>
/// </remarks>
public sealed record TransactionDto(
    Guid Id,
    decimal Amount,
    DateOnly Date,
    string? Description,
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
    /// already read — <c>accountName</c>, <c>payeeName</c>, <c>categoryName</c> and
    /// <c>categoryGroupName</c>, every one of them a <b>sealed</b> name as unpadded base64url rather
    /// than text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every sealed parameter is typed <see cref="string"/>, so nothing in this signature tells a caller
    /// what it is holding: passing <c>group.Name.ToString()</c> or <c>System.Text.Json</c>'s own
    /// <see cref="byte"/><c>[]</c> handling compiles and ships padded standard base64 the client's strict
    /// decoder refuses. Every caller encodes through <c>PasskeyEncoding.Encode</c>, which is the one
    /// alphabet every binary member of this API crosses JSON in.
    /// </para>
    /// <para>
    /// <b>The description is carried, never coerced.</b> This member used to write
    /// <c>?? string.Empty</c> and it was wrong the day the column was sealed: the empty string is not a
    /// legal envelope, so the coercion turned "no note" into a value a client is asked to decode and
    /// cannot. It may not come back in any form.
    /// </para>
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
        transaction.Description is null
            ? null
            : PasskeyEncoding.Encode(transaction.Description.Envelope.Span),
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
