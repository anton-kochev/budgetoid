using Application.KeyRotations;

namespace UnitTests.Fakes;

/// <summary>
/// The gate a completion consults before it destroys the generation in force, standing in for the read
/// that walks six tables: it answers <see langword="true" /> for the runs a caller named as finished
/// and <see langword="false" /> for every other, and it records what it was asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>It answers per rotation identifier, and that is the whole reason it is not a
/// <see langword="bool" /> switch.</b> The read takes a run's identifier and has no idea which run an
/// account has staged — <see cref="IRotationCompletenessReadService" />'s own doc says the answer is
/// about stamps and about nothing else. So a handler that asked it with the <em>caller's</em>
/// identifier rather than the staged one can be told apart from one that asked with the staged one
/// only by a stub that gives the two different answers. A switch cannot express that arrangement, and
/// a suite built on one would leave the substitution untested while looking thorough.
/// </para>
/// <para>
/// <b><see cref="Asked" /> records the account beside the run</b>, because the read is scoped by an
/// explicit owner for the reason that interface writes out at length: <c>budgets</c> carries no query
/// filter, so an implementation that lost the owner predicate answers for the wrong account, and an
/// empty answer there reads as <em>complete</em>. A test that pinned only the rotation identifier
/// would not notice a handler passing somebody else's user id.
/// </para>
/// <para>
/// <b>Nothing here models the <c>IS DISTINCT FROM</c> trap</b> — the never-stamped rows that a careful
/// null guard silently drops — and no case driving this stub may be read as covering it. That is a
/// property of the real query, it needs PostgreSQL, and <c>RotationCompletenessTests</c> owns it one
/// tier down. What this stub is for is the tier above: whether the destructive step asks the gate at
/// all, which identifier it asks with, and what it does with a <see langword="false" />.
/// </para>
/// </remarks>
public sealed class StubRotationCompletenessReadService(params Guid[] completeRotationIds)
    : IRotationCompletenessReadService
{
    private readonly List<(Guid UserId, Guid RotationId)> _asked = [];

    /// <summary>
    /// Every question this gate was put, oldest first. Empty is what a refusal that happened before the
    /// gate has to leave behind, and it is a different fact from the gate having answered
    /// <see langword="false" />.
    /// </summary>
    public IReadOnlyList<(Guid UserId, Guid RotationId)> Asked => _asked;

    /// <summary>
    /// Whether the gate <b>refuses</b> rather than answering, which is what it does when the account
    /// owns a budget other than the one the request operates inside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refusing and answering <see langword="false" /> are two different facts and a caller must
    /// never collapse them.</b> A <see langword="false" /> says the account's rows are all visible and
    /// some of them are outstanding. This says the read <em>could not see the question</em>: five of
    /// the six sets it walks are scoped to the ambient budget by a query filter that takes no
    /// argument, so for an account owning a budget beside that one the answer would be about part of
    /// an account — and the part it cannot reach is exactly the part the promotion would destroy.
    /// <see cref="IRotationCompletenessReadService" /> and <c>docs/business-logic/export.md</c> argue
    /// it; the price of getting it wrong here is the account.
    /// </para>
    /// <para>
    /// <b>The ask is recorded before the throw</b>, so a case can say the refusal came out of the gate
    /// rather than out of something earlier that happened to have the same shape.
    /// </para>
    /// </remarks>
    public bool RefusesForScope { get; set; }

    public Task<bool> EveryNarrativeRowIsStampedAsync(
        Guid userId,
        Guid rotationId,
        CancellationToken cancellationToken = default)
    {
        _asked.Add((userId, rotationId));

        if (RefusesForScope)
        {
            // Counts and never identifiers, the rule RotationScopeException states: the Development
            // branch of GlobalExceptionHandler echoes this message into the response body.
            throw new RotationScopeException(
                "The account owns 2 budgets, of which 1 is the one this request operates inside; a "
                + "rotation can rewrite the narrative rows of that budget alone.");
        }

        return Task.FromResult(completeRotationIds.Contains(rotationId));
    }
}
