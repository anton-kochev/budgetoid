using Domain.Sessions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Infrastructure.Repositories;

public sealed class SessionRepository(BudgetoidDbContext dbContext) : ISessionRepository
{
    private const int MaxRevocationAttempts = 3;
    private const int MaxSweepAttempts = 3;
    private const int MaxRemovalAttempts = 3;

    /// <inheritdoc />
    public async Task AddAsync(
        Session session,
        SessionToken token,
        CancellationToken cancellationToken = default)
    {
        // ONE SaveChangesAsync FOR BOTH ROWS AND THE SWEEP BELOW. The sweep's half of that is argued
        // where it runs: a sign-in that failed to store must delete nothing, and a sweep that failed
        // must store no sign-in. The pair's half is older. EF
        // sends the two inserts inside one transaction — its own when nothing else has opened one, the
        // ambient one when a caller has — and orders them from the foreign key, so the session is
        // written before the handle that references it whichever order they were added in. What the
        // single save buys is the other direction: there is no window in which one of them is committed
        // and the other is not, on any path, including one that throws between these two lines because
        // neither has reached the database yet.
        //
        // The alternative a reader will reach for is a second repository call, and it fails on both
        // shapes it can take. Two saves inside the caller's transaction are atomic by accident — they
        // are atomic because somebody remembered the transaction, and the first caller who forgets
        // leaves a session nobody can present or a handle naming nothing. Two saves without one are not
        // atomic at all.
        dbContext.Sessions.Add(session);
        dbContext.SessionTokens.Add(token);

        // A concurrent sign-in on the same account reads the same ended rows and can delete one
        // between this read and the save below; a concurrent credential revocation can stamp an
        // expired one, and revoked_at_utc is a concurrency token, so the DELETE carries
        // "revoked_at_utc IS NULL" and matches nothing. Either way EF raises
        // DbUpdateConcurrencyException, and the save is one transaction — EF's own, or a savepoint
        // inside the caller's — so nothing this attempt wrote survives it, the new session included.
        // Re-reading is what converges: a deleted row is gone from the next read, and a row revoked
        // under us is read back revoked and is still ended.
        //
        // The bound is the same one RevokeForCredentialAsync carries, for the same reason: a row can
        // be taken from this sweep at most once, so the loop shrinks toward a set nobody else is
        // touching, and a defect that broke that reasoning surfaces as an exception rather than a hang.
        for (int attempt = 1; ; attempt++)
        {
            // The whole set and NO owner predicate. sessions is policed by user_isolation, so this read
            // reaches only the account the caller published — which is the new session's owner, on
            // every path that calls this. An owner filter above the policy would be a second source of
            // tenancy able to disagree with it.
            //
            // Filtered in memory through Session.IsActiveAt rather than in SQL, so "ended" has one
            // spelling — the one that decides whether a request is authenticated — and is judged at the
            // new session's own CreatedAtUtc, the instant this establishment was decided at, rather than
            // at a second read of a clock. The live rows ride along; they are the account's live
            // sessions, a handful at most, because every establishment leaves only those behind.
            //
            // Loaded and removed, because ExecuteDelete is banned (BannedSymbols.txt). The handles leave
            // by the database's ON DELETE CASCADE from sessions, which runs as the table owner — the
            // role holds no DELETE on session_tokens, so a SessionToken tracked in this context under a
            // removed session would make EF send its own DELETE and die with 42501. That is why
            // SessionTokenRepository.FindByTokenHashAsync reads untracked.
            List<Session> accountSessions = await dbContext.Sessions.ToListAsync(cancellationToken);
            foreach (Session ended in accountSessions.Where(
                         candidate => !candidate.IsActiveAt(session.CreatedAtUtc)))
            {
                dbContext.Sessions.Remove(ended);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);

                return;
            }
            // Narrowed by the entries, the shape RevokeAsync uses: SaveChangesAsync flushes everything
            // the scoped context is tracking, so a stranger's entity conflicting on the same save must
            // propagate rather than be retried as a raced sweep. The count test stops an exception EF
            // could not attribute to any entry from satisfying the predicate vacuously.
            catch (DbUpdateConcurrencyException exception) when (
                attempt < MaxSweepAttempts
                && exception.Entries.Count > 0
                && exception.Entries.All(entry =>
                    entry.Entity is Session && entry.State == EntityState.Deleted))
            {
                // Every removal this attempt queued, not only the one EF reported: they hold values read
                // before the race, and EF's identity map would hand those same instances back to the read
                // above rather than the rows as they now stand. The new session and its handle stay
                // Added — only Deleted entries are touched — so the next attempt writes them again.
                foreach (EntityEntry<Session> entry in dbContext.ChangeTracker.Entries<Session>()
                             .Where(entry => entry.State == EntityState.Deleted)
                             .ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    /// <inheritdoc />
    public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // The id alone, and NO owner predicate — the rule RevokeAsync below spells out at length.
        // sessions is policed by user_isolation, so the app.current_user_id the caller published a
        // moment ago is appended to this read underneath. A session belonging to somebody else is not
        // found here, and a connection that has published nobody does not read the wrong row: it fails
        // with 22P02, because an unset setting reaches the policy as ''::uuid.
        //
        // SingleOrDefault rather than FirstOrDefault: id is the primary key, so a second row is a
        // database that has lost that rule rather than a case to choose between.
        //
        // AsNoTracking, which is the exception in this repository and is the point rather than a habit
        // borrowed from the read services. This read runs on EVERY authenticated request, before the
        // handler the request was made for has started, and it writes nothing. Tracked, the entity
        // would sit in the change tracker for the rest of the request and join whatever that request
        // then saves. EF cascades into session rows it happens to be holding when a credential is
        // removed and sends its own DELETE FROM sessions — which no longer fails, because the role holds
        // DELETE there for the ended-session sweep and for displacement, so the mistake is now silent:
        // the row leaves by the application rather than by the database's cascade, and nothing says so.
        // Every path that
        // removes a credential or an account clears the tracker immediately before the delete, so
        // nothing depends on this today; leaving this untracked is what keeps the next one from having
        // to remember.
        return dbContext.Sessions
            .AsNoTracking()
            .SingleOrDefaultAsync(session => session.Id == sessionId, cancellationToken);
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

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(
        Guid sessionId,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        // The id and the unrevoked predicate, and NO owner predicate — which is the opposite of the
        // rule the exempt tables keep, for the opposite reason. sessions is POLICED by user_isolation,
        // so PostgreSQL appends user_id = current_setting('app.current_user_id') to this read and to
        // the update below: another person's session is not found here and could not be written if it
        // were. Adding a filter above that would be a second source of tenancy able to disagree with
        // the policy.
        //
        // The unrevoked half is where Session.Revoke's idempotence becomes an answer rather than a
        // no-op: a session somebody already ended falls out of the predicate, so this call reports
        // ending nothing instead of silently re-stamping — the same reading RevokeForCredentialAsync's
        // count carries.
        //
        // SingleOrDefault rather than FirstOrDefault: id is the primary key, so a second row is a
        // database that has lost that rule rather than a case to choose between.
        Session? session = await dbContext.Sessions
            .SingleOrDefaultAsync(
                session => session.Id == sessionId && session.RevokedAtUtc == null,
                cancellationToken);

        if (session is null)
        {
            return false;
        }

        session.Revoke(revokedAtUtc);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // A concurrent revocation of the same session — a retried request, or this one racing the
        // credential sweep above. revoked_at_utc is a concurrency token, so the loser's UPDATE carries
        // "and revoked_at_utc is null", matches nothing and raises rather than overwriting the instant
        // access actually ended. There is nothing to retry: the session IS revoked, by somebody else,
        // and the honest answer is that this call ended nothing. That is why this catch returns where
        // RevokeForCredentialAsync's re-reads — that one has to establish a COUNT across rows a winner
        // may have taken some of, and this one has a single row whose fate the exception already
        // settles.
        //
        // Narrowed BY THE ENTRIES, the shape every translated conflict in this folder uses:
        // SaveChangesAsync flushes everything the scoped context is tracking, so a stranger's entity
        // conflicting on the same save must propagate rather than be reported as an already-revoked
        // session. The count test is not redundant — an exception EF could not attribute to any entry
        // would otherwise satisfy the predicate vacuously.
        catch (DbUpdateConcurrencyException exception) when (
            exception.Entries.Count > 0
            && exception.Entries.All(entry =>
                entry.Entity is Session && entry.State == EntityState.Modified))
        {
            // The tracked instance holds a revocation instant the database has contradicted, and it is
            // still Modified: leaving it there would make the next SaveChangesAsync on this scoped
            // context replay the same failed UPDATE and throw again, in a caller that has nothing to do
            // with revoking anything. Detaching is what confines the conflict to this call.
            foreach (EntityEntry entry in exception.Entries)
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // A concurrent sign-out can stamp the row between this read and the save. revoked_at_utc is a
        // concurrency token, so the DELETE of a row read live carries "revoked_at_utc IS NULL", matches
        // nothing, and EF raises DbUpdateConcurrencyException. Unlike RevokeAsync, the exception does
        // not settle the answer here: that call wanted the row revoked and somebody else revoked it,
        // while this one wants it GONE, and a revoked row is still standing. So it re-reads, the shape
        // the sweep in AddAsync uses: a row revoked under us comes back revoked and is removed, and a
        // row a concurrent removal took is not found and answers false. Measured for the revocation:
        // without the retry, or without the detach below, RemoveAsync_WhenRevokedConcurrently_StillRemovesIt
        // fails with DbUpdateConcurrencyException. The concurrent-removal arm is reasoned, not run.
        //
        // The bound is the sweep's, for the sweep's reason: the only competing writes are a revocation,
        // which happens to a row at most once, and a delete, after which the read finds nothing — so a
        // defect that broke that reasoning surfaces as an exception rather than a hang.
        for (int attempt = 1; ; attempt++)
        {
            // The id alone, live or ended, and NO owner predicate — the rule RevokeAsync spells out.
            // sessions is policed by user_isolation, so a session of any account but the published one
            // is not found and cannot be deleted. Tracked, because Remove needs a tracked entity and
            // ExecuteDelete is banned (BannedSymbols.txt).
            Session? session = await dbContext.Sessions
                .SingleOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);

            if (session is null)
            {
                return false;
            }

            // The handle leaves by the database's ON DELETE CASCADE from sessions, never by EF: the role
            // holds no DELETE on session_tokens, so a SessionToken tracked in this context under this
            // session would make EF send its own DELETE and die with 42501. Nothing on the path that
            // calls this tracks one — SessionTokenRepository.FindByTokenHashAsync reads untracked.
            dbContext.Sessions.Remove(session);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);

                return true;
            }
            // Narrowed by the entries, the shape RevokeAsync uses: SaveChangesAsync flushes everything
            // the scoped context is tracking, so a stranger's entity conflicting on the same save must
            // propagate rather than be retried as a raced removal. The count test stops an exception EF
            // could not attribute to any entry from satisfying the predicate vacuously.
            catch (DbUpdateConcurrencyException exception) when (
                attempt < MaxRemovalAttempts
                && exception.Entries.Count > 0
                && exception.Entries.All(entry =>
                    entry.Entity is Session && entry.State == EntityState.Deleted))
            {
                // The removed instance holds the values read before the race, and EF's identity map
                // would hand that same instance back to the read above rather than the row as it now
                // stands. Detaching it is what makes the next attempt observe reality.
                foreach (EntityEntry entry in exception.Entries)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }
}
