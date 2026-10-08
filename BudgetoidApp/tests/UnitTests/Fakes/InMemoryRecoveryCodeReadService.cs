using Application.RecoveryCodes;

namespace UnitTests.Fakes;

/// <summary>
/// The "how many codes has this account left?" projection, answered from the rows
/// <see cref="InMemoryRecoveryCodeRepository" /> is holding at the moment it is asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads the repository rather than keeping a number of its own, and that is the whole design.</b>
/// The count a redemption reports has to be the count <em>after</em> the consume — the card before it no
/// longer exists — so a fake holding a figure a test handed it would report whatever the test expected
/// whether or not the handler had spent anything, and the one member of
/// <c>RedeemedRecoveryCode</c> that is a fact about this request would stop being measured.
/// </para>
/// <para>
/// <b>The owner predicate is honoured, not accepted and dropped.</b> <c>recovery_code_hashes</c> is
/// exempt from row-level security, so no policy and no query filter narrows the real query — the
/// argument is <see cref="IRecoveryCodeReadService.CountRemainingForUserAsync" />'s own: drop the
/// predicate and every caller is told how many codes the whole installation holds. A fake that counted
/// every row would let exactly that through, which is why the fixtures that use this seed a second
/// account.
/// </para>
/// <para>
/// <see cref="CountedFor" /> exists because "the count names the account the code resolved" is a claim
/// about the argument rather than about the number: with one account in the store, an owner read off the
/// wrong thing produces the right total anyway.
/// </para>
/// </remarks>
public sealed class InMemoryRecoveryCodeReadService(InMemoryRecoveryCodeRepository recoveryCodes)
    : IRecoveryCodeReadService
{
    private readonly List<Guid> _countedFor = [];

    /// <summary>Every account this service was asked to count for, oldest first.</summary>
    public IReadOnlyList<Guid> CountedFor => _countedFor;

    public Task<int> CountRemainingForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _countedFor.Add(userId);

        return Task.FromResult(recoveryCodes.Hashes.Count(hash => hash.UserId == userId));
    }
}
