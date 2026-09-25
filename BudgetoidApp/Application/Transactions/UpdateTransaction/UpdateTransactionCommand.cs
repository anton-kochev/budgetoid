using Application.Abstractions;

namespace Application.Transactions.UpdateTransaction;

/// <summary>
/// A partial edit of a transaction. Every mutable field is an <see cref="Optional{T}"/>: absent
/// leaves the current value alone, present replaces it, and present-and-null clears it. Amount, date
/// and account are declared over non-nullable types because a transaction without them is not a
/// transaction, so an explicit null for those is a bad request rather than a clear.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="PayeeId"/> replaced a <c>PayeeName</c>, and its three states are the same three.</b>
/// Absent leaves whatever payee the transaction already names; present-and-null detaches it;
/// present with an identifier attaches the payee that identifier names, which must be a row of the
/// ambient budget. What is gone is the fourth reading the old member carried: a blank or whitespace
/// name used to mean "no payee", because a name was a string somebody typed. An identifier has no
/// blank — the clear is spelled by the explicit null and by nothing else.
/// </para>
/// <para>
/// <b>The payee is named rather than described because the server can no longer resolve a name.</b>
/// <c>payees.name</c> is an AEAD envelope drawn under a fresh nonce, so no lookup by name is a
/// question this side can answer; <c>CreateTransactionCommand</c> carries the full argument.
/// </para>
/// </remarks>
public sealed record UpdateTransactionCommand(
    Guid Id,
    Optional<decimal> Amount,
    Optional<DateOnly> Date,
    Optional<Guid> AccountId,
    Optional<string?> Description,
    Optional<Guid?> PayeeId,
    Optional<Guid?> CategoryId);
