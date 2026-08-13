using Application.RecoveryCodes;
using Domain.Common;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class RecoveryCodeRepository(BudgetoidDbContext dbContext) : IRecoveryCodeRepository
{
    // ONE MESSAGE FOR BOTH HALVES OF ONE RACE, and the sameness is the point rather than reuse. Two
    // requests issuing codes for one account — a double-clicked button, a client retry, two open tabs —
    // lose in one of two places depending on whether the account already held a set, and the caller's
    // situation is identical either way: this attempt wrote nothing, somebody else's set is the
    // account's, and the codes this client has already shown a person will never redeem. Two
    // sentences would let a caller tell "you had a set" from "you had none", which is a fact about the
    // account's prior state that a losing request has no business learning and no use for.
    //
    // It says what to do next, because a 409 with no detail tells a client nothing about whether to
    // retry. The fresh assertion is not a formality: this attempt's nonce was consumed by the gate
    // before the transaction opened, so a retry replaying it is refused with the gate's 401.
    private const string LostTheRaceMessage =
        "Another request replaced this account's recovery codes. Present a fresh re-authentication and "
        + "generate them again.";

    // ONE FACT ABOUT ONE TABLE, REACHED FROM TWO ROUTES. The identical sentence lives on
    // PasskeyRepository.TryAddAsync, which is the other write of wrapped_account_keys: a factor
    // identifier is unique across the whole table, so a recovery-code generation and a passkey
    // registration collide on the same index and the caller's situation is the same either way — nothing
    // was written, and the identifier their client chose is spoken for. Change one message and change
    // both.
    //
    // It says what to do next, because a 409 with no detail leaves a client with no idea whether to
    // retry. Minting a fresh identifier is not optional advice: the envelopes were sealed with the old
    // one as their associated data, so they have to be re-wrapped rather than re-sent. And the ceremony
    // has to run again either way, because this attempt's nonce is already spent.
    private const string FactorAlreadyRegisteredMessage =
        "That factor identifier is already registered. Mint a fresh one, wrap the account keys under it, "
        + "and run the ceremony again.";

    /// <inheritdoc />
    public Task<Credential?> FindRecoveryCodeCredentialAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // BOTH predicates are the scope, and neither is belt-and-braces. credentials is exempt from
        // row-level security (ADR 0011) — it is the table read to answer who is asking — so no policy
        // and no query filter narrows this statement to the person asking.
        //
        // This read produces a Credential naming a row the table actually holds — the shape
        // PasskeyRepository.FindPasskeyCredentialAsync also has — and ADR 0014 names exactly that as
        // the thing review has to catch: the entity travels on to DeleteSetAsync, so
        // whatever scopes this read is what scopes that delete. It holds here because the owner is in
        // the predicate. Adding a lookup to this class that returns a Credential without naming its
        // owner is what would break it.
        //
        // The type predicate is a rule of its own: the account's federated Google credential is a row
        // in this table too, and so is every passkey it has registered, so an untyped lookup would hand
        // the caller one of those and offer it to a delete.
        //
        // SingleOrDefault rather than FirstOrDefault: IX_credentials_user_id_recovery_codes is what
        // makes an account hold at most one set, and a second row would mean that rule has been lost —
        // leaving this read with two sets and nothing to choose between them, which is a broken
        // database rather than a question it can answer honestly.
        dbContext.Credentials
            .SingleOrDefaultAsync(
                credential => credential.UserId == userId
                              && credential.Type == CredentialType.RecoveryCodes,
                cancellationToken);

    /// <inheritdoc />
    public Task<RecoveryCodeHash?> FindByVerifierHashAsync(
        ReadOnlyMemory<byte> verifierHash,
        CancellationToken cancellationToken = default) =>
        // THE DISCOVERY LOOKUP, the shape PasskeyRepository.FindByWebAuthnCredentialIdAsync and
        // DbWebAuthnChallengeStore.ConsumeAsync also have. It names no owner because there is no owner
        // to name: a redemption arrives anonymous, on a connection whose app.current_user_id is still
        // '', and whose account it belongs to is what this answer establishes. That is the whole reason
        // recovery_code_hashes is exempt from row-level security (ADR 0016) — a user_isolation policy
        // here would compare against ''::uuid and raise 22P02 on every redemption — and it is why this
        // statement may touch no second table. A join to users or to credentials would put a policed
        // relation on the same identity-less connection and fail for exactly that reason.
        //
        // WHAT THIS ESTABLISHES IS AN ACCOUNT, NOT A ROW TO SPEND. The redemption handler takes the
        // user_id off the answer and publishes it, then reads the row it removes through
        // FindOwnedByVerifierHashAsync below, which names that owner. So the entity this returns never
        // has to survive as far as a DELETE, and the one statement on this path that runs unscoped is
        // not the one that scopes a write.
        //
        // SingleOrDefault rather than FirstOrDefault: verifier_hash is the PRIMARY KEY, so a second row
        // is not a case to choose between, it is a database that has lost the rule making two codes
        // hashing alike unstorable.
        //
        // Equals rather than ==, for the reason FindByWebAuthnCredentialIdAsync gives: ReadOnlyMemory
        // declares no equality operator, and what reaches PostgreSQL through the property's value
        // converter is a bytea comparison of content rather than of buffer identity.
        dbContext.RecoveryCodeHashes
            .SingleOrDefaultAsync(hash => hash.VerifierHash.Equals(verifierHash), cancellationToken);

    /// <inheritdoc />
    public Task<RecoveryCodeHash?> FindOwnedByVerifierHashAsync(
        Guid userId,
        ReadOnlyMemory<byte> verifierHash,
        CancellationToken cancellationToken = default) =>
        // THE SCOPED READ, and the owner predicate is what makes it a different statement from the
        // discovery lookup above rather than a second copy of it. recovery_code_hashes is exempt from
        // row-level security (ADR 0016), so no policy and no query filter narrows either one: an exempt
        // table scopes nothing, and only the statement that establishes the identity may run without an
        // owner. This one runs after that identity is published, so it has an owner to name and no
        // reason to omit it — ADR 0011.
        //
        // BOTH predicates are the scope, in the sense FindRecoveryCodeCredentialAsync's pair is. The
        // hash alone would select the same row today, because it is the PRIMARY KEY and
        // credentials.user_id is immutable; what the owner buys is that the row this hands to
        // ConsumeAsync belongs to the account the caller has published, by predicate rather than by that
        // argument. Without it the delete below would be scoped by the application and by nothing
        // beneath it, on a statement that never mentions whose row it takes.
        //
        // NO TRACKING IS NOT WANTED HERE, unlike on the count next door: the entity this returns is the
        // one ConsumeAsync removes, and a detached instance would have to be re-attached to be deleted.
        //
        // SingleOrDefault and Equals for the two reasons written out above.
        dbContext.RecoveryCodeHashes
            .SingleOrDefaultAsync(
                hash => hash.UserId == userId && hash.VerifierHash.Equals(verifierHash),
                cancellationToken);

    /// <inheritdoc />
    public async Task ConsumeAsync(RecoveryCodeHash hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hash);

        // Through the change tracker, and there is no alternative to weigh: ExecuteDelete is a compile
        // error under BannedSymbols.txt.
        //
        // No owner predicate here, and none is missing: the scope arrived with the argument. The entity
        // came from FindOwnedByVerifierHashAsync in this same unit of work — the third of ADR 0014's
        // three legs — so the row removed is the row read, and that read named both the account and the
        // hash. Restating the owner here would be a second source of tenancy that could disagree with
        // the first, which is the reason DeleteSetAsync gives for not restating its own.
        //
        // Remove on the one code, never on the set's credential: deleting that row would cascade away
        // the session this very redemption is about to open, and would be an anonymous request removing
        // a credentials row that nothing beneath the application polices. See
        // IRecoveryCodeRepository.ConsumeAsync.
        dbContext.RecoveryCodeHashes.Remove(hash);

        // A save of its own rather than one shared with the session insert, so the ordering FR-054 asks
        // for is an ordering of STATEMENTS and not merely of C# lines. Both live inside the handler's
        // one transaction, so a failure after this point still takes the delete back — what the separate
        // save buys is that no reordering of the flush can put the session in front of the consume.
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // THE SAME CODE PRESENTED TWICE AT ONCE. Two requests carrying one verifier both find the row,
        // and the loser's DELETE matches nothing where EF expected one row. Left alone it is a 500 on an
        // anonymous route — and worse than a 500, it is a SECOND ANSWER: a caller able to tell "that
        // code went while you were asking" from "no such code" has learned the value they presented was
        // real, which is precisely what the byte-identical refusal exists to withhold. So it becomes
        // that refusal, and the losing request establishes no session, exactly as a caller replaying a
        // spent code a second later gets.
        //
        // Translated here rather than in the handler because DbUpdateConcurrencyException is EF's, and
        // the handler is in a layer that has never heard of it — the same reason the two catches below
        // translate their own. Narrowed BY THE ENTRIES, the shape DeleteSetAsync's catch uses:
        // SaveChangesAsync flushes everything the scoped context is tracking, so a stranger's entity
        // conflicting on the same save must propagate rather than be reported as a spent code.
        catch (DbUpdateConcurrencyException exception) when (IsAlreadyConsumed(exception))
        {
            throw new RecoveryCodeRedemptionException("The presented code was consumed by a concurrent redemption.");
        }
    }

    /// <inheritdoc />
    public async Task AddSetAsync(
        Credential credential,
        IReadOnlyList<RecoveryCodeHash> hashes,
        WrappedAccountKeys wrappedAccountKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentNullException.ThrowIfNull(wrappedAccountKeys);

        dbContext.Credentials.Add(credential);
        dbContext.RecoveryCodeHashes.AddRange(hashes);
        dbContext.WrappedAccountKeys.Add(wrappedAccountKeys);

        try
        {
            // One save, so the credential, its codes and its share of the account keys land together or
            // not at all. EF orders the statements from the foreign keys between the entity types, so
            // the credential is inserted before the rows whose composite keys reference it. A set filed
            // without its envelopes would be a card whose codes derive a key-encryption key with nothing
            // to open, and the person would find that out on the day they had nothing else left.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // THE LIKELIER HALF OF THE RACE, and the one no delete is involved in. Two concurrent FIRST
        // generations for one account — the double-clicked button on a fresh account — both find no
        // previous set, so neither reaches DeleteSetAsync at all: both insert, and the loser collides on
        // IX_credentials_user_id_recovery_codes with a 23505 that nothing above translates, so the
        // person is answered 500 having just been shown a set of codes that will never redeem.
        //
        // 409, exactly as the delete's own conflict, and for the reasons written out on that catch.
        //
        // NARROWED ON THE CONSTRAINT NAME, never on the SQLSTATE alone: SaveChangesAsync flushes
        // everything the scoped context is tracking, and this call alone adds the credential plus one row
        // per code, each carrying unique rules of its own. recovery_code_hashes is keyed on the verifier
        // hash, so a 23505 could just as well be two codes hashing alike, which is a different broken
        // rule with a different answer. A bare catch (PostgresException) would report any of them as a
        // lost race: a confident, specific, false 409. This is the filter
        // RepositoryConstraintAttributionTests requires of every repository in this folder that
        // translates anything, and PasskeyRepository.TryAddAsync's catch is its shape.
        //
        // No detach on the way out, unlike that one: it swallows and lets the caller carry on with the
        // same context, while this throws. The unit of work unwinds, and ITransactionalExecutor does not
        // replay a ConflictException.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CredentialConfiguration.RecoveryCodesPerUserIndexName,
        })
        {
            throw new ConflictException(LostTheRaceMessage);
        }
        // A DIFFERENT COLLISION WITH A DIFFERENT ANSWER, and it is not the race above wearing another
        // name. That one is two requests contending for the account's one set — a fact about timing,
        // which is why it tells the caller to prove presence again. This one is the client-minted factor
        // identifier already standing in wrapped_account_keys, which at 122 random bits is never chance:
        // it means an identifier reused or a request replayed. Telling such a caller "another request
        // replaced your codes" would send them looking for a second client they do not have.
        //
        // The message is PasskeyRepository.TryAddAsync's, verbatim — the same table reached from the
        // other route; see the constant above.
        //
        // NARROWED ON THE CONSTRAINT NAME for the reason the catch above is, and the need is greater
        // rather than equal: this save writes the credential, one row per code and the wrapped keys, each
        // carrying unique rules of its own, so a bare SQLSTATE catch would report a verifier-hash
        // collision or the one-set-per-account index as a factor-id conflict.
        // RepositoryConstraintAttributionTests pins that every translated exception in this folder names
        // its constraint.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: WrappedAccountKeysConfiguration.FactorIdIndexName,
        })
        {
            throw new ConflictException(FactorAlreadyRegisteredMessage);
        }
    }

    /// <inheritdoc />
    public async Task DeleteSetAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // Through the change tracker, and there is no alternative to weigh: ExecuteDelete is a compile
        // error under BannedSymbols.txt.
        //
        // No owner predicate here, and none is missing: the scope arrived with the argument. The entity
        // was resolved by FindRecoveryCodeCredentialAsync, whose predicate named the owner and the
        // type. Credential.CreateRecoveryCodes is public, so an instance can be made elsewhere; it
        // mints its own Guid.CreateVersion7(), so the row it names does not exist and Remove raises on
        // a zero-row DELETE instead of removing a stranger's set. Restating the owner here would be a
        // second source of tenancy that could disagree with the first.
        //
        // Remove on the one row, never on its codes: recovery_code_hashes rows leave by the database's
        // own cascade from this row, which runs with the referencing table owner's privileges. Nothing
        // on this path loads them, and it must stay that way — the role IS granted DELETE on that
        // table, so an EF cascade into tracked copies would silently succeed and take the rows by the
        // application instead, with no SQLSTATE to say so. See IRecoveryCodeRepository.DeleteSetAsync.
        dbContext.Credentials.Remove(credential);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        // The set went out from under this request between the caller's lookup and this save: another
        // generation for the same account committed first, so the DELETE matched zero rows where EF
        // expected one. Left alone it surfaces as a 500 logged as a fault, describing a removal that in
        // fact succeeded — on the request of somebody who is at that moment looking at a set of codes
        // that will never work.
        //
        // 409, and not the four answers a reader will reach for instead:
        //
        // - Not a 200. This request wrote no set: the winner's codes are the account's, and this
        //   caller's client is holding a set it has already shown a person. A success here is the one
        //   outcome that leaves somebody with a printed card that unlocks nothing and no way to find out.
        // - Not a 404, which is what the sibling path answers — PasskeyRepository.DeletePasskeyAsync
        //   turns this identical exception into NotFoundException. That is right there and wrong here,
        //   and the difference is what the caller named. A revocation names a credential in its route, so
        //   "that row is gone" is an answer about the thing asked for, and it makes the two orderings of
        //   the pair indistinguishable, which is what makes a client's retry safe. A generation names
        //   nothing: the resource it addresses — /api/me/recovery-codes — exists, so a 404 would be a
        //   false statement about the caller's own account.
        // - Not the gate's 401. The caller proved possession of an authenticator registered to this
        //   account; reporting a lost race as a failed proof sends a person to debug an authenticator
        //   that is working perfectly.
        // - Not swallowed and continued, in the shape UserRepository.DeleteAsync uses. The row is already
        //   gone, so carrying on means inserting this request's set beside the winner's —
        //   IX_credentials_user_id_recovery_codes permits one per account, so AddSetAsync's insert is
        //   refused with 23505 and the same conflict arrives one statement later and harder to read.
        //
        // So: 409 with a real sentence. The request conflicts with the state of the resource, it would
        // succeed unchanged if made again, and a retry is exactly what the client should do — with a
        // fresh assertion, since the nonce this attempt spent is spent. The sentence is legitimate for
        // the reason the whole validation family is: this is past the gate.
        //
        // Narrowed by the ENTRIES, the same way PasskeyRepository.DeletePasskeyAsync and
        // UserRepository.DeleteAsync narrow theirs: a concurrency conflict carries no SQLSTATE and no
        // constraint name, so "every conflicting row is a credentials row this call itself marked
        // Deleted" is this method's equivalent of the constraint-name filter above. SaveChangesAsync
        // flushes everything the scoped context is tracking, so a stranger's entity conflicting on the
        // same save must propagate — a 500 naming the real failure beats a confident, specific, false
        // "your recovery codes were replaced".
        //
        // No detach on the way out, for the reason the insert's catch gives.
        catch (DbUpdateConcurrencyException exception) when (IsAlreadyDeleted(exception))
        {
            throw new ConflictException(LostTheRaceMessage);
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

    /// <summary>
    /// True when the conflict is only about <see cref="RecoveryCodeHash"/> rows this call spent. The
    /// count test carries the weight <see cref="IsAlreadyDeleted"/>'s does: an exception EF could not
    /// attribute to any entry would otherwise satisfy the predicate vacuously and report an unrelated
    /// failure as a spent code.
    /// </summary>
    private static bool IsAlreadyConsumed(DbUpdateConcurrencyException exception) =>
        exception.Entries.Count > 0
        && exception.Entries.All(entry =>
            entry.Entity is RecoveryCodeHash && entry.State == EntityState.Deleted);
}
