using Application.KeyRotations.BeginKeyRotation;

namespace Application.KeyRotations;

/// <summary>
/// The two reads a begin consults about the account itself: which budgets it owns, and how many rows
/// the rotation has to rewrite.
/// </summary>
/// <remarks>
/// <para>
/// <b>A read service rather than members on a repository</b>, the split <c>ICredentialReadService</c>
/// and <c>IRotationCompletenessReadService</c> both state: a repository loads entities that rules are
/// applied to, and these answer questions out of columns. Here the split earns more than usual,
/// because the question is about an entire account — materialising an account's worth of tracked
/// entities to count them is the one shape this path must not build, and a count that went through the
/// change tracker would also be the way <c>wrapped_account_keys</c> and <c>sessions</c> get pulled into
/// a save that then dies with <c>42501</c>.
/// </para>
/// <para>
/// <b>It refuses nothing, which is the difference from its sibling gate.</b>
/// <c>IRotationCompletenessReadService</c> throws <see cref="RotationScopeException" /> itself, because
/// rotation's completion route is unbuilt and a guard placed in a handler that does not exist guards
/// nothing. Begin <em>has</em> a handler, and that handler has to make the same refusal before it
/// reads anything else at all — so the refusal belongs there, and a second throw in here would leave
/// <c>BeginKeyRotationHandler</c> free to hold no such rule while every scope test still passed.
/// </para>
/// <para>
/// <b>The two members are separate calls because the first one decides whether the second may be
/// asked.</b> Five of the six sets counted below carry the <c>BudgetIsolation</c> query filter and are
/// scoped to the <em>ambient</em> budget, which takes no argument and cannot be re-pointed part-way
/// through a request; so unless the owned set is exactly that budget the counts are a denominator
/// measured over part of an account. Folding the two into one call would make the caller's ordering a
/// matter of taste rather than a matter of which read runs first.
/// </para>
/// </remarks>
public interface IRotationInventoryReadService
{
    /// <summary>
    /// The identifiers of every budget <paramref name="userId" /> owns.
    /// </summary>
    /// <remarks>
    /// <b>The owner predicate on <c>budgets</c> is written by hand or it is not there at all.</b> That
    /// table carries no query filter — it is what registration writes and what session authentication
    /// reads before any budget is ambient — so nothing beneath this call narrows it. The
    /// <c>user_isolation</c> policy sits under it in production and does not replace it, for the reason
    /// <c>ExportReadService</c> gives about its own <c>budgets</c> predicate: a policy makes a wrong
    /// query answer <em>empty</em> rather than correct, and an empty owned set here reads as "the
    /// account owns nothing", which is a refusal aimed at a caller who did nothing wrong.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListOwnedBudgetIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many rows carrying a narrative value each of the six narrative-bearing tables holds for
    /// <paramref name="userId" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The population is the completeness gate's population, and the two must not drift.</b> The
    /// gate asks whether any row that <em>carries a narrative value</em> is unstamped; this answers how
    /// many such rows there are. Count a wider set here and the client's progress bar stops short of
    /// the end on an ordinary account — count a narrower one and it reports a rotation finished while
    /// the gate is still refusing, which <c>docs/business-logic/key-rotation.md</c> calls harder to
    /// diagnose than a crash.
    /// </para>
    /// <para>
    /// <b>It answers for the ambient budget on five of the six sets</b>, for the reason above, and the
    /// caller is responsible for having established that the ambient budget is the whole of what the
    /// account owns before it asks.
    /// </para>
    /// </remarks>
    Task<RotationInventory> CountNarrativeRowsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
