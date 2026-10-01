using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Transactions;

namespace Domain.Security;

/// <summary>
/// The five sets a content-key rotation rewrites, loaded by row identifier so that a chunk can re-seal
/// the rows a client named and stamp each with the run that rewrote it.
/// </summary>
/// <remarks>
/// <para>
/// <b>There are five arms and there is deliberately no budget arm.</b> A sixth would need
/// <c>rotation_id</c> on the <c>budgets</c> <c>GRANT UPDATE</c> column list, and FR-099 requires the
/// application role to hold <c>UPDATE</c> on <c>budgets.name</c> and on no other column — an omission
/// from a column list being the only way this schema makes a column immutable. So the sixth arm is
/// refused rather than missing, and <see cref="Budgets.Budget.ResealName" /> being
/// <see langword="internal" /> is what makes the refusal a compile error rather than a review note:
/// <c>Domain.csproj</c> grants Domain's internals to <c>Infrastructure</c> alone, so no caller in the
/// Application ring can reach it whatever the grant said. Nothing is lost while every budget row is
/// nameless and the completeness gate is presence-aware, so no budget is ever outstanding.
/// <c>docs/business-logic/key-rotation.md</c> argues all of it.
/// </para>
/// <para>
/// <b>Each list answers a dictionary rather than a list, because the caller's question is a
/// lookup.</b> A chunk holds one entry per row it is re-sealing and has to find the row that entry
/// names; a list would make that a second search, and the search would be written five times. It is
/// <see cref="Users.IKeyRotationRepository.ListFactorsAsync" />'s argument on a different key.
/// </para>
/// <para>
/// <b>A miss is a miss and never a null entity, and that is the whole of how a foreign row is reported
/// here.</b> The five sets carry the <c>BudgetIsolation</c> query filter, so a row of another budget is
/// <em>invisible</em> rather than forbidden — an implementation answers a dictionary that simply does
/// not hold that key. A caller that read a missing key as "nothing to do for this one" would answer
/// success to a chunk that re-sealed nothing, after which the client counts those rows as done and the
/// completeness gate refuses a run the client believes it finished. So the absent key is a refusal the
/// caller owes, not a silence it may keep.
/// </para>
/// <para>
/// <b>The rows come back tracked, which is what separates this port from every read service beside
/// it.</b> A reseal is a mutation of a loaded entity followed by <see cref="SaveAsync" />, so an
/// implementation that asked for no tracking would answer a chunk that appears to succeed and writes
/// nothing. The never-materialise rule <c>IKeyRotationRepository</c> keeps is about
/// <c>wrapped_account_keys</c>, whose role holds no <c>DELETE</c> and where a tracked row EF later
/// cascades into dies with <c>42501</c>; these five tables are the ordinary budget-owned ones the role
/// holds a column-listed <c>UPDATE</c> on, and tracking them is exactly what that grant is for.
/// </para>
/// </remarks>
public interface INarrativeResealRepository
{
    /// <summary>The accounts of the ambient budget that <paramref name="ids" /> names.</summary>
    /// <remarks>
    /// An identifier no row answers for is absent from the result rather than raising: see the interface
    /// remarks for why that is the shape and what the caller owes because of it.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, Account>> ListAccountsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ListAccountsAsync" />
    Task<IReadOnlyDictionary<Guid, Payee>> ListPayeesAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ListAccountsAsync" />
    Task<IReadOnlyDictionary<Guid, CategoryGroup>> ListCategoryGroupsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ListAccountsAsync" />
    Task<IReadOnlyDictionary<Guid, Category>> ListCategoriesAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ListAccountsAsync" />
    Task<IReadOnlyDictionary<Guid, Transaction>> ListTransactionsAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists every reseal applied to the rows this port answered with, in one save.
    /// </summary>
    /// <remarks>
    /// <b>One save for all five arms, and it is the caller that asks for it rather than each list
    /// member saving its own.</b> A chunk's arms have to reach the database together or the account is
    /// left part under one content key and part under the next, with a stamp on each half saying the
    /// whole run rewrote it — and the stamp is the one signal the destructive completion step trusts.
    /// A save per arm would commit four of them and leave the fifth to a refusal, which is exactly the
    /// state the staging design exists to keep out of reach.
    /// </remarks>
    /// <exception cref="Common.ConflictException">
    /// A re-sealed name would give two rows of the budget one blind-index value, spelled
    /// <c>rotation_name_collision</c>. Nothing the save carried is written.
    /// </exception>
    Task SaveAsync(CancellationToken cancellationToken = default);
}
