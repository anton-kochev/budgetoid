using System.Text.Json.Serialization;

namespace Application.Transactions.CreateTransaction;

/// <summary>
/// A transaction to create, in the shape <c>POST /api/transactions</c> binds its body to.
/// </summary>
/// <param name="Id">
/// The row's identifier, minted by the client, in the canonical spelling
/// <see cref="Application.Security.CanonicalIdentifier"/> accepts.
/// </param>
/// <param name="Amount">The signed amount, in the account's currency.</param>
/// <param name="Date">The day the money moved.</param>
/// <param name="AccountId">The account the transaction is filed under.</param>
/// <param name="Description">
/// The caller's <b>sealed</b> note as unpadded base64url, or <see langword="null" /> for none.
/// </param>
/// <param name="PayeeId">
/// The payee this transaction names, or <see langword="null" /> for none. A row the caller already
/// created through <c>POST /api/payees</c> — see the remarks for why it is an identifier.
/// </param>
/// <param name="CategoryId">The category this transaction is filed under, or <see langword="null" />.</param>
/// <remarks>
/// <para>
/// <b><see cref="Id" /> is a <see cref="string" /> and must never become a <see cref="Guid" />.</b> The
/// argument is <see cref="Payees.CreatePayee.CreatePayeeCommand.Id" />'s and is not restated: bound as a
/// <see cref="Guid" />, <c>System.Text.Json</c> folds the spellings before any handler sees text, which
/// does not make the canonical check fail — it makes it unwritable. This row seals one narrative member
/// against that identifier, so a spelling this API cannot reproduce costs the note.
/// </para>
/// <para>
/// <b><see cref="AccountId" />, <see cref="PayeeId" /> and <see cref="CategoryId" /> stay
/// <see cref="Guid" />s, and the difference is not drift.</b> None of them is associated data for
/// anything; each is a foreign key the caller read back from this API, so the spellings folding together
/// costs nothing.
/// </para>
/// <para>
/// <b><see cref="Description" /> is <see langword="null" /> for a transaction with no note, and
/// <c>""</c> is neither.</b> The decoder underneath refuses <see langword="null" /> and <c>""</c>
/// identically, so the distinction lives in the handler, which tests <c>is null</c> and nothing else.
/// <c>""</c> is a malformed envelope and answers 400.
/// </para>
/// <para>
/// <b><see cref="PayeeId" /> replaced a <c>PayeeName</c>, and the change is not a rename.</b> A name
/// could be resolved here because the server could fold its case and look it up; <c>payees.name</c> is
/// now an AEAD envelope drawn under a fresh nonce, so two seals of one name are different bytes and no
/// lookup by name is a question this side can answer. Creating the payee is therefore the caller's own
/// request, made before this one, and what arrives here is the row it created.
/// </para>
/// <para>
/// <b>The consequence is a payee that can outlive every transaction naming it.</b>
/// <c>CreateTransactionHandler</c> carries that argument at the write it used to guard.
/// </para>
/// <para>
/// <b>The <c>Disallow</c> below is why a body still carrying <c>payeeName</c> is a 400 and not a
/// 201.</b> <c>Api/Endpoints/TransactionEndpoints.cs</c> carries the argument, for both shapes that
/// held the retired member.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateTransactionCommand(
    string Id,
    decimal Amount,
    DateOnly Date,
    Guid AccountId,
    string? Description,
    Guid? PayeeId = null,
    Guid? CategoryId = null);
