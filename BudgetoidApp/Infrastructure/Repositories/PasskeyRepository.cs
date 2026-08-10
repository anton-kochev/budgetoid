using Domain.Common;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class PasskeyRepository(BudgetoidDbContext dbContext) : IPasskeyRepository
{
    /// <inheritdoc />
    public Task<PasskeyPublicKey?> FindByWebAuthnCredentialIdAsync(
        ReadOnlyMemory<byte> webAuthnCredentialId,
        CancellationToken cancellationToken = default) =>
        // The discovery lookup, and the one query in the codebase allowed to read passkey_public_keys
        // without naming an owner. An assertion arrives carrying a credential id and a signature and
        // nothing else, so this statement runs before the request has an identity, on a connection
        // whose app.current_user_id is still ''. Naming an owner here is impossible — the owner is
        // what the answer establishes — and touching any policed table would fail with 22P02. That is
        // the whole reason the table is exempt from row-level security; see docs/decisions/0012.
        //
        // SingleOrDefault rather than FirstOrDefault: the unique index on webauthn_credential_id is
        // what makes one authenticator handle resolve to one account, and a second row would mean that
        // rule has been lost — leaving this read with two accounts and nothing to choose between them,
        // which is a broken database rather than a sign-in it can answer honestly.
        //
        // Equals rather than ==, because ReadOnlyMemory<byte> declares no equality operator. In C# it
        // would be the struct's own comparison — buffer, offset and length — but this is an expression
        // tree the provider reads, and what reaches PostgreSQL is a bytea comparison through the
        // property's value converter, matching content rather than identity.
        dbContext.PasskeyPublicKeys
            .SingleOrDefaultAsync(
                publicKey => publicKey.WebAuthnCredentialId.Equals(webAuthnCredentialId),
                cancellationToken);

    /// <inheritdoc />
    public Task<PasskeyPublicKey?> FindByWebAuthnCredentialIdForUserAsync(
        Guid userId,
        ReadOnlyMemory<byte> webAuthnCredentialId,
        CancellationToken cancellationToken = default) =>
        // The same handle predicate as the discovery lookup above, with the owner filter that one is
        // the codebase's single exception to. The difference is not caution: this statement runs with
        // an identity already published, so there IS an owner to name, and passkey_public_keys is
        // exempt from row-level security — nothing beneath this line narrows the read to the person
        // asking. Drop the user_id predicate and this method becomes the discovery lookup, which
        // answers with a stranger's key and lets an assertion signed by somebody else's authenticator
        // verify against it.
        //
        // Two predicates, still SingleOrDefault: the unique index on webauthn_credential_id already
        // means one handle resolves to at most one row, and adding the owner can only narrow that. A
        // second row would mean the index has been lost, which is a broken database rather than a
        // question this read can answer honestly.
        dbContext.PasskeyPublicKeys
            .SingleOrDefaultAsync(
                publicKey => publicKey.UserId == userId
                             && publicKey.WebAuthnCredentialId.Equals(webAuthnCredentialId),
                cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadOnlyMemory<byte>>> ListWebAuthnCredentialIdsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // The owner filter is load-bearing, and its presence one method below a query that deliberately
        // omits one is the whole hazard of an exempt table: passkey_public_keys carries no
        // user_isolation policy and no BudgetIsolation query filter, so nothing beneath this line
        // narrows the read to the person asking — the application is the only thing scoping it. The
        // precedent is BudgetRepository.FindFirstForUserAsync, which scopes budgets explicitly for the
        // same reason.
        //
        // The read above may omit the filter because it runs before there is an owner to name. This one
        // runs with an identity already established, so omitting it would hand a registration ceremony
        // every handle in the table — offering one account the credential ids of every other, which is
        // the enumeration the exemption was argued as not permitting.
        await dbContext.PasskeyPublicKeys
            .Where(publicKey => publicKey.UserId == userId)
            .Select(publicKey => publicKey.WebAuthnCredentialId)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(
        Credential credential,
        PasskeyPublicKey publicKey,
        PasskeySignatureCounter counter,
        CancellationToken cancellationToken = default)
    {
        dbContext.Credentials.Add(credential);
        dbContext.PasskeyPublicKeys.Add(publicKey);
        dbContext.PasskeySignatureCounters.Add(counter);

        try
        {
            // One save, so the three rows land together or not at all. A credential without its public
            // key would be a passkey nothing can verify a signature against, and a key without its
            // counter would be a passkey whose clone detection silently never runs.
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Filtered on the constraint name, never on the SQLSTATE alone: three rows are written here and
        // each carries unique rules of its own, so a 23505 says only that some rule was broken. Naming
        // the index is what makes this catch mean the one thing the caller can act on — that handle is
        // already registered. Any other unique violation propagates on purpose, because it is a
        // constraint this method does not model and a 500 naming it is more useful than a false
        // "already registered".
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PasskeyPublicKeyConfiguration.WebAuthnCredentialIdIndexName,
        })
        {
            // Detached so the rejected rows cannot ride along on a later save through the same scoped
            // context, which would report the refusal a second time from somewhere unrelated.
            dbContext.Entry(credential).State = EntityState.Detached;
            dbContext.Entry(publicKey).State = EntityState.Detached;
            dbContext.Entry(counter).State = EntityState.Detached;

            return false;
        }
    }

    /// <inheritdoc />
    public Task<Credential?> FindPasskeyCredentialAsync(
        Guid credentialId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // Both keys in the predicate, and the owner one is not belt-and-braces: credentials carries no
        // user_isolation policy — it is the table read to answer who is asking — so unlike the counter
        // read below, nothing under this line scopes the statement to the signed-in person. The type
        // predicate is the third: a federated credential resolved here would open a session claiming a
        // passkey established it.
        //
        // SingleOrDefault because the predicate names the primary key; two rows would mean the key has
        // been lost.
        dbContext.Credentials
            .SingleOrDefaultAsync(
                credential => credential.Id == credentialId
                              && credential.UserId == userId
                              && credential.Type == CredentialType.Passkey,
                cancellationToken);

    /// <inheritdoc />
    public Task<int> CountPasskeysForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // The owner predicate is not belt-and-braces, for the reason the lookup above gives about its
        // own: credentials is exempt from row-level security, so no policy and no query filter narrows
        // this read. Without it the floor would be measured against every passkey in the table, a
        // number that never falls to one and therefore never refuses anything.
        //
        // The type predicate is the rule itself. The account's federated Google credential is a row
        // here too, so an untyped count reads two for an account holding exactly one passkey.
        dbContext.Credentials
            .CountAsync(
                credential => credential.UserId == userId
                              && credential.Type == CredentialType.Passkey,
                cancellationToken);

    /// <inheritdoc />
    public async Task DeletePasskeyAsync(
        Credential credential,
        CancellationToken cancellationToken = default)
    {
        // Through the change tracker, and there is no alternative to weigh: ExecuteDelete is a compile
        // error under BannedSymbols.txt, and rightly — a statement carrying its own owner predicate
        // would put the scope where a reader expects it while bypassing the tracker the calling
        // handler depends on having emptied.
        //
        // No owner predicate here either, and none is missing: the scope arrived with the argument.
        // The entity was resolved by FindPasskeyCredentialAsync, whose predicate named the owner and
        // the type — and that is the only read in this class returning a Credential the table actually
        // holds. Credential.CreateFederated and CreatePasskey are public, so an instance can certainly
        // be made elsewhere; each mints its own Guid.CreateVersion7(), so the row it names does not
        // exist and Remove raises on a zero-row DELETE instead of removing a stranger's. Restating the
        // owner here would be a second source of tenancy that could disagree with the first.
        //
        // What that leaves standing is this file: add a query above that returns a Credential without
        // an owner filter and this delete is scoped by nothing at all, with no policy beneath it to
        // notice — docs/decisions/0014 names that as the one thing review has to catch.
        //
        // Remove on the one row, never on its children: passkey_public_keys,
        // passkey_signature_counters and sessions leave by the database's own cascade from this row,
        // and the role holds no DELETE on any of them.
        dbContext.Credentials.Remove(credential);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The row went out from under this request between the caller's lookup and this save: another
        // revocation of the same credential committed first, so the DELETE matched zero rows where EF
        // expected one. A person double-tapping the button on a slow connection is enough to produce
        // it, and left alone it surfaces as a 500 logged as a fault — a lie about what happened,
        // because the passkey the caller asked to have removed is gone and the only thing that went
        // wrong is that somebody else removed it first.
        //
        // 404, and not the two answers a reader will reach for instead:
        //
        // - Not a RETRY. The retry would re-run the lookup, find nothing, and raise this same
        //   NotFoundException one round trip later. There is no other outcome to reach, because the
        //   row is not coming back — this is not the transient failure ITransactionalExecutor's
        //   execution strategy replays for.
        // - Not a 409. A conflict says the work could not be done and invites another attempt; here it
        //   WAS done, and inviting a retry at a completed removal can only end in a 404 anyway.
        //
        // 404 is also what the caller's own lookup would have answered a moment earlier had it run
        // after the winner's delete instead of before, which is why the message below is the one
        // RevokePasskeyHandler's lookup miss uses, verbatim: the two orderings of the same pair of
        // requests become indistinguishable to the caller, and that is what makes a client's retry
        // safe. Change one message and change both.
        //
        // Narrowed by the entries, the same way UserRepository.DeleteAsync and
        // TransactionRepository.DeleteAllForAmbientBudgetAsync narrow theirs: a concurrency conflict
        // carries no SQLSTATE, so "every conflicting row is a credentials row this call itself marked
        // Deleted" is this method's equivalent of the constraint-name filter
        // RepositoryConstraintAttributionTests requires elsewhere in this folder. A conflict over some
        // other entity riding along on the same SaveChanges is a failure this method does not model,
        // and reporting it as a missing passkey would be the same lie in the other direction.
        //
        // No detach on the way out, unlike those two: they swallow and let the request carry on with a
        // context that still holds Deleted entries, while this one throws. The unit of work unwinds,
        // and the enclosing ITransactionalExecutor does not replay a NotFoundException.
        catch (DbUpdateConcurrencyException exception) when (IsAlreadyDeleted(exception))
        {
            throw new NotFoundException("Passkey was not found.");
        }
    }

    /// <summary>
    /// True when the conflict is only about <see cref="Credential"/> rows this call removed. The count
    /// test is not redundant: an exception EF could not attribute to any entry would otherwise satisfy
    /// the predicate vacuously.
    /// </summary>
    private static bool IsAlreadyDeleted(DbUpdateConcurrencyException exception) =>
        exception.Entries.Count > 0
        && exception.Entries.All(entry =>
            entry.Entity is Credential && entry.State == EntityState.Deleted);

    /// <inheritdoc />
    public Task<PasskeySignatureCounter?> FindCounterAsync(
        Guid credentialId,
        CancellationToken cancellationToken = default) =>
        // No owner filter, and the difference from the read above is the point: this table carries
        // user_id and is policed by user_isolation, so the database narrows the statement to the
        // signed-in person. Re-filtering here would add a second source of tenancy that could disagree
        // with the policy. The counter is read only after the signature has verified and the identity
        // has been published, which is what makes the ambient user available at all —
        // docs/decisions/0012 records that ordering.
        dbContext.PasskeySignatureCounters
            .SingleOrDefaultAsync(counter => counter.CredentialId == credentialId, cancellationToken);

    /// <inheritdoc />
    public async Task SaveCounterAsync(
        PasskeySignatureCounter counter,
        CancellationToken cancellationToken = default) =>
        // The entity arrives already mutated by PasskeySignatureCounter.Accept, so this only flushes
        // what the domain decided. Nothing here restates which column may move: the role holds
        // GRANT UPDATE (signature_counter) and nothing wider, so an UPDATE touching any other column
        // is refused by the database rather than by a rule written twice.
        await dbContext.SaveChangesAsync(cancellationToken);
}
