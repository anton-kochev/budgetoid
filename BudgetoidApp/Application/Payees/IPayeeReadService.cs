namespace Application.Payees;

public interface IPayeeReadService
{
    /// <summary>
    /// Reads every payee of the ambient budget, sealed names and all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every one of them, with no page, no filter and no search term, and that is a decision this
    /// interface makes rather than one it has yet to get round to.</b> This is the list a client resolves
    /// a counterparty against before it posts a transaction — the read that took over from the
    /// server-side find-or-create — so a partial answer is one the caller cannot use: a name missing from
    /// the page it was handed reads as a payee it must create, and the create collides on
    /// <c>IX_payees_budget_id_name_key</c>. Narrowing the set is not available to this side either, since
    /// narrowing means matching on a name, which is the question <see cref="GetByIdAsync"/> explains this
    /// server cannot answer.
    /// </para>
    /// <para>
    /// The order is the implementation's to argue and <c>PayeeReadService</c> argues it: not by name,
    /// which over a per-seal nonce is no order at all. Only the client holds the text, so only the client
    /// can sort by it.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<PayeeDto>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one payee of the ambient budget by its identifier, or <see langword="null"/> when the budget
    /// holds no such row.
    /// </summary>
    /// <remarks>
    /// <b>By identifier and by nothing else, which is the whole of what this interface may ever offer.</b>
    /// A member taking a name cannot be written here — <c>payees.name</c> is an envelope drawn under a
    /// fresh nonce every time, so two seals of one name are different bytes, and the digest that is stable
    /// is taken under a key this server never sees. <see cref="Domain.Payees.IPayeeRepository"/> states
    /// the same refusal for the write side.
    /// </remarks>
    Task<PayeeDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
