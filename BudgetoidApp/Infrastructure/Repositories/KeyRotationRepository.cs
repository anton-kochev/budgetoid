using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Infrastructure.Repositories;

public sealed class KeyRotationRepository(BudgetoidDbContext dbContext) : IKeyRotationRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Credential>> ListPasskeyFactorsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // THE OWNER PREDICATE IS WRITTEN RATHER THAN LEFT TO THE POLICY, for the reason
        // ExportReadService gives about its own budgets predicate: user_isolation makes a query that
        // lost its scoping answer EMPTY, not correct, and empty is the dangerous answer here — an empty
        // factor listing refuses a begin the account is entitled to make, and does it with a 400 about
        // the factor the client named.
        //
        // THE TYPE PREDICATE READS credential_type OFF wrapped_account_keys RATHER THAN OFF THE JOINED
        // credentials ROW. The column is on this table precisely so a filter like this one does not
        // have to trust the join to be scoped, and the composite foreign key over
        // (credential_id, user_id, credential_type) is what makes the two copies unable to disagree.
        // Reading it off credentials instead would put the whole predicate on an EXEMPT table — that
        // one carries no policy at all — and leave this read narrowed by nothing the database enforces.
        //
        // NOTHING MATERIALISES A wrapped_account_keys ENTITY, and that is a rule rather than a
        // projection preference. The application role holds NO DELETE on that table, so a tracked row
        // EF later decides to cascade into dies with 42501 — the never-materialise rule
        // GenerateRecoveryCodesHandler carries at length and IAccountKeyReadService was shaped around.
        // The factor identifier is projected out and the row itself never becomes an object.
        //
        // AsNoTracking on the credential half for the same family of reasons and one of its own: the
        // Credential travels to KeyRotation.Begin, which reads its owner and its type and mutates
        // nothing, so tracking it would buy an entity sitting in the change tracker across a delegate
        // that gets replayed. Untracked, a replay finds the tracker holding exactly one thing — the
        // KeyRotation the handler built once.
        List<PasskeyFactor> factors = await dbContext.WrappedAccountKeys
            .AsNoTracking()
            .Where(keys => keys.UserId == userId && keys.CredentialType == CredentialType.Passkey)
            .Join(
                dbContext.Credentials,
                keys => keys.CredentialId,
                credential => credential.Id,
                (keys, credential) => new PasskeyFactor(keys.FactorId, credential))
            .ToListAsync(cancellationToken);

        // No duplicate key is reachable: factor_id is the primary key of wrapped_account_keys, so the
        // projection cannot answer the same factor twice. ToDictionary would throw rather than
        // overwrite if it ever did, which is the right direction — two credentials claiming one factor
        // is a database that has lost that key, not a case to pick a winner in.
        return factors.ToDictionary(factor => factor.FactorId, factor => factor.Credential);
    }

    /// <inheritdoc />
    public Task<KeyRotation?> FindStagedRotationAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // SingleOrDefault rather than FirstOrDefault: user_id is the PRIMARY KEY of key_rotations, so a
        // second row is not a case to choose between — it is a database that has lost the rule making
        // two concurrent rotations of one account unstorable. Find/FindAsync is a banned symbol, so the
        // primary-key read is spelled as a predicate like every other one in this assembly.
        dbContext.KeyRotations
            .SingleOrDefaultAsync(rotation => rotation.UserId == userId, cancellationToken);

    /// <inheritdoc />
    public async Task StageAsync(KeyRotation rotation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rotation);

        // AN UPSERT, BECAUSE THE PORT PROMISES REPLACEMENT. Begin is the repair path — a completion
        // that refuses because the live factor set moved is answered by a begin carrying the corrected
        // set — so a second begin has to go through. A blind Add passes every case in
        // BeginKeyRotationHandlerTests over an in-memory dictionary and meets 23505 on PK_key_rotations
        // the first time a person begins twice.
        //
        // FOUND AND UPDATED RATHER THAN DELETED AND RE-INSERTED, and the alternative fails two ways
        // rather than one. EF batches a delete and an insert of the same primary key without a
        // guaranteed order, so the pair is a coin flip on 23505; and key_rotations is deliberately
        // granted no DELETE, so the tidier-looking spelling would also need a privilege nothing else on
        // this path needs. What it does need is INSERT and UPDATE, the latter over rotation_id,
        // factor_id, wrapped_content_key, wrapped_index_key and started_at_utc — user_id is the primary
        // key and is never in the SET list.
        KeyRotation? staged = await dbContext.KeyRotations
            .SingleOrDefaultAsync(existing => existing.UserId == rotation.UserId, cancellationToken);

        if (staged is null)
        {
            // Add on an instance the tracker already holds as Unchanged — which is what an attempt the
            // database rolled back leaves behind — moves it back to Added rather than throwing, so the
            // replayed attempt re-issues the INSERT. That is why the port asks a caller for ONE
            // instance per begin: a fresh object here would be a second entity carrying the same
            // primary key while the first is still tracked, and EF refuses that by name.
            dbContext.KeyRotations.Add(rotation);
        }
        else if (!ReferenceEquals(staged, rotation))
        {
            EntityEntry<KeyRotation> entry = dbContext.Entry(staged);

            // SetValues copies every mapped scalar from the detached instance onto the tracked one,
            // which is how a row with private setters and no mutator is replaced without giving
            // KeyRotation a second way to be built. The key is copied too and is the same value, so
            // nothing about the row's identity moves.
            entry.CurrentValues.SetValues(rotation);

            // MARKED MODIFIED RATHER THAN LEFT TO CHANGE DETECTION, and this line is what makes a
            // replayed replacement converge. On the second attempt the tracked entity ALREADY carries
            // the new values — it was updated by the abandoned attempt, and EF does not refresh a
            // tracked entity from a later query — so change detection sees nothing to write, no
            // statement is emitted, and the row the database rolled back keeps the OLD generation's
            // envelopes with the request answering 200. Forcing the state writes every column again,
            // which is idempotent and correct on every attempt.
            entry.State = EntityState.Modified;
        }

        // One save, because a begin writes this row and nothing else. The transaction the caller opened
        // is what holds this write together with the rest of its unit of work; this save is what makes
        // the write happen inside it.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// One row of the factor listing, so the projection has a name instead of an anonymous type.
    /// </summary>
    /// <remarks>
    /// Declared rather than projected into an anonymous type because this repository would otherwise be
    /// the only place in the assembly forced to write <c>var</c>, and nested rather than public because
    /// it is a shape of one query rather than a thing the assembly offers.
    /// </remarks>
    private sealed record PasskeyFactor(Guid FactorId, Credential Credential);
}
