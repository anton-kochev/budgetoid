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
