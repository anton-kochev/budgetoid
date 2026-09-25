using System.Security.Cryptography;
using Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

/// <summary>
/// Keeps outstanding WebAuthn challenges in <c>webauthn_challenges</c>.
/// </summary>
/// <remarks>
/// Internal, following the row and the DbSet it works over: the application layer holds the port, and
/// nothing outside this assembly has a reason to reach the row behind it.
/// </remarks>
internal sealed class DbWebAuthnChallengeStore(
    BudgetoidDbContext dbContext,
    TimeProvider timeProvider) : IWebAuthnChallengeStore
{
    // The size WebAuthn's own guidance settles on, and the exact length
    // CK_webauthn_challenges_length holds this table to.
    private const int ChallengeLength = 32;

    // How many expired rows one issue may clear. Capped rather than unbounded because this work is
    // charged to a caller waiting on a sign-in ceremony: a table that somehow grew large must not turn
    // the next ceremony into a long delete, and the sweep runs on every issue, so a backlog drains
    // across calls instead of in one of them.
    private const int SweepBatchSize = 100;

    // Long enough for a person to reach for an authenticator, short enough that the window in which a
    // leaked nonce is worth anything closes on its own. Named, because the same number is handed to the
    // client in the credential options and enforced by ConsumeAsync, and the two must be one number.
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async Task<IssuedChallenge> IssueAsync(
        WebAuthnCeremony ceremony,
        CancellationToken cancellationToken = default)
    {
        DateTime nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        await SweepExpiredAsync(nowUtc, cancellationToken);

        WebAuthnChallengeRow row = new()
        {
            // Chosen here rather than left to EF's key-generation convention: the row is fully formed
            // before the context sees it, and the version 7 layout is the time-ordered shape every
            // other identifier in the schema uses, which is what keeps inserts into a table of
            // short-lived rows landing together rather than scattered across the index.
            Id = Guid.CreateVersion7(),

            // A cryptographic generator, not Random: the whole value of a nonce is that nobody can
            // produce the next one, and a predictable challenge is a challenge an attacker signs in
            // advance.
            Challenge = RandomNumberGenerator.GetBytes(ChallengeLength),
            Ceremony = ceremony,
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + ChallengeLifetime,
        };

        dbContext.WebAuthnChallenges.Add(row);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new IssuedChallenge(row.Challenge, row.ExpiresAtUtc);
    }

    /// <inheritdoc />
    public async Task<WebAuthnCeremony?> ConsumeAsync(
        ReadOnlyMemory<byte> challenge,
        CancellationToken cancellationToken = default)
    {
        // Materialised because the column is bytea and the parameter is a view over a buffer the caller
        // owns; comparing the array is also what lets the unique index on challenge serve the lookup.
        byte[] bytes = challenge.ToArray();
        DateTime nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        // SingleOrDefault, because the index over this column is unique: two rows carrying one nonce
        // would mean spending one of them left the other spendable, which is the single fact this type
        // exists to prevent.
        WebAuthnChallengeRow? row = await dbContext.WebAuthnChallenges
            .SingleOrDefaultAsync(candidate => candidate.Challenge == bytes, cancellationToken);

        // An expired row is left where it is for the sweep to collect. Deleting it here would be a
        // write performed on behalf of a caller presenting bytes that are already worthless, and the
        // answer it gets is the same either way.
        if (row is null || row.ExpiresAtUtc <= nowUtc)
        {
            return null;
        }

        dbContext.WebAuthnChallenges.Remove(row);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // Two callers presenting the same bytes at once both read the row and both try to delete it;
        // the loser's DELETE matches nothing and EF reports it as a concurrency failure. That loser is
        // in exactly the position of a caller replaying a spent nonce, and it is told exactly what that
        // caller is told. The indistinguishability is deliberate and runs further than this catch: a
        // challenge that was never issued, one already spent, one lost to a race and one that expired
        // all return null. No caller has a decision that depends on telling them apart — and an
        // attacker probing bytes would, since "already spent" confirms the value was once real.
        catch (DbUpdateConcurrencyException)
        {
            // The tracked instance is stale in a way the identity map would otherwise hand back to a
            // later read on this scoped context.
            dbContext.Entry(row).State = EntityState.Detached;

            return null;
        }

        return row.Ceremony;
    }

    // Opportunistic rather than scheduled, and the reason it has to exist at all is that the leg
    // issuing an authentication challenge is unauthenticated: this is the one table in the schema any
    // caller can make the application role insert into. Nothing rate-limits that — the product has no
    // rate limiting — so what bounds growth is the five-minute lifetime and this sweep, an accepted gap
    // rather than a solved problem, recorded as such in docs/decisions/0012.
    //
    // A read-then-RemoveRange rather than a set-based delete because ExecuteDeleteAsync is banned by
    // BannedSymbols.txt, and that ban is not being argued with here: the rows are few, short-lived and
    // capped by SweepBatchSize, so loading a page of them costs a round trip and nothing else.
    private async Task SweepExpiredAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        List<WebAuthnChallengeRow> expired = await dbContext.WebAuthnChallenges
            .Where(row => row.ExpiresAtUtc <= nowUtc)
            .OrderBy(row => row.ExpiresAtUtc)
            .Take(SweepBatchSize)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return;
        }

        dbContext.WebAuthnChallenges.RemoveRange(expired);

        try
        {
            // Saved on its own, before the insert rather than with it, so that a lost sweep cannot take
            // the new challenge down with it: two concurrent callers sweeping the same page would leave
            // one of them deleting rows that are already gone, and in a single save that failure would
            // fail the ceremony too.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another caller collected these first, which is the sweep succeeding by other means.
            // Detaching is what stops the deletions being retried on the save that follows.
            //
            // That rows-already-gone race is all this catch covers. Two sweeps deleting overlapping
            // pages in a different order can instead deadlock — the OrderBy above does not break ties on
            // expires_at_utc — and PostgreSQL reports 40P01 as a DbUpdateException, which passes this
            // catch by. That case is handled a layer up rather than here: nothing on this path opens a
            // transaction, so the save runs under the NpgsqlRetryingExecutionStrategy the API's
            // EnrichAzureNpgsqlDbContext installs, which classifies 40P01 as transient and replays it.
            foreach (WebAuthnChallengeRow row in expired)
            {
                dbContext.Entry(row).State = EntityState.Detached;
            }
        }
    }
}
