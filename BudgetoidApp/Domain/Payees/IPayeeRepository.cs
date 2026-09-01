namespace Domain.Payees;

/// <summary>
/// The writes a payee supports: create one, read one by its identifier, rename it.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no member here that takes a name, and its absence is the load-bearing part of this
/// file.</b> A payee is now found by its identifier or not at all. The server holds no index key, so it
/// cannot compute the digest a name is stored under; it cannot fold a name's case either, since the
/// column that used to carry a case-insensitive collation is <c>bytea</c> and <c>bytea</c> is not
/// collatable. A lookup by name is therefore not a member somebody has yet to write — it is a question
/// nothing on this side can answer, and a member offering to answer it could only do so by comparing
/// ciphertext, which differs for every seal of the same text because every seal draws a fresh nonce.
/// </para>
/// <para>
/// <b>What that costs, so nobody re-derives it as a gap:</b> find-or-create is gone with it, and with it
/// the property that a payee row could only come into existence beside the transaction that needed it.
/// Creating a payee is now a request of its own, made by the client that already holds the list and can
/// tell whether the counterparty is on it. Uniqueness is what remains of the old behaviour, and it is
/// enforced by <c>IX_payees_budget_id_name_key</c> over the blind index rather than by a re-read here.
/// </para>
/// </remarks>
public interface IPayeeRepository
{
    Task AddAsync(Payee payee, CancellationToken cancellationToken = default);

    Task<Payee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(Payee payee, CancellationToken cancellationToken = default);
}
