using Domain.Common;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Who <see cref="KeyRotationRepository" /> is allowed to speak for when PostgreSQL refuses one of the
/// two saves it makes: a <c>StageAsync</c> violation this method does not model escapes instead of being
/// detached and replayed as a lost race between two begins, and a <c>PromoteAsync</c> failure escapes
/// unless it is the account's own manifest losing its concurrency token.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two differently shaped catches, and only one of them is narrowed on anything PostgreSQL says.</b>
/// <c>StageAsync</c> reads a SQLSTATE and a constraint name off a <c>DbUpdateException</c>;
/// <c>PromoteAsync</c> catches a <c>DbUpdateConcurrencyException</c>, which carries neither, and narrows
/// on the <em>entries</em> EF could not account for instead. So the two halves of this file are not
/// copies: the first stages a rule from another table and asserts it is not converged away, and the
/// second stages a row of another <em>kind</em> and asserts it is not answered as a conflict about a
/// factor set nobody moved.
/// </para>
/// <para>
/// <b>The promotion half runs both ways round, and that is what keeps it from being a decoration.</b>
/// <c>PromoteAsync_WhenTheStoredGenerationMovedFirst_…</c> makes the filter <em>fire</em> — without it
/// the <c>when</c> clause is dead text, which is the state <c>StageAsync</c>'s filter was in before it
/// had a control — and <c>PromoteAsync_WhenAStrangersRowLosesItsOwnRowCount_…</c> makes it <em>not</em>
/// fire. Neither needs a race: a promotion is optimistic concurrency over one stored integer, so the
/// competing write is committed on a context of its own, in order, between this request's read and its
/// write. That is the difference from the begin path, whose retry nothing in the suite runs
/// deterministically.
/// </para>
/// <para>
/// <b>This file holds one half of the begin narrowing, and the other half is not here.</b> The
/// translation —
/// that a <c>23505</c> under <c>PK_key_rotations</c> or <c>PK_key_rotation_seals</c> converges on one
/// staged generation rather than answering a conflict — is pinned over HTTP by
/// <c>KeyRotationBeginEndpointTests</c>, which is where a begin has a caller. What is written here is
/// the control that keeps that convergence from being said about somebody else's rule.
/// </para>
/// <para>
/// <b>The violation has to be staged and cannot be provoked from the outside</b>, which is why this
/// control lives at the repository layer exactly as <c>PasskeyRepositoryTests</c>',
/// <c>RecoveryCodeRepositoryTests</c>' and <c>RegistrationRepositoryTests</c>' equivalents do.
/// <c>SaveChangesAsync</c> flushes the whole change tracker rather than the two tables this method
/// writes, but the begin path as it stands leaves nothing else pending in it: the re-authentication gate
/// in front of the route flushes the signature counter through <c>PasskeyRepository.SaveCounterAsync</c>,
/// which saves on its own, so that row is <c>Unchanged</c> by the time <c>StageAsync</c> is entered.
/// There is no request that reaches this <c>catch</c> carrying a stranger's row, and that is a fact
/// about today's caller rather than about the rule — the context is request-scoped, and the first thing
/// added to the handler below the staging write makes the whole of it reachable.
/// </para>
/// <para>
/// <b>The rule broken is on another table, and it is not a preference: neither table this method writes
/// has a second unique rule to break.</b> <see cref="KeyRotationConfiguration" /> keys
/// <c>key_rotations</c> on <c>user_id</c> and declares no index at all — it says in as many words that
/// <c>rotation_id</c> is deliberately <em>not</em> unique — and
/// <see cref="KeyRotationSealConfiguration" /> keys <c>key_rotation_seals</c> on
/// <c>(user_id, factor_id)</c> and adds two check constraints and two foreign keys, no unique index
/// among them. So the two names the <c>when</c> clause carries are the <em>whole</em> of the unique
/// rules those two tables hold, and a control staged on a neighbouring rule of the same table — the
/// shape the deleted <c>UserRepository</c> control had — is not merely weaker here, it is unreachable.
/// </para>
/// <para>
/// Driven on <see cref="RepositoryTestHost.ConnectionString" />, the container superuser, which both
/// isolation policies are <c>FOR ALL</c> against: half of what is asserted here is that rows are
/// <b>absent</b>, and a policed connection reports a row that is there exactly as it reports one that is
/// not.
/// </para>
/// </remarks>
public sealed class KeyRotationRepositoryTests
{
    /// <summary>
    /// A unique violation this repository does not model escapes, instead of being converged away by the
    /// retry that answers a second begin of one account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the <c>ConstraintName</c> half of <c>StageAsync</c>'s <c>when</c> clause, which
    /// is the whole reason that clause names two primary keys rather than matching the SQLSTATE.</b>
    /// Widen it to a bare <c>23505</c> and this method starts detaching a caller's <c>Added</c> rows and
    /// replaying its save against a state that really is wrong — for a rule it has never heard of, on a
    /// table it does not write, keyed on a value a stranger chose.
    /// </para>
    /// <para>
    /// <b>The intruder is a second <c>users</c> row reusing the seeded account's address</b>, breaking
    /// <c>IX_users_email</c> with the same <c>23505</c> the two primary keys would raise. That index is
    /// unique <em>table-wide regardless of owner</em>, so one account's address really can refuse another
    /// account's write — which is the property that makes this control stand in for a stranger's row, and
    /// the same property <c>RegistrationRepositoryTests</c> reaches for in <c>PK_recovery_code_hashes</c>.
    /// It is also the identical intruder <c>PasskeyRepositoryTests</c> stages, deliberately: a
    /// <c>users</c> row with no credential is legal at the schema level, so exactly one rule is broken and
    /// this test cannot pass or fail on which of two violations PostgreSQL reported first. Everything the
    /// begin itself carries is beyond reproach — no rotation is staged for the account, so
    /// <c>PK_key_rotations</c> is free, and the account holds one factor with no seal against it, so
    /// <c>PK_key_rotation_seals</c> is free too.
    /// </para>
    /// <para>
    /// <b>The save count is asserted beside the exception, and without it this test is a decoration.</b>
    /// The three sibling controls can stop at "something escaped" because their repositories answer a
    /// swallowed violation with a value — <see langword="false" />, or one of four outcomes — so widening
    /// their filters turns a throw into a return. This one converges instead: widened, the <c>catch</c>
    /// detaches the rolled-back attempt's <c>Added</c> rotation and seals, re-reads, re-adds them and
    /// saves again — and the intruder is still <c>Added</c> through all of it, because the detach loop is
    /// typed to this method's own two entities. The second save therefore raises the <em>same</em>
    /// violation under the <em>same</em> constraint name, and every assertion about the escaping
    /// exception stays green. That was measured rather than reasoned: under the widening this test fails
    /// on the attempt count alone, at two. So the count is what says the violation escaped
    /// <em>unconverged</em>, which is the claim; <c>SavingChanges</c> is read rather than the change
    /// tracker, because what is being counted is how many times the method asked the database, not what
    /// EF was left holding.
    /// </para>
    /// <para>
    /// <b>What this does not pin, said here rather than left to be assumed: the retry.</b> Nothing in the
    /// suite deterministically exercises it — the concurrency test over in
    /// <c>KeyRotationBeginEndpointTests</c> cannot force two <c>Task.WhenAll</c> requests to interleave,
    /// and can only lose toward a false pass. This case pins that the filter is <em>narrow</em>, never
    /// that the convergence behind it <em>runs</em>.
    /// </para>
    /// <para>
    /// <b>What it costs when it fires: a begin whose save was refused by a rule nobody on this path
    /// broke is re-issued rather than reported.</b> The replay writes the same statements against the
    /// same wrong state and fails the same way, so the person gets the 500 either way — but the log now
    /// names a violation raised on a second attempt the method had no reason to make, and the account is
    /// one caller's addition away from a <c>23505</c> the retry could swallow outright. The escape is the
    /// honest answer: a 500 naming the real constraint beats a staged generation nobody asked for.
    /// </para>
    /// <para>
    /// The SQLSTATE is asserted beside the constraint name, which is what keeps this a narrowing test
    /// rather than a test that any failure escapes: a violation of some entirely different kind — a
    /// <c>23503</c> from either of the seal's two foreign keys, say — would satisfy "neither primary key"
    /// without ever exercising the filter.
    /// </para>
    /// </remarks>
    [Test]
    public async Task StageAsync_WhenATrackedRowBreaksAnotherUniqueRule_LetsTheViolationEscape()
    {
        // Arrange — an account holding one passkey and that passkey's factor, which is the smallest
        // account a begin can be made for: the handler stages one seal per factor the account holds, and
        // a factor is what a seal names.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync(GoogleSubject, TakenEmail);
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);
        Guid factorId = await host.SeedWrappedAccountKeysAsync(credentialId, Guid.CreateVersion7());

        await using BudgetoidDbContext db = CreateDb(host);

        // The stranger's row, arriving with the seeded account's address. Tracked and never saved by this
        // test: what puts it on the wire is the repository's own SaveChangesAsync, which is the whole
        // mechanism this control is about.
        Guid intruderId = Guid.CreateVersion7();
        db.Users.Add(User.CreateWithId(intruderId, TakenEmail, SeedInstant));

        KeyRotationRepository repository = new(db);

        // Built the way BeginKeyRotationHandler builds them — the loaded passkey, and the factor read
        // back through this repository's own listing — so the rows handed to the act are rows the
        // application could really have produced. Both factories refuse a great deal, and a begin
        // assembled some other way could be refused before it reached a save at all.
        Credential passkey = await db.Credentials
            .SingleAsync(credential => credential.Id == credentialId);
        IReadOnlyDictionary<Guid, WrappedAccountKeys> factors = await repository.ListFactorsAsync(userId);
        KeyRotation rotation = KeyRotation.Begin(
            passkey,
            Guid.CreateVersion7(),
            ManifestFixture.Mint().Manifest,
            StagedRotationEpoch,
            SeedInstant);
        KeyRotationSeal seal = KeyRotationSeal.For(
            rotation,
            factors[factorId],
            RepositoryTestHost.EncapsulatedAccountKeysPayload(StagedAccountKeysFiller));

        // Attached after the arrangement so nothing but the act is counted. The seeding above runs on
        // contexts of its own, and neither query below it saves anything.
        int saveAttempts = 0;
        db.SavingChanges += (_, _) => saveAttempts++;

        // Act
        Exception? escaped = await CaptureAsync(() => repository.StageAsync(rotation, [seal]));

        // Assert — something escaped, which is the first half of the claim.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();

        // And it really was a unique violation, on the rule the arrangement staged rather than on some
        // unrelated failure that would satisfy "neither primary key" for free.
        await Assert.That(SqlStateOf(escaped)).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(UserEmailIndex);

        // Both names, because the when clause carries both and widening either one swallows this
        // violation. Read off the configurations the filter itself reads, so a rename moves the filter
        // and this assertion together — the constraint name asserted above is the literal, for the
        // opposite and correct reason: it is this file's independent statement of what PostgreSQL says.
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(KeyRotationConfiguration.PrimaryKeyName);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(KeyRotationSealConfiguration.PrimaryKeyName);

        // THE HALF THAT NOTICES THE WIDENING. One attempt: the violation reached the caller instead of
        // being detached and replayed. Two is the converging path running for a rule it does not model,
        // and every assertion above stays green through it.
        await Assert.That(saveAttempts).IsEqualTo(1);

        // Nothing of the begin landed. Through a fresh context, so this cannot pass by reading rows back
        // out of the tracker that queued them, and by the account's own id rather than by a count — the
        // seeded rows are still there, so a count would be answering about those.
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.KeyRotations.AnyAsync(staged => staged.UserId == userId)).IsFalse();
        await Assert.That(await verify.KeyRotationSeals.AnyAsync(staged => staged.UserId == userId))
            .IsFalse();
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == intruderId)).IsFalse();

        // And the seeded account is untouched, factor and all. Without this, a save that rolled the whole
        // database back would satisfy every absence assertion above.
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == userId)).IsTrue();
        await Assert.That(await verify.WrappedAccountKeys.AnyAsync(keys => keys.FactorId == factorId))
            .IsTrue();
    }

    /// <summary>
    /// A promotion whose stored generation moved between the read and the write is answered as a
    /// conflict about the factor set, and nothing of the completion lands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: that <c>PromoteAsync</c>'s <c>when</c> clause is reachable at all.</b> The
    /// filter was written and nothing drove it, which is the state <c>StageAsync</c>'s was in before the
    /// case above existed — a <c>when</c> clause nothing enters is a clause whose predicate can be wrong
    /// in either direction without a single line going red, and the translation it guards is the
    /// difference between a 409 telling somebody to reseal a manifest and a 500 telling them the server
    /// broke.
    /// </para>
    /// <para>
    /// <b>No race, and none is needed — which is the whole difference from the begin path above.</b>
    /// The rule is EF optimistic concurrency over <c>factor_manifests.rotation_epoch</c>: the promoted
    /// instance carries the generation EF snapshotted when this repository loaded it, and the UPDATE it
    /// emits carries <c>WHERE rotation_epoch = @original</c>. So "another promotion landed first" is
    /// staged by committing one, in order, on a context of its own — the racing registration or
    /// recovery-code issue this catch exists for, reduced to the one statement it makes. Two
    /// <c>Task.WhenAll</c> requests would be the same claim with a coin flip in it.
    /// </para>
    /// <para>
    /// <b>The racer promotes to the same epoch this request computed, and the manifests are compared by
    /// their bytes rather than by that number.</b> Both callers read generation one and both wrote two,
    /// because that is what the arithmetic <c>FactorManifest.Promote</c> enforces leaves them: an
    /// assertion on the epoch alone would be satisfied by <em>either</em> promotion having landed, which
    /// is exactly the outcome being told apart. <see cref="ManifestFixture" /> mints random bytes per
    /// call, so the two blobs cannot collide.
    /// </para>
    /// <para>
    /// <b>The factor row is read back beside the manifest, and that is the half the message claims.</b>
    /// The sentence this conflict travels with says <em>nothing here was written</em>. The manifest and
    /// every factor move in one save, so a promotion refused after the factors had adopted their seals
    /// would leave rows rewritten under a generation the account does not hold — and the only thing
    /// standing between that and the database is the single <c>SaveChangesAsync</c>. Asserting the
    /// factor still carries the filler <see cref="RepositoryTestHost" /> seeded it with is what says the
    /// rollback covered the whole of it rather than only the row that raised.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PromoteAsync_WhenTheStoredGenerationMovedFirst_RaisesFactorSetMoved()
    {
        // Arrange — the smallest account a completion can be made for: one passkey, one factor, and the
        // manifest registration files at the floor. The seeded generation is what both promotions below
        // are computed from.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync(GoogleSubject, TakenEmail);
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);
        Guid factorId = await host.SeedWrappedAccountKeysAsync(credentialId, Guid.CreateVersion7());

        await using BudgetoidDbContext db = CreateDb(host);
        KeyRotationRepository repository = new(db);

        // Through the repository's own members, because tracking is the whole mechanism: the manifest
        // read is deliberately not AsNoTracking, and TrackFactorsAsync exists so a mutated factor is an
        // entity some save will look at. A test that loaded either of these its own way would be
        // measuring its own arrangement.
        FactorManifest manifest = await repository.FindFactorManifestAsync(userId)
            ?? throw new InvalidOperationException("The seeded account holds no factor manifest.");
        IReadOnlyDictionary<Guid, WrappedAccountKeys> factors = await repository.TrackFactorsAsync(userId);

        // Built the way CompleteKeyRotationHandler builds them, so the rows handed to the act are rows
        // the application could really have produced.
        Credential passkey = await db.Credentials
            .SingleAsync(credential => credential.Id == credentialId);
        ManifestFixture ours = ManifestFixture.Mint();
        KeyRotation rotation = KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), ours.Manifest, StagedRotationEpoch, SeedInstant);
        KeyRotationSeal seal = KeyRotationSeal.For(
            rotation,
            factors[factorId],
            RepositoryTestHost.EncapsulatedAccountKeysPayload(StagedAccountKeysFiller));

        // This request's promotion, computed against the generation it read and therefore correct
        // arithmetic — which is what separates the 409 below from the 400 Promote raises for a caller
        // whose epoch was never the stored one plus one.
        manifest.Promote(ours.Manifest, StagedRotationEpoch);
        factors[factorId].Promote(seal);

        // The change that lands first. A registration or a recovery-code issue in the product; here the
        // one statement either of them makes about this row, on a context of its own so nothing about
        // the act's tracker is disturbed.
        ManifestFixture theirs = ManifestFixture.Mint();
        await using (BudgetoidDbContext racer = CreateDb(host))
        {
            FactorManifest overtaking = await racer.FactorManifests
                .SingleAsync(stored => stored.UserId == userId);
            overtaking.Promote(theirs.Manifest, StagedRotationEpoch);
            await racer.SaveChangesAsync();
        }

        // Act
        Exception? refusal = await CaptureAsync(
            () => repository.PromoteAsync(manifest, [factors[factorId]]));

        // Assert — translated rather than propagated, which is the first half of the claim.
        await Assert.That(refusal).IsNotNull();
        await Assert.That(refusal).IsTypeOf<ConflictException>();

        // And it is the member a client branches on, not merely a 409. The token itself is transcribed
        // in ConflictKindSpellingTests and deliberately not restated here.
        ConflictException conflict = (ConflictException)refusal!;
        await Assert.That(conflict.Kind).IsEqualTo(ConflictKind.FactorSetMoved);

        // Nothing of the completion landed. Through a fresh context, so this cannot pass by reading rows
        // back out of the tracker that queued them.
        await using BudgetoidDbContext verify = CreateDb(host);
        FactorManifest survivor = await verify.FactorManifests
            .SingleAsync(stored => stored.UserId == userId);

        // The racer's bytes, not this request's — asserted on the blob because both promotions carry the
        // same epoch and the number tells the two apart not at all.
        await Assert.That(survivor.Manifest.ToArray().SequenceEqual(theirs.Manifest)).IsTrue();
        await Assert.That(survivor.Manifest.ToArray().SequenceEqual(ours.Manifest)).IsFalse();

        // And the factor never adopted its seal, which is what "nothing here was written" means to
        // somebody whose account keys these are.
        WrappedAccountKeys storedFactor = await verify.WrappedAccountKeys
            .SingleAsync(keys => keys.FactorId == factorId);

        await Assert.That(storedFactor.EncapsulatedAccountKeys.ToArray().SequenceEqual(
                RepositoryTestHost.EncapsulatedAccountKeysPayload(
                    RepositoryTestHost.SeededAccountKeysFiller)))
            .IsTrue();
    }

    /// <summary>
    /// A concurrency failure over somebody else's row escapes, instead of being dressed up as this
    /// account's factor set having moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it holds: the <c>Entries</c> half of <c>PromoteAsync</c>'s <c>when</c> clause, which is
    /// this catch's only available narrowing.</b> A <c>DbUpdateConcurrencyException</c> carries no
    /// SQLSTATE and no constraint name — there is no violation, only a statement that matched fewer rows
    /// than EF expected — so "every row EF could not account for is the manifest this call promoted" is
    /// the whole of the filter. Widen it to a bare <c>catch (DbUpdateConcurrencyException)</c> and this
    /// method answers 409 <c>factor_set_moved</c> for a row it has never heard of, telling somebody to
    /// reseal a manifest that was never the problem and swallowing the real failure on the way.
    /// </para>
    /// <para>
    /// <b>The intruder is a bare <c>users</c> row the act's context is holding as <c>Modified</c>, taken
    /// away underneath it.</b> A row with no credential is legal at the schema level — the same property
    /// the case above reaches for — so it has no dependents, and deleting it is one statement rather
    /// than a cascade whose reach a reader would have to verify. When the save runs, the UPDATE against
    /// it matches nothing, EF attributes the shortfall to that entry, and the entry is neither a
    /// <see cref="FactorManifest" /> nor in this method's save for any reason. Everything the completion
    /// itself carries is beyond reproach: the account's own promotion is correct arithmetic against a
    /// generation nobody moved, so it would land on its own.
    /// </para>
    /// <para>
    /// <b>The entries are read off the escaping exception, and without that line this is half a
    /// test.</b> "Something other than a <c>ConflictException</c> escaped" is satisfied by a
    /// <c>PromoteAsync</c> whose <c>catch</c> was deleted outright. Naming what EF attributed the
    /// failure to is what says the filter <em>looked</em> and declined — and it is the assertion that
    /// would fail if somebody relaxed the predicate to <c>Entries.Any(…)</c>, which reads as a
    /// tightening and is the exact opposite: one manifest among a stranger's rows would satisfy it.
    /// </para>
    /// <para>
    /// <b>What it does not claim.</b> The state half of the predicate — that the conflicting manifest is
    /// <c>Modified</c> — is not staged here, and cannot be from anything this path can produce: the only
    /// other state a manifest could conflict in is <c>Deleted</c>, and no route, repository or grant in
    /// the product removes one. The count half is covered by construction, since an exception EF could
    /// attribute to no entry would satisfy the <c>All</c> vacuously and the predicate tests it first.
    /// </para>
    /// </remarks>
    [Test]
    public async Task PromoteAsync_WhenAStrangersRowLosesItsOwnRowCount_LetsTheFailureEscape()
    {
        // Arrange — the same smallest account, and a promotion that is correct in every respect.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync(GoogleSubject, TakenEmail);
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);
        Guid factorId = await host.SeedWrappedAccountKeysAsync(credentialId, Guid.CreateVersion7());

        // The stranger's row, written on a context of its own so the act's tracker holds it only as
        // something loaded — the state a request carrying an unrelated pending write would be in.
        Guid strangerId = Guid.CreateVersion7();
        await using (BudgetoidDbContext author = CreateDb(host))
        {
            author.Users.Add(User.CreateWithId(strangerId, StrangerEmail, SeedInstant));
            await author.SaveChangesAsync();
        }

        await using BudgetoidDbContext db = CreateDb(host);
        KeyRotationRepository repository = new(db);

        FactorManifest manifest = await repository.FindFactorManifestAsync(userId)
            ?? throw new InvalidOperationException("The seeded account holds no factor manifest.");
        IReadOnlyDictionary<Guid, WrappedAccountKeys> factors = await repository.TrackFactorsAsync(userId);

        Credential passkey = await db.Credentials
            .SingleAsync(credential => credential.Id == credentialId);
        ManifestFixture ours = ManifestFixture.Mint();
        KeyRotation rotation = KeyRotation.Begin(
            passkey, Guid.CreateVersion7(), ours.Manifest, StagedRotationEpoch, SeedInstant);
        KeyRotationSeal seal = KeyRotationSeal.For(
            rotation,
            factors[factorId],
            RepositoryTestHost.EncapsulatedAccountKeysPayload(StagedAccountKeysFiller));

        manifest.Promote(ours.Manifest, StagedRotationEpoch);
        factors[factorId].Promote(seal);

        // Marked rather than mutated, because User exposes no setter: forcing the state makes EF write
        // every mapped column, which is the statement that has to find no row.
        User stranger = await db.Users.SingleAsync(user => user.Id == strangerId);
        db.Entry(stranger).State = EntityState.Modified;

        // Taken away, on a context of its own. Nothing the act does causes this.
        await using (BudgetoidDbContext racer = CreateDb(host))
        {
            racer.Users.Remove(await racer.Users.SingleAsync(user => user.Id == strangerId));
            await racer.SaveChangesAsync();
        }

        // Act
        Exception? escaped = await CaptureAsync(
            () => repository.PromoteAsync(manifest, [factors[factorId]]));

        // Assert — something escaped, and it is EF's own exception rather than this repository's word
        // for a race that did not happen.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();
        await Assert.That(escaped).IsNotTypeOf<ConflictException>();

        // And the filter really was asked: what EF could not account for is the stranger's row and
        // nothing else, so the predicate declined on the entity rather than on there being no entries.
        DbUpdateConcurrencyException failure = (DbUpdateConcurrencyException)escaped!;
        await Assert.That(failure.Entries.Select(entry => entry.Entity.GetType().Name))
            .IsEquivalentTo(new[] { nameof(User) });

        // The account's manifest is untouched, which says the save was rolled back whole rather than
        // committing the half that would have worked.
        await using BudgetoidDbContext verify = CreateDb(host);
        FactorManifest survivor = await verify.FactorManifests
            .SingleAsync(stored => stored.UserId == userId);

        await Assert.That(survivor.RotationEpoch).IsEqualTo(FactorManifest.MinimumRotationEpoch);
        await Assert.That(survivor.Manifest.ToArray().SequenceEqual(ours.Manifest)).IsFalse();

        // And the stranger's row stayed gone, which is what says the failure was the one this
        // arrangement staged rather than anything the act wrote.
        await Assert.That(await verify.Users.AnyAsync(user => user.Id == strangerId)).IsFalse();
    }

    /// <summary>The provider subject the seeded account's federated credential carries.</summary>
    private const string GoogleSubject = "google-key-rotation-owner";

    /// <summary>
    /// The address both the seeded account and the intruding row carry. A constant because the collision
    /// <b>is</b> the arrangement: two literals that happened to match would be a coincidence a reader has
    /// to verify.
    /// </summary>
    private const string TakenEmail = "rotation-owner@example.com";

    /// <summary>
    /// The address the bare <c>users</c> row in the promotion escape carries. Deliberately <b>not</b>
    /// <see cref="TakenEmail" />: that case is about a row losing its own row count, and a second row
    /// reusing the seeded address would break <c>IX_users_email</c> on the way in and never reach the
    /// act at all.
    /// </summary>
    private const string StrangerEmail = "rotation-stranger@example.com";

    /// <summary>
    /// The unique index <c>users.email</c> carries, spelled out rather than read off
    /// <c>UserConfiguration</c>. <see cref="PasskeyRepositoryTests" /> and
    /// <see cref="RegistrationRepositoryTests" /> keep the same habit for the name they <em>expect</em>: a
    /// test taking its expectation from the configuration the schema was rendered from would agree with a
    /// renamed constraint the moment it was renamed, and what this file is about is what PostgreSQL
    /// <em>reports</em>.
    /// </summary>
    private const string UserEmailIndex = "IX_users_email";

    /// <summary>
    /// The generation the begin stages. One above the floor, because <see cref="RepositoryTestHost" />
    /// seeds an account's manifest at the floor and the epoch a client may submit is the stored one plus
    /// one. Nothing in <see cref="KeyRotationRepository" /> reads it — the refusal that does lives on
    /// <c>FactorManifest.Promote</c> — so this is about the row being one the product could have written
    /// rather than about a rule under test here.
    /// </summary>
    private const int StagedRotationEpoch = FactorManifest.MinimumRotationEpoch + 1;

    /// <summary>
    /// The filler the staged seal's payload carries, different from both fillers
    /// <see cref="RepositoryTestHost" /> seeds with, so a value that had reached
    /// <c>wrapped_account_keys</c> would be visible by eye rather than indistinguishable from the row
    /// that was already there.
    /// </summary>
    private const byte StagedAccountKeysFiller = 0x77;

    /// <summary>
    /// Fixed UTC instant for the rows this file writes itself. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The WebAuthn handle the seeded passkey carries. Nothing here verifies a signature, so the bytes
    /// are arbitrary — but there are <see cref="PasskeyPublicKey.MinWebAuthnCredentialIdLength" /> of
    /// them, because a shorter handle is refused by the domain factory and the seed would fail before the
    /// act.
    /// </summary>
    private static readonly byte[] WebAuthnCredentialId =
    [
        .. Enumerable.Range(1, PasskeyPublicKey.MinWebAuthnCredentialIdLength).Select(value => (byte)value),
    ];

    /// <summary>
    /// Names the constraint PostgreSQL actually refused on, or <see langword="null" /> when the escaping
    /// exception never reached the database at all.
    /// </summary>
    /// <remarks>
    /// <see cref="PasskeyRepositoryTests" />' helper, spelled out here because each file in this folder
    /// owns the readers its own assertions need.
    /// </remarks>
    private static string? ConstraintNameOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.ConstraintName
            : null;

    /// <summary>
    /// The SQLSTATE PostgreSQL refused with, or <see langword="null" /> when nothing did. Read beside the
    /// constraint name so a narrowing test can say the violation it staged really is the kind the filter
    /// has to tell apart.
    /// </summary>
    private static string? SqlStateOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.SqlState
            : null;

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" /> when
    /// nothing did.
    /// </summary>
    /// <remarks>
    /// Deliberately untyped, as in <see cref="PasskeyRepositoryTests" />: the question is <i>which</i>
    /// exception surfaces, so catching a specific one in the helper would decide the answer before the
    /// assertion reads it — and one of the failures being watched for here is no exception at all.
    /// </remarks>
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// A context with no ambient budget, which is safe because nothing a begin writes carries a budget
    /// query filter — see <see cref="BudgetoidDbContext" />, which says so relation by relation. Every
    /// table on this path is policed on the user instead.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();

        return host;
    }
}
