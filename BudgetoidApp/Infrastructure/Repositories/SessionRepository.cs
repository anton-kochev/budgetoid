using Domain.Sessions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Infrastructure.Repositories;

public sealed class SessionRepository(BudgetoidDbContext dbContext) : ISessionRepository
{
    private const int MaxRevocationAttempts = 3;

    /// <inheritdoc />
    public async Task AddAsync(Session session, CancellationToken cancellationToken = default)
    {
        dbContext.Sessions.Add(session);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> RevokeForCredentialAsync(
        Guid credentialId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        // A concurrent sweep of the same credential can revoke a row between this read and the save
        // below. revoked_at_utc is a concurrency token, so the loser's UPDATE matches nothing and
        // SaveChangesAsync throws rather than overwriting the instant access actually ended — and
        // because that save is one transaction, nothing this attempt stamped survives it. Re-reading
        // is therefore what establishes the count instead of guessing it: the rows the winner took
        // are revoked by the time the next read runs, so they fall out of the predicate and the
        // number returned is the number of sessions THIS call ended.
        //
        // The loop terminates because every attempt strictly shrinks the set of unrevoked sessions
        // for this credential — nothing re-opens one. The bound is there so a defect that breaks
        // that reasoning surfaces as an exception rather than as a hang.
        for (int attempt = 1; ; attempt++)
        {
            // CredentialId, never UserId. Revoking one credential must end only the sessions that
            // credential established and leave the account's other credentials signed in; a
            // predicate keyed on the user would sign the whole account out, and because every
            // session in a single-credential account has the same owner, every test but one would
            // still pass.
            //
            // sessions carries no BudgetIsolation query filter — it is user-owned, like Users,
            // Budgets and Credentials — so nothing in EF narrows this read to the person asking. The
            // user_isolation row-level security policy in the database is what does, which is why a
            // predicate wide enough to select the world still reaches only the signed-in person's
            // rows.
            List<Session> sessions = await dbContext.Sessions
                .Where(session => session.CredentialId == credentialId && session.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);

            if (sessions.Count == 0)
            {
                return 0;
            }

            // ExecuteUpdateAsync is banned by BannedSymbols.txt, and the ban buys correctness here
            // rather than merely uniformity: running the transition in the domain, per row, keeps
            // Session.Revoke's idempotence, so a re-run after a retry or a second report of the same
            // compromise leaves the first revocation instant standing. A set-based UPDATE would
            // rewrite every matched row's revoked_at_utc on every call, and its row count would
            // report that retry as if it had ended access a second time.
            foreach (Session session in sessions)
            {
                session.Revoke(revokedAtUtc);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxRevocationAttempts)
            {
                // The tracked instances hold values the database has since contradicted, and EF's
                // identity map would hand those same instances back to the read above rather than
                // the winner's row. Detaching them is what makes the next attempt observe reality.
                foreach (EntityEntry<Session> entry in dbContext.ChangeTracker.Entries<Session>().ToList())
                {
                    entry.State = EntityState.Detached;
                }

                continue;
            }

            // Every row loaded was unrevoked when read and was still unrevoked when the save matched
            // it, so this counts the sessions this call ended — excluding any a concurrent sweep
            // ended first.
            return sessions.Count;
        }
    }
}
