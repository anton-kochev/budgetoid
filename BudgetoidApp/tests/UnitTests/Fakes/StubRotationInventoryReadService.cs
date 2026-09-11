using Application.KeyRotations;
using Application.KeyRotations.BeginKeyRotation;

namespace UnitTests.Fakes;

/// <summary>
/// The two reads a begin consults about the account itself: which budgets it owns, and how many rows
/// the rotation has to rewrite.
/// </summary>
/// <remarks>
/// <para>
/// <b>It answers both questions plainly and refuses nothing</b>, which is the whole reason it is a stub
/// rather than a copy of the real read. The "the account owns a budget other than the ambient one"
/// refusal is the <em>handler's</em> under test — a stub that threw <c>RotationScopeException</c> of its
/// own would leave the handler free to hold no such rule and every scope case would still pass.
/// </para>
/// <para>
/// <b>The counts are handed in whole rather than derived from seeded rows.</b> Nothing about the six
/// counts is computed anywhere in the Application ring; what the handler owes is to pass them through
/// unchanged and to name each one on the member a client reads it from, and a fixed answer is what makes
/// a transposed pair visible.
/// </para>
/// </remarks>
public sealed class StubRotationInventoryReadService(
    RotationInventory inventory,
    params Guid[] ownedBudgetIds)
    : IRotationInventoryReadService
{
    /// <summary>
    /// How many times the counts were read. Zero is what a refusal that happened first has to leave
    /// behind — a rotation refused for its scope or its factor set owes no inventory.
    /// </summary>
    public int CountCallCount { get; private set; }

    public Task<IReadOnlyList<Guid>> ListOwnedBudgetIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. ownedBudgetIds]);

    public Task<RotationInventory> CountNarrativeRowsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        CountCallCount++;

        return Task.FromResult(inventory);
    }
}
