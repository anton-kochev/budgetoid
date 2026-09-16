using Domain.Common;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public sealed class PasskeyRepository(BudgetoidDbContext dbContext) : IPasskeyRepository
{
    // ONE FACT ABOUT ONE TABLE, REACHED FROM TWO ROUTES. The identical sentence lives on
    // RecoveryCodeRepository.AddSetAsync, which is the other write of wrapped_account_keys: a factor
    // identifier is unique across the whole table, so a passkey registration and a recovery-code
    // generation collide on the same index and the caller's situation is the same either way — nothing
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

    // ONE FACT ABOUT ONE ROW, REACHED FROM THREE ROUTES: registration of a passkey and revocation of
    // one, both in this file, and RecoveryCodeRepository.AddSetAsync, where the identical sentence
    // lives. Every path that changes an account's factor set promotes its manifest, so every one of
    // them can lose this race. Change one message and change all of them.
    //
    // IT DOES NOT BLAME THE CALLER, AND THAT IS THE WHOLE CARE THIS SENTENCE NEEDS. Their rotation epoch
    // was the stored generation plus one when they read it; a concurrent registration or issue committed
    // in between and took that generation. A sentence implying a malformed request would send somebody
    // whose arithmetic was right off to correct it — which is the 400 FactorManifest.Promote raises for
    // the caller whose arithmetic was wrong, and the two are deliberately different answers.
    //
    // It says what to do next, because a 409 with no detail leaves a client with no idea whether to
    // retry. Re-reading is not optional advice: the manifest is sealed with its epoch as associated
    // data, so it has to be resealed over the new generation rather than re-sent. And the ceremony has
    // to run again either way, because this attempt's nonce is already spent.
    private const string FactorSetMovedMessage =
        "Another change to this account's recovery factors landed first, so its manifest is now at a "
        + "later generation and nothing here was written. Read the account's keys back, seal a manifest "
        + "over the generation it reports, and run the ceremony again.";

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
    public Task<FactorManifest?> FindFactorManifestAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        // TRACKED, AND NO AsNoTracking MAY BE ADDED HERE. FactorManifest.Promote checks the step
        // against the stored generation and EF builds WHERE rotation_epoch = @original from the value
        // snapshotted at load, so a no-tracking instance would satisfy the C# guard against a number it
        // read a moment ago and emit an UPDATE guarded by nothing. AccountKeyReadService reads the same
        // row with AsNoTracking because it only shows it; this one is read to be promoted.
        //
        // SingleOrDefault because user_id is the primary key: an account has one manifest row or none,
        // and a second would mean that key has been lost. The predicate names the row rather than
        // scoping the statement — factor_manifests carries the user_isolation policy, so another
        // account's manifest is not reachable from this connection at all.
        dbContext.FactorManifests
            .SingleOrDefaultAsync(manifest => manifest.UserId == userId, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(
        Credential credential,
        PasskeyPublicKey publicKey,
        PasskeySignatureCounter counter,
        WrappedAccountKeys wrappedAccountKeys,
        FactorManifest factorManifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factorManifest);

        dbContext.Credentials.Add(credential);
        dbContext.PasskeyPublicKeys.Add(publicKey);
        dbContext.PasskeySignatureCounters.Add(counter);
        dbContext.WrappedAccountKeys.Add(wrappedAccountKeys);

        // NOT Add AND NOT Update. The manifest arrived from FindFactorManifestAsync, so it is already
        // tracked as Unchanged-then-Modified and its original values are the ones the row held when it
        // was read — which is the whole of what makes the concurrency token on rotation_epoch mean
        // anything. Update on it would be harmless and Add would be an INSERT of a row that exists;
        // the promotion is picked up by the save below because the tracker already knows about it.

        try
        {
            // One save, so the five rows land together or not at all. A credential without its public
            // key would be a passkey nothing can verify a signature against, a key without its counter
            // would be a passkey whose clone detection silently never runs, either of them without
            // the wrapped keys would be a factor that looks registered to every screen in the product
            // and opens nothing — discovered on the day somebody needs it — and all four without the
            // promoted manifest would be a factor no client can learn exists, since the manifest is the
            // sole carrier of every factor's public key and a rotation encapsulates to what it names.
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Filtered on the constraint name, never on the SQLSTATE alone: four rows are written here and
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
            // context, which would report the refusal a second time from somewhere unrelated. All four,
            // because all four were queued by this call.
            dbContext.Entry(credential).State = EntityState.Detached;
            dbContext.Entry(publicKey).State = EntityState.Detached;
            dbContext.Entry(counter).State = EntityState.Detached;
            dbContext.Entry(wrappedAccountKeys).State = EntityState.Detached;

            // AND THE MANIFEST WITH THEM, THOUGH THIS CALL DID NOT QUEUE IT. It is the one entity here
            // the tracker already held, and the promotion mutated it in place: left Modified, it is a
            // pending UPDATE moving the account's generation for a registration that wrote nothing, and
            // any later save through this scoped context would commit it. Detached rather than reloaded
            // because the caller is about to raise a conflict and nothing downstream reads it again.
            dbContext.Entry(factorManifest).State = EntityState.Detached;

            return false;
        }
        // THE OTHER RACE ON THE SAME SAVE, AND IT THROWS WHERE THE ONE ABOVE ANSWERS false. That
        // asymmetry is deliberate: a WebAuthn handle collision is a fact about the AUTHENTICATOR the
        // caller's own device produced — their device has enrolled here before, and "this authenticator
        // is already registered" is something they can act on. A factor identifier is a value the CLIENT
        // chose, so a collision on it says nothing about any device, and reporting it as an
        // already-registered authenticator would be a confident, specific, false sentence about hardware
        // that has never been seen here. Two facts, two answers; they must not be collapsed into one.
        //
        // The message is RecoveryCodeRepository.AddSetAsync's, verbatim — the same table reached from
        // the other route; see the constant above.
        //
        // NARROWED ON THE CONSTRAINT NAME for the reason the catch above is, and the need is greater
        // rather than equal: this save now writes rows carrying several unique rules apiece, so a bare
        // SQLSTATE catch would report a collision on any of them — the WebAuthn handle included — as a
        // factor-id conflict. RepositoryConstraintAttributionTests pins that every translated exception
        // in this folder names its constraint.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: WrappedAccountKeysConfiguration.PrimaryKeyName,
        })
        {
            // The kind is shared with the other two routes that write this table, for the reason the
            // sentence is: one fact about one table, and one thing the caller does about it.
            throw new ConflictException(FactorAlreadyRegisteredMessage, ConflictKind.FactorAlreadyRegistered);
        }
        // THE THIRD RACE ON THIS SAVE, AND THE ONLY ONE THAT IS NOT A UNIQUE VIOLATION. Two requests
        // adding a passkey to one account both read the manifest at generation N and both compute N + 1;
        // the loser's UPDATE carries WHERE rotation_epoch = N, matches nothing, and EF raises. Left
        // alone it is a 500 telling a caller who did everything right that the server broke, on a
        // request their retry could have completed.
        //
        // NARROWED BY THE ENTRIES, the shape DeletePasskeyAsync and UserRepository.DeleteAsync use for
        // their own: a concurrency conflict carries no SQLSTATE and no constraint name, so "every
        // conflicting row is the manifest this call promoted" is this catch's equivalent of the
        // constraint-name filters above. SaveChangesAsync flushes everything the scoped context is
        // tracking, so a conflict over some other entity riding along must propagate — a 500 naming the
        // real failure beats a confident, specific, false "the factor set moved".
        //
        // No detach on the way out, unlike the handle race: that one swallows and returns false, while
        // this throws and the unit of work unwinds. Nothing replays a ConflictException.
        catch (DbUpdateConcurrencyException exception) when (IsManifestPromotionLost(exception))
        {
            // Deliberately not FactorAlreadyRegistered, though the same save can raise both: that one is
            // a value the client chose and has to mint again, this one is a generation the account moved
            // to without them. And deliberately not the 400 FactorManifest.Promote raises over the same
            // rule — that caller's epoch was never one greater than stored, this caller's was.
            throw new ConflictException(FactorSetMovedMessage, ConflictKind.FactorSetMoved);
        }
    }

    /// <summary>
    /// True when the conflict is only about the <see cref="FactorManifest"/> this call promoted. The
    /// count test is not redundant: an exception EF could not attribute to any entry would otherwise
    /// satisfy the predicate vacuously.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The state is half of the filter.</b> The manifest is the one entity in either save that is
    /// <see cref="EntityState.Modified"/> — <see cref="TryAddAsync"/>'s other four rows are inserts and
    /// <see cref="DeletePasskeyAsync"/>'s other row is a delete — so a conflict attributed to a row in
    /// any other state is not the lost promotion this models, whatever its type.
    /// </para>
    /// <para>
    /// <b>Shared by the two saves in this class rather than written twice</b>, because it is one fact
    /// about one row read the same way on both; <c>RecoveryCodeRepository</c> spells its own out for the
    /// reason that file gives — each repository owns the predicates its own catches read. On the delete
    /// it is the narrower half of a pair: <see cref="IsAlreadyDeleted"/> claims every conflict naming a
    /// removed credential, manifest or no manifest, and this one claims the rest.
    /// </para>
    /// </remarks>
    private static bool IsManifestPromotionLost(DbUpdateConcurrencyException exception) =>
        exception.Entries.Count > 0
        && exception.Entries.All(entry =>
            entry.Entity is FactorManifest && entry.State == EntityState.Modified);

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
        FactorManifest factorManifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factorManifest);

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
        // passkey_signature_counters, wrapped_account_keys and sessions leave by the database's own
        // cascade from this row, and the role holds no DELETE on any of them.
        //
        // wrapped_account_keys is the member of that list this method now owes a manifest for — the
        // factor's share of the account keys goes with it, which is a change to the account's set of
        // recovery factors. See IPasskeyRepository.DeletePasskeyAsync.
        dbContext.Credentials.Remove(credential);

        // NOT Add AND NOT Update, the same as TryAddAsync above: the manifest arrived from
        // FindFactorManifestAsync, so it is tracked as Unchanged-then-Modified and its original values
        // are the ones the row held when it was read — which is the whole of what makes the concurrency
        // token on rotation_epoch mean anything. The promotion is picked up by the save below because
        // the tracker already knows about it.

        try
        {
            // ONE SAVE, so the credential's row and the promoted manifest leave and move together — two
            // statements in one batch rather than two saves whose atomicity would be a fact about the
            // enclosing transaction instead of about this method. A revocation committed without the
            // promotion would leave the account's only statement of its factor set naming the passkey
            // that just went, and the next rotation would encapsulate the account's keys to it.
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
        // carries no SQLSTATE, so "every conflicting row is one this call itself queued" is this
        // method's equivalent of the constraint-name filter RepositoryConstraintAttributionTests
        // requires elsewhere in this folder. A conflict over some other entity riding along on the same
        // SaveChanges is a failure this method does not model, and reporting it as a missing passkey
        // would be the same lie in the other direction.
        //
        // This call queues TWO rows, so the filter names two: the credentials row it marked Deleted,
        // which has to be among them, and the manifest it promoted, which is tolerated beside it. The
        // second arm is unreachable on this provider — a lost double tap loses both statements, and EF
        // Core 10 over Npgsql reports only the FIRST failing command in the batch, which is the DELETE.
        // IsAlreadyDeleted carries the measurement, why the clause is kept regardless, and the accepted
        // gap that follows from reporting one statement: the batch's ordering is what decides whether
        // this 404 or the 409 below is what a double tap receives.
        //
        // No detach on the way out, unlike those two: they swallow and let the request carry on with a
        // context that still holds Deleted entries, while this one throws. The unit of work unwinds,
        // and the enclosing ITransactionalExecutor does not replay a NotFoundException.
        catch (DbUpdateConcurrencyException exception) when (IsAlreadyDeleted(exception))
        {
            throw new NotFoundException("Passkey was not found.");
        }
        // THE OTHER RACE ON THIS SAVE, and the two catches cannot swallow each other: the predicate
        // above requires at least one conflicting Credential and this one requires every conflicting
        // entry to be a FactorManifest, so no exception satisfies both and their order is a reading
        // choice rather than a behaviour. The delete's own answer is written first because it is the
        // thing this method is named for.
        //
        // A concurrent change to this account's factors — a passkey registration, an issue of recovery
        // codes, or another revocation of a DIFFERENT passkey — moved the generation between the
        // caller's read and this save, so the promotion's UPDATE carries WHERE rotation_epoch = N,
        // matches nothing, and EF raises. Left alone it is a 500 telling a caller who did everything
        // right that the server broke, on a request their retry could complete.
        //
        // "A different passkey" is not a stray qualifier. This catch is reached when the UPDATE is the
        // first failing command in the batch, which means the DELETE above it matched its row — so the
        // revocation itself was sound and only the generation moved. A winner that took THIS credential
        // fails the DELETE first and is answered by the catch above; see IsAlreadyDeleted.
        //
        // NARROWED BY THE ENTRIES, the shape the delete's own catch and TryAddAsync's use: a concurrency
        // conflict carries no SQLSTATE and no constraint name, so "every conflicting row is the manifest
        // this call promoted" is this catch's equivalent of a constraint-name filter. SaveChangesAsync
        // flushes everything the scoped context is tracking, so a conflict over some other entity riding
        // along must propagate — a 500 naming the real failure beats a confident, specific, false "the
        // factor set moved".
        //
        // No detach on the way out, for the reason the catch above gives: this throws, the unit of work
        // unwinds, and ITransactionalExecutor does not replay a ConflictException.
        catch (DbUpdateConcurrencyException exception) when (IsManifestPromotionLost(exception))
        {
            // The kind and the sentence are shared with the two paths that ADD a factor, because a
            // caller whose generation moved under them does the same thing about it whichever route
            // they were on: read the account's keys back, seal a manifest over the generation it
            // reports, and prove presence again. Deliberately not the 400 FactorManifest.Promote raises
            // over the same rule — that caller's epoch was never one greater than stored, this caller's
            // was.
            throw new ConflictException(FactorSetMovedMessage, ConflictKind.FactorSetMoved);
        }
    }

    /// <summary>
    /// True when the conflict is about <see cref="Credential"/> rows this call removed, and about
    /// nothing else the call itself queued. The count test is not redundant: an exception EF could not
    /// attribute to any entry would otherwise satisfy the predicate vacuously.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A promoted <see cref="FactorManifest"/> is tolerated beside the credential, and on this
    /// provider that arm is unreachable today.</b> A revocation that wins the race deletes the
    /// credential <em>and</em> promotes the generation in one save, so both of the loser's statements
    /// match no row — but EF Core 10 over Npgsql reports only the <em>first failing command in the
    /// batch</em> and nothing after it, so the exception carries one entry,
    /// <c>Credential/Deleted</c>. Measured, not reasoned:
    /// <c>DeletePasskeyAsync_WhenBothStatementsMatchNothing_ReportsOnlyTheFirstFailingStatement</c> is
    /// the test that reads it off a real database. <c>ThrowAggregateUpdateConcurrencyExceptionAsync</c>
    /// is exported by the relational assembly, and an exported symbol is not proof of the path taken.
    /// </para>
    /// <para>
    /// <b>It is kept anyway, and the reason is the cost either way rather than a race it prevents.</b>
    /// The clause costs one line and changes no answer today; without it, a provider that ever did
    /// aggregate would turn the commonest race this method has — a person double-tapping the button —
    /// into a 500, silently, because every test of that race arranges the loss out of band and sees one
    /// entry. The <c>Count &gt; 0</c> guard above it is kept on the same footing: a defence against a
    /// shape nobody has produced, written down rather than trusted to stay impossible.
    /// </para>
    /// <para>
    /// The <c>Any</c> is what keeps it disjoint from <see cref="IsManifestPromotionLost"/>: a conflict
    /// naming only the manifest is that one's, and a conflict naming any <em>third</em> entity is
    /// neither's and propagates, which is what
    /// <c>DeletePasskeyAsync_WhenAnUnrelatedEntityConflicts_LetsTheConflictEscape</c> holds. That
    /// disjointness is a property of the two predicates and holds however many entries a provider
    /// reports.
    /// </para>
    /// <para>
    /// 404 is what the double tap answers, under this filter and under a strict
    /// <c>All(entry is Credential)</c> alike, because the <c>DELETE</c> is the first failing command —
    /// and it is the right answer: the passkey the caller asked to have removed is gone, and inviting
    /// them to reseal a manifest and retry sends them to a request that can only answer 404.
    /// </para>
    /// <para>
    /// <b>ACCEPTED GAP, and it follows from the measurement rather than from this predicate.</b> Since
    /// only the first failing command is reported, <em>which</em> answer a double tap receives is
    /// decided by the statement order EF chooses inside the batch. Today the <c>DELETE</c> on
    /// <c>credentials</c> is ordered before the <c>UPDATE</c> on <c>factor_manifests</c> — the two
    /// tables have no foreign key between them, so nothing in this repository, in the model or in any
    /// test pins that order — and were it ever reversed, the identical request would answer 409
    /// <see cref="ConflictKind.FactorSetMoved"/>: telling somebody to reseal a manifest and retry a
    /// revocation that can only ever 404. A dependency's ordering choice reaching a user-visible
    /// response is the gap; it is recorded rather than closed, because pinning the order or splitting
    /// the save is a larger decision than the one this method was changed for, and the test named above
    /// is what would catch the day it moves.
    /// </para>
    /// </remarks>
    private static bool IsAlreadyDeleted(DbUpdateConcurrencyException exception) =>
        exception.Entries.Count > 0
        && exception.Entries.Any(entry =>
            entry.Entity is Credential && entry.State == EntityState.Deleted)
        && exception.Entries.All(entry =>
            (entry.Entity is Credential && entry.State == EntityState.Deleted)
            || (entry.Entity is FactorManifest && entry.State == EntityState.Modified));

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
