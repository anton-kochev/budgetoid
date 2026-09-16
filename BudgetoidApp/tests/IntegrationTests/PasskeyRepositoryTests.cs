using Domain.Common;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Configurations;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;
using TUnit.Assertions.Enums;

namespace IntegrationTests;

/// <summary>
/// Who <see cref="PasskeyRepository" /> is allowed to speak for when PostgreSQL refuses one of its
/// writes: the delete's answer when the passkey it was handed is no longer in the table, and the
/// registration's answer when a handle is already spoken for — together with the controls that keep
/// each of those from being said about somebody else's rule.
/// </summary>
/// <remarks>
/// <para>
/// Turning a persistence failure into a domain answer is Infrastructure's job, and this is the lowest
/// layer that can be measured doing it. <c>UserRepository.DeleteAsync</c> and
/// <c>TransactionRepository.DeleteAllForAmbientBudgetAsync</c> already catch the same EF exception in
/// the same place, and <see cref="UserRepositoryTests" /> is where that catch is tested; this file is
/// the passkey equivalent, written to the same shape rather than to a second one.
/// </para>
/// <para>
/// <b>The two narrowings this class holds are narrowed on different things, and only one of them has a
/// name to narrow on.</b> <see cref="PasskeyRepository.TryAddAsync" /> filters a <c>23505</c> by
/// <see cref="PasskeyPublicKeyConfiguration.WebAuthnCredentialIdIndexName" />;
/// <see cref="PasskeyRepository.DeletePasskeyAsync" /> filters a concurrency conflict, which carries no
/// SQLSTATE and no constraint name, by the <i>entries</i>. Both are the same claim in the end — this
/// repository speaks only for the rule it models — and both are pinned here in both directions, so
/// neither can be satisfied by deleting the <c>catch</c> nor by widening it to the bare exception type.
/// </para>
/// <para>
/// <b>The mis-attribution mechanism is <c>RepositoryConstraintAttributionTests</c>', not a second
/// one.</b> <c>SaveChangesAsync</c> flushes everything the scoped context is tracking, not only the
/// entity the repository was handed, so
/// <see cref="TryAddAsync_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape" /> tracks one
/// unrelated row that breaks a <i>different</i> unique index carrying the <i>same</i> <c>23505</c>, and
/// then hands the repository a passkey that is beyond reproach. Read that file's remarks for the
/// argument; what is written out here is only why this repository's half lives beside its method
/// instead — the same placement decision <c>UserRepositoryTests</c> and
/// <c>RecoveryCodeRepositoryTests</c> record, which that file states as covering "the five repositories
/// reachable through a budget" and no others. Nothing here is reachable through a budget: a credential
/// belongs to a person, and the seeding, the intruder and the context all have to be built without an
/// ambient budget.
/// </para>
/// <para>
/// It cannot be written above this layer, and the attempt is what makes the placement worth stating.
/// Naming <c>DbUpdateConcurrencyException</c> in a handler puts the EF assembly on
/// <c>Application.csproj</c> — a reference pointing the wrong way down a dependency direction that
/// runs <c>Infrastructure → Application</c> — and a fake standing in for
/// <see cref="IPasskeyRepository" /> could only raise that type by modelling something the port never
/// surfaces. What the port promises is the line below: a credential that is already gone raises
/// <see cref="NotFoundException" />.
/// </para>
/// <para>
/// Every row is written and removed on <see cref="RepositoryTestHost.ConnectionString" /> — the
/// container superuser. What is measured here is the repository's own translation, not the isolation
/// policies; <c>RlsIsolationTests</c> owns those.
/// </para>
/// </remarks>
public sealed class PasskeyRepositoryTests
{
    [Test]
    public async Task DeletePasskeyAsync_WhenTheRowIsAlreadyGone_ThrowsNotFound()
    {
        // Arrange — a real passkey, read back through FindPasskeyCredentialAsync, the one query that
        // materialises a Credential the table actually holds and which names the owner and the type in
        // its predicate, and then removed out of band on a separate connection. That lookup is not the
        // only way to obtain a Credential: CreateFederated and CreatePasskey are both public. Each of
        // them mints its own Guid.CreateVersion7() though, so a fabricated instance names no existing
        // row — which leaves the guarantee at "naming an existing row of the caller's choosing takes a
        // new query on PasskeyRepository", a rule review enforces over one class rather than something
        // the type prevents. It has to be enforced, because credentials is exempt from row-level
        // security and this predicate is the entire scope of the delete below.
        //
        // The arrangement here is that distinction made concrete, and worth reading as one: the scoped
        // lookup yields a credential naming a real row, the out-of-band delete takes the row away, and
        // what is left is a tracked entity naming a row that no longer exists — the state an unscoped
        // source would hand the delete for free. It is also precisely what a lost delete race leaves
        // behind: both requests resolve the
        // credential, both clear the last-passkey floor, and the loser's DELETE matches zero rows
        // where EF expected one. No race is arranged and none should be — interleaving two
        // transactions at a chosen statement buys a timing-dependent test for a branch whose entire
        // input is this state, and a flaky assertion about a rare path is worse than none.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);
        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new PasskeyRepository(db);
        Credential credential = await repository.FindPasskeyCredentialAsync(credentialId, userId)
            ?? throw new InvalidOperationException(
                "The seeded passkey was not readable through the repository before the act.");
        // The manifest the revocation promotes in the same save, read and promoted in the order the
        // handler works in. THE WINNER HERE PROMOTED NOTHING — it removed one row with a bare DELETE —
        // so this UPDATE matches its row and the DELETE beside it is the only statement that finds
        // nothing. That is deliberately the SIMPLER of the two lost races on this save, and
        // DeletePasskeyAsync_WhenTheWinnerDeletedAndPromotedInOneSave_ThrowsNotFound is the one that
        // stages a real revocation as the winner, so that BOTH of the loser's statements match nothing.
        FactorManifest manifest = await PromotedManifestAsync(repository, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        int removedByTheWinner = await DeleteCredentialAsync(admin, credentialId);

        // Act
        Exception? escaped = await CaptureAsync(
            () => repository.DeletePasskeyAsync(credential, manifest));

        // Assert — the premise first. A run in which the out-of-band delete matched nothing would be
        // arranging the opposite of this test, and could still go green against a repository that
        // threw for some entirely unrelated reason.
        await Assert.That(removedByTheWinner).IsEqualTo(1);

        // 404 is the honest answer, and it is a decision rather than a convenience. The row is gone,
        // which is what this route means by "not found", and it is the answer the caller would have
        // received a moment earlier had its own lookup run after the winner's delete instead of
        // before — so the two orderings of one pair of requests become indistinguishable, which is
        // what makes a client's retry safe. A retry would find nothing, so there is nothing left to
        // retry; a 409 would say the work could not be done and invite exactly that retry at work
        // already completed. Letting the DbUpdateConcurrencyException escape is worse than either: a
        // 500 logged as a fault, describing a removal that in fact succeeded.
        await Assert.That(escaped).IsTypeOf<NotFoundException>();
    }

    /// <summary>
    /// A conflict over somebody else's entity is not dressed up as a passkey that is already gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The narrowing half of the delete, and the reason its <c>catch</c> carries a <c>when</c> at
    /// all.</b> A concurrency conflict carries no SQLSTATE and no constraint name, so the
    /// <i>entries</i> are the only thing there is to narrow on: every conflicting row must be a
    /// <see cref="Credential" /> this call itself marked <c>Deleted</c>. Drop the <c>when</c> clause and
    /// a stranger's conflict comes back as <see cref="NotFoundException" /> — a 404 telling somebody
    /// their passkey is already gone, on a request that rolled back and removed nothing, which is a
    /// client's cue to stop retrying at exactly the moment retrying is the right thing to do.
    /// </para>
    /// <para>
    /// <b>The intruder is a second passkey's signature counter</b>, loaded first, removed out of band
    /// second, and marked <c>Deleted</c> third — so the context is tracking a delete the database will
    /// match no row for. It belongs to a <em>different</em> credential on purpose: the counter of the
    /// passkey under test leaves by the database's own cascade from the row this method removes, so
    /// tracking that one would be arranging a conflict the delete itself causes rather than one riding
    /// along beside it. <c>RecoveryCodeRepositoryTests</c> stages the identical intruder against its own
    /// delete, and this is written to that shape rather than to a second one.
    /// </para>
    /// <para>
    /// <b>The passkey being revoked is deliberately still present, and so is the generation this save
    /// promotes</b>, so exactly one entry can be in the exception and the test cannot pass or fail on
    /// how EF happened to batch three statements' failures.
    /// </para>
    /// <para>
    /// <b>It is also the control on the <em>widening</em> of <see cref="PasskeyRepository" />'s
    /// already-deleted filter, which is what makes it worth more than it was.</b> That predicate now
    /// tolerates a <c>Modified</c> <see cref="FactorManifest" /> beside the <c>Deleted</c>
    /// <see cref="Credential" />, because a winning revocation deletes and promotes in one save. Widen it
    /// one notch further — drop the requirement that some conflicting entry <em>is</em> a credential —
    /// and a lost promotion alone comes back as a 404 saying the passkey is gone when it is still there,
    /// which is <see cref="DeletePasskeyAsync_WhenTheManifestGenerationMovedFirst_ThrowsFactorSetMovedAndRemovesNothing" />'s
    /// failure. Widen it to the bare exception type and a stranger's conflict comes back the same way,
    /// which is this one's. Neither test sees the other's mutation, so both have to be here.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeletePasskeyAsync_WhenAnUnrelatedEntityConflicts_LetsTheConflictEscape()
    {
        // Arrange — two passkeys: the one being revoked, which is entirely healthy, and a bystander
        // whose signature counter is not.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);
        Guid bystanderId = await host.SeedPasskeyAsync(userId, UnregisteredWebAuthnCredentialId);

        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new PasskeyRepository(db);
        Credential credential = await repository.FindPasskeyCredentialAsync(credentialId, userId)
            ?? throw new InvalidOperationException(
                "The seeded passkey was not readable through the repository before the act.");

        PasskeySignatureCounter counter = await db.PasskeySignatureCounters
            .SingleAsync(tracked => tracked.CredentialId == bystanderId);

        // The revocation's own manifest promotion, which nothing has overtaken: its UPDATE matches the
        // row, so the only statement in this save that finds nothing is the intruder's DELETE and the
        // exception names exactly one entry.
        FactorManifest manifest = await PromotedManifestAsync(repository, userId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        int removedOutOfBand = await DeleteSignatureCounterAsync(admin, bystanderId);
        db.PasskeySignatureCounters.Remove(counter);

        // Act
        Exception? escaped = await CaptureAsync(
            () => repository.DeletePasskeyAsync(credential, manifest));

        // Assert — the premise first, or the exception below was raised by something this test did not
        // arrange.
        await Assert.That(removedOutOfBand).IsEqualTo(1);

        // A 500 naming a conflict this method does not model beats a 404 claiming a revocation that a
        // rolled-back transaction did not perform.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();
    }

    /// <summary>
    /// A revocation whose manifest promotion was overtaken is refused as a conflict naming the moved
    /// factor set — and the passkey it was asked to remove is still there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="TryAddAsync_WhenTheManifestGenerationMovedFirst_ThrowsFactorSetMovedAndWritesNothing" />'s
    /// claim on the save that takes a factor AWAY, and it is not covered by it.</b> The two saves are
    /// different statement batches reaching different <c>catch</c> blocks in the same file: that one
    /// emits four INSERTs and one UPDATE, this one emits one DELETE and one UPDATE, and this one has a
    /// <em>second</em> filter sitting in front of the manifest's — <c>IsAlreadyDeleted</c> — that the
    /// registration save does not have. Nothing before this case had ever raised a
    /// <see cref="DbUpdateConcurrencyException" /> on the delete's manifest half at all.
    /// </para>
    /// <para>
    /// <b>409 and not 404 here, which is the whole of what separates this from its neighbour below.</b>
    /// The credential this caller named is still in the table — the winner promoted a generation and
    /// removed nothing — so telling them the passkey is gone would be a lie that stops them retrying at
    /// exactly the moment a retry is the right thing to do. What they have to do first is real work:
    /// read the account's keys back and reseal a manifest over the generation it now reports.
    /// </para>
    /// <para>
    /// <b>The winner promotes and does nothing else</b>, which is what "another change to this account's
    /// factor set landed first" is at the row level — a registration, an issue of recovery codes and
    /// another revocation are indistinguishable from here, and this repository must not depend on which.
    /// It commits through a context of its own for the reason every race in this file does: written
    /// through the context under test it would travel inside the same unit of work, where the loser's
    /// UPDATE could not fail to see it.
    /// </para>
    /// <para>
    /// <b>Nothing of the loser's landed either</b>, and the DELETE is the half worth counting. One save
    /// is one batch, so a refused promotion takes the credential's removal back with it; a repository
    /// that promoted in a save of its own would leave a passkey deleted and a 409 telling its owner that
    /// nothing happened.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeletePasskeyAsync_WhenTheManifestGenerationMovedFirst_ThrowsFactorSetMovedAndRemovesNothing()
    {
        // Arrange — one account, one passkey, at the generation the seeding left.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);

        // The loser: the credential resolved through the scoped lookup, and the manifest read and
        // promoted before the winner exists. Read FIRST, so the entity's original values really are the
        // generation this attempt started from — the order is the arrangement rather than tidiness.
        await using BudgetoidDbContext loserDb = CreateDb(host);
        var loser = new PasskeyRepository(loserDb);
        Credential credential = await loser.FindPasskeyCredentialAsync(credentialId, userId)
            ?? throw new InvalidOperationException(
                "The seeded passkey was not readable through the repository before the act.");
        FactorManifest losersManifest = await PromotedManifestAsync(loser, userId);

        // The winner: the same row, promoted and committed by another session. Its bytes are its own, so
        // the read-back below can say WHOSE generation survived rather than only that one did.
        ManifestFixture winnersManifest = ManifestFixture.Mint();
        await using (BudgetoidDbContext winnerDb = CreateDb(host))
        {
            var winner = new PasskeyRepository(winnerDb);
            FactorManifest stored = await winner.FindFactorManifestAsync(userId)
                ?? throw new InvalidOperationException("The seeded account holds no factor manifest.");
            stored.Promote(winnersManifest.Manifest, stored.RotationEpoch + 1);
            await winnerDb.SaveChangesAsync();
        }

        // Act
        Exception? escaped = await CaptureAsync(
            () => loser.DeletePasskeyAsync(credential, losersManifest));

        // Assert — the premise first: the two attempts really did claim the same generation, or the
        // token had nothing to refuse and everything below is about a race that did not happen.
        await Assert.That(losersManifest.RotationEpoch).IsEqualTo(SeededRotationEpoch + 1);

        // Translated, not raw, and not the delete's own NotFoundException either. The kind is asserted
        // beside the type because the two this save can raise ask for opposite things of a caller.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ConflictException>();
        await Assert.That(((ConflictException)escaped!).Kind).IsEqualTo(ConflictKind.FactorSetMoved);
        await Assert.That(string.IsNullOrWhiteSpace(escaped.Message)).IsFalse();

        // And the passkey is whole — all three rows, on a context of its own because the one under test
        // is still holding a Deleted credential. The public key and the counter are counted beside the
        // credential because they leave by the database's own cascade: a DELETE that had committed takes
        // all three, so any one of them surviving alone would be a state no path produces.
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Credentials.AnyAsync(row => row.Id == credentialId)).IsTrue();
        await Assert.That(await verify.PasskeyPublicKeys.AnyAsync(row => row.CredentialId == credentialId))
            .IsTrue();
        await Assert.That(await verify.PasskeySignatureCounters.AnyAsync(row => row.CredentialId == credentialId))
            .IsTrue();

        // And the row still holds the WINNER'S generation — both halves of it. The epoch alone would be
        // satisfied by a loser whose UPDATE landed anyway, because both attempts computed the same
        // number: it is the BYTES that say which of the two factor sets the account is now claiming.
        FactorManifest survivor = await verify.FactorManifests.SingleAsync(row => row.UserId == userId);
        await Assert.That(survivor.RotationEpoch).IsEqualTo(SeededRotationEpoch + 1);
        await Assert.That(survivor.Manifest.ToArray())
            .IsEquivalentTo(winnersManifest.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A double-tapped revocation: the winner deletes the credential <b>and</b> promotes the generation
    /// in one save, and the loser is answered 404 rather than 409 or 500.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the shape <c>IsAlreadyDeleted</c> was widened for, and nothing else arranges it.</b>
    /// <see cref="DeletePasskeyAsync_WhenTheRowIsAlreadyGone_ThrowsNotFound" /> removes the row out of
    /// band with a bare <c>DELETE</c> and no competing promotion, so its loser's save has exactly one
    /// statement matching nothing. A real winner is a whole revocation, and a whole revocation moves two
    /// rows — so here the loser's <em>save</em> carries a <c>DELETE</c> and an <c>UPDATE</c> that both
    /// match nothing, which is the state the widening was written against.
    /// </para>
    /// <para>
    /// <b>Be precise about what that does and does not prove, because the obvious reading is wrong.</b>
    /// The widening tolerates a <c>Modified</c> <see cref="FactorManifest" /> arriving beside the
    /// <c>Deleted</c> <see cref="Credential" /> — and on this stack that pair never arrives.
    /// <see cref="DeletePasskeyAsync_WhenBothStatementsMatchNothing_ReportsOnlyTheFirstFailingStatement" />
    /// measures it: EF Core 10 over Npgsql reports the first failing command and nothing after it, so
    /// this case is green under the strict <c>All(entry is Credential)</c> filter exactly as it is under
    /// the widened one. <b>It does not hold the widening.</b> What it holds is the answer — that the
    /// commonest race this method has is a 404 — against a real winner rather than against a hand-written
    /// <c>DELETE</c>, and it is the case that reddens the day either the provider's aggregation or the
    /// batch's statement order changes underneath the choice.
    /// </para>
    /// <para>
    /// <b>404 beats 409 here, and the two losses are not weighed equally.</b> Both races were lost, so
    /// there is a choice about which to report. The passkey the caller asked to have removed is gone and
    /// is going to stay gone: a retry finds nothing, and the answer to a retry is this same 404. Telling
    /// them instead that the factor set moved invites the one remedy the neighbouring case asks for —
    /// read the account's keys back, reseal a manifest, prove presence again — and spends all of it on a
    /// request that can only answer 404 at the end of it. The generation is not left stale by the
    /// choice: the winner promoted it, so the account's statement of its factor set is the winner's and
    /// is correct.
    /// </para>
    /// <para>
    /// <b>The winner is the production method rather than two hand-written statements</b>, which is what
    /// makes the loser's exception the one a real double tap produces. A test that removed the row and
    /// bumped the epoch with SQL of its own would be arranging the shape it believes production has,
    /// which is precisely the belief under test.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeletePasskeyAsync_WhenTheWinnerDeletedAndPromotedInOneSave_ThrowsNotFound()
    {
        // Arrange — one account, one passkey, two contexts about to revoke the same row.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);

        // The loser resolves and promotes FIRST, so its manifest's original values are the generation
        // the winner is about to take.
        await using BudgetoidDbContext loserDb = CreateDb(host);
        var loser = new PasskeyRepository(loserDb);
        Credential losersCredential = await loser.FindPasskeyCredentialAsync(credentialId, userId)
            ?? throw new InvalidOperationException(
                "The seeded passkey was not readable through the repository before the act.");
        FactorManifest losersManifest = await PromotedManifestAsync(loser, userId);

        // The winner: a whole revocation, through the method under test, committed by another session.
        await using (BudgetoidDbContext winnerDb = CreateDb(host))
        {
            var winner = new PasskeyRepository(winnerDb);
            Credential winnersCredential = await winner.FindPasskeyCredentialAsync(credentialId, userId)
                ?? throw new InvalidOperationException("The winner could not resolve the passkey.");
            await winner.DeletePasskeyAsync(winnersCredential, await PromotedManifestAsync(winner, userId));
        }

        // Act
        Exception? escaped = await CaptureAsync(
            () => loser.DeletePasskeyAsync(losersCredential, losersManifest));

        // Assert — the premise first: the winner really did move both rows, or this is the neighbouring
        // one-statement race under another name.
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Credentials.AnyAsync(row => row.Id == credentialId)).IsFalse();
        await Assert.That(
                (await verify.FactorManifests.SingleAsync(row => row.UserId == userId)).RotationEpoch)
            .IsEqualTo(SeededRotationEpoch + 1);

        // The delete's own answer, and NOT the manifest's: see the remarks for why the 404 is the one
        // worth giving when both races were lost.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<NotFoundException>();
    }

    /// <summary>
    /// What the loser of a double tap is actually handed: <b>one</b> conflicting entry, the credential's,
    /// although two of its statements matched no row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a measurement, and it says the opposite of what a reader expects.</b> EF Core's
    /// concurrency machinery is documented as aggregating a batch's failures, and
    /// <c>PasskeyRepository.IsAlreadyDeleted</c>'s remarks are written on that premise — a <c>Deleted</c>
    /// <see cref="Credential" /> and a <c>Modified</c> <see cref="FactorManifest" /> arriving together,
    /// which is what its widening exists to tolerate. On this stack — EF Core 10 over Npgsql, batched —
    /// that pair never arrives: the exception names the <b>first</b> command whose row count was wrong
    /// and nothing after it. Measured twice, because one measurement could have been about the
    /// <c>UPDATE</c> having quietly succeeded: a save queueing two <c>DELETE</c>s that both match nothing
    /// reports one entry as well.
    /// </para>
    /// <para>
    /// <b>What follows for the neighbour above, stated rather than left for somebody to discover.</b>
    /// The double tap is answered 404 because the credential's entry is the only one there, so it
    /// satisfies the strict <c>All(entry is Credential)</c> filter exactly as it satisfies the widened
    /// one — the neighbour is green under both and cannot be the thing that holds the widening. The
    /// widening is therefore <em>unexercised</em> today, and it is this case that says so out loud rather
    /// than the suite implying otherwise by staying green.
    /// </para>
    /// <para>
    /// <b>It is still a pin worth keeping, and it is one that reddens in both directions.</b> A provider
    /// that begins aggregating makes this case report the pair — at which point the widening starts
    /// earning its keep and the 404 above keeps holding, while an un-widened filter would answer 500. A
    /// batch whose statement order put the manifest's <c>UPDATE</c> first makes it report
    /// <c>FactorManifest/Modified</c> instead — at which point the double tap silently becomes a 409
    /// asking a caller to reseal a manifest for a request that can only answer 404, and the neighbour
    /// reddens beside this one. Both are behaviours of a dependency this repository does not control, so
    /// they belong in a test rather than in a belief.
    /// </para>
    /// <para>
    /// <b>It queues the two statements itself rather than calling the method under test</b>, which is the
    /// one place in this file that is deliberate rather than a shortcut: what is being observed is the
    /// <em>exception</em>, and <see cref="PasskeyRepository.DeletePasskeyAsync" /> translates it into a
    /// type carrying no entries at all. The two statements are the two that method queues, in the states
    /// it queues them — <c>Remove</c> on the credential and a promotion on a tracked manifest — so the
    /// batch this arranges is the batch it emits.
    /// </para>
    /// </remarks>
    [Test]
    public async Task DeletePasskeyAsync_WhenBothStatementsMatchNothing_ReportsOnlyTheFirstFailingStatement()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);

        await using BudgetoidDbContext loserDb = CreateDb(host);
        var loser = new PasskeyRepository(loserDb);
        Credential credential = await loser.FindPasskeyCredentialAsync(credentialId, userId)
            ?? throw new InvalidOperationException(
                "The seeded passkey was not readable through the repository before the act.");
        // Discarded, because what this line is for is the promotion it performs on the tracked instance:
        // that is what queues the UPDATE the save below carries beside the DELETE.
        _ = await PromotedManifestAsync(loser, userId);

        // The winner: the same whole revocation, through the method under test, on another session.
        await using (BudgetoidDbContext winnerDb = CreateDb(host))
        {
            var winner = new PasskeyRepository(winnerDb);
            Credential winnersCredential = await winner.FindPasskeyCredentialAsync(credentialId, userId)
                ?? throw new InvalidOperationException("The winner could not resolve the passkey.");
            await winner.DeletePasskeyAsync(winnersCredential, await PromotedManifestAsync(winner, userId));
        }

        // Act — the loser's two statements, saved directly so the raw exception survives the call.
        loserDb.Credentials.Remove(credential);
        Exception? escaped = await CaptureAsync(() => loserDb.SaveChangesAsync());

        // Assert
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();

        // Rendered as text rather than probed with a predicate, so a failure prints what the provider DID
        // report instead of only that something was or was not there — which is the whole question this
        // case asks, and the answer a reader will want when it changes. Ordered, so a day on which two
        // entries do arrive produces the same message whichever order the batch put them in.
        string reported = string.Join(
            ", ",
            ((DbUpdateConcurrencyException)escaped!).Entries
                .Select(entry => $"{entry.Entity.GetType().Name}/{entry.State}")
                .Order(StringComparer.Ordinal));

        await Assert.That(reported).IsEqualTo("Credential/Deleted");
    }

    /// <summary>
    /// A registration of an authenticator this account already holds is refused, and leaves nothing
    /// behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The translation half, without which the control below could be satisfied by deleting the
    /// <c>catch</c> outright.</b> <c>PasskeyCeremonyTests</c> takes the same refusal as a 409 over HTTP,
    /// which is the answer a client sees; this is the same rule read at the layer that decides it, and
    /// the two are not redundant in the direction that matters here — a widened <c>when</c> clause is
    /// invisible from the route, because a route only ever stages the violation the repository does
    /// model.
    /// </para>
    /// <para>
    /// <b>The winner is committed by a context of its own.</b> Written through the context under test it
    /// would travel inside the same unit of work, where this insert could not fail to see it, and the
    /// collision would be EF's rather than the database's — the reason
    /// <c>RecoveryCodeRepositoryTests</c> gives for the same arrangement.
    /// </para>
    /// <para>
    /// The registration is aimed at the <b>same</b> account, which is the shape a person re-registering
    /// an authenticator they already enrolled produces, and it is also the sharper of the two: the
    /// account is allowed to hold a second passkey — <c>UserRepositoryTests</c> pins that — so nothing
    /// but the handle index can refuse this row. The rows are counted afterwards because the promise is
    /// one save: a refusal that left the credential behind without its key would be a passkey nothing
    /// can verify a signature against, and it would satisfy a bare <c>IsFalse</c> perfectly.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryAddAsync_WhenTheHandleIsAlreadyRegistered_ReturnsFalse()
    {
        // Arrange — the winner's passkey, committed by another session.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid winnerId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);

        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new PasskeyRepository(db);
        (Credential credential, PasskeyPublicKey publicKey, PasskeySignatureCounter counter,
            WrappedAccountKeys wrappedAccountKeys) = NewPasskeyFor(userId, WebAuthnCredentialId);

        // The account's manifest, read through the repository so it is tracked, and promoted to the
        // generation this registration claims. Loaded before the act because that is the order the
        // handler works in — and because the concurrency token only means anything on an instance whose
        // original values came back from the database.
        FactorManifest manifest = await PromotedManifestAsync(repository, userId);

        // Act
        bool added = await repository.TryAddAsync(
            credential, publicKey, counter, wrappedAccountKeys, manifest);

        // Assert — refused, and the winner is still the account's.
        await Assert.That(added).IsFalse();

        // Nothing of the loser's survives, and each row is looked for by the loser's own id rather
        // than by a count: the winner's rows are still there, so a count would be answering a
        // question about the seed.
        //
        // ALL FOUR ROWS, because all four are what the one save promises. Three of them checked would
        // stay green on a refusal that left the wrapped account keys behind — a factor's share of the
        // account's keys filed against a credential the same statement rolled back, which nothing else
        // in this suite reads and no screen in the product would ever show.
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Credentials.AnyAsync(row => row.Id == credential.Id)).IsFalse();
        await Assert.That(await verify.PasskeyPublicKeys.AnyAsync(row => row.CredentialId == credential.Id))
            .IsFalse();
        await Assert.That(await verify.PasskeySignatureCounters.AnyAsync(row => row.CredentialId == credential.Id))
            .IsFalse();
        await Assert.That(await verify.WrappedAccountKeys.AnyAsync(row => row.CredentialId == credential.Id))
            .IsFalse();

        // And by the factor identifier as well, which is the column a client chose and the one a row
        // surviving under a different credential would still carry.
        await Assert.That(await verify.WrappedAccountKeys
                .AnyAsync(row => row.FactorId == wrappedAccountKeys.FactorId))
            .IsFalse();
        await Assert.That(await verify.Credentials.AnyAsync(row => row.Id == winnerId)).IsTrue();

        // AND THE GENERATION DID NOT MOVE, which is a fifth row and the only one that was UPDATEd
        // rather than inserted. The refusal detaches it rather than reloading it, so nothing here
        // emitted the statement — but the entity was mutated in place before the save, and a
        // registration that left it Modified would have any later save through this scoped context
        // commit a promotion for a ceremony that wrote nothing. Read on a context of its own, because
        // the one under test is still holding that entity.
        await Assert.That(
                (await verify.FactorManifests.SingleAsync(row => row.UserId == userId)).RotationEpoch)
            .IsEqualTo(SeededRotationEpoch);
    }

    /// <summary>
    /// A unique violation this repository does not model is not dressed up as an authenticator that is
    /// already registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The narrowing half, and the reason the <c>catch</c> carries a constraint name at all.</b>
    /// <c>TryAddAsync</c> writes three rows and <c>SaveChangesAsync</c> flushes every other tracked one
    /// beside them, so a <c>23505</c> reaching that <c>catch</c> says only that <i>some</i> unique rule
    /// broke. Widen the <c>when</c> clause to the bare SQLSTATE and this test's arrangement — a
    /// stranger's email collision — comes back as <see langword="false" />, which the ceremony above
    /// reports as "this authenticator is already registered": a confident, specific, false 409 about a
    /// handle no row in the table holds.
    /// </para>
    /// <para>
    /// <b>The intruder is a second <c>users</c> row reusing an address the seeded account already
    /// holds</b>, breaking <c>IX_users_email</c> with the same <c>23505</c> the handle index would
    /// raise. Not even a row this repository has a port for, which is the point:
    /// <c>RepositoryConstraintAttributionTests</c> stages the identical intruder against
    /// <c>BudgetRepository</c> for the same reason — the tracked graph is wider than any one
    /// repository's subject. A users row with no credential is legal at the schema level, so exactly
    /// one rule is broken and the test cannot pass or fail on which of two violations PostgreSQL
    /// reported first.
    /// </para>
    /// <para>
    /// <b>The passkey being registered carries a handle nothing holds</b>, so the index this method
    /// does model is untouched and the refusal is attributable to the intruder alone.
    /// </para>
    /// <para>
    /// The SQLSTATE is asserted beside the constraint name, which is what keeps this a narrowing test
    /// rather than a test that any failure escapes: a violation of some entirely different kind would
    /// satisfy "not the handle index" without ever exercising the filter. The expected behaviour is
    /// that an unmodelled violation <b>propagates</b> — a 500 naming the real constraint beats a 409
    /// that lies.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryAddAsync_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape()
    {
        // Arrange — an account, and a second users row arriving with that account's address.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", TakenEmail);

        await using BudgetoidDbContext db = CreateDb(host);
        db.Users.Add(User.CreateWithId(Guid.CreateVersion7(), TakenEmail, SeedInstant));
        var repository = new PasskeyRepository(db);

        // The registration itself is flawless: a handle no row in the table carries, and a factor
        // identifier freshly minted.
        (Credential credential, PasskeyPublicKey publicKey, PasskeySignatureCounter counter,
            WrappedAccountKeys wrappedAccountKeys) = NewPasskeyFor(userId, UnregisteredWebAuthnCredentialId);
        FactorManifest manifest = await PromotedManifestAsync(repository, userId);

        // Act
        Exception? escaped = await CaptureAsync(
            () => repository.TryAddAsync(
                credential, publicKey, counter, wrappedAccountKeys, manifest));

        // Assert — something escaped, which is already the claim: a swallowed violation would have
        // returned false and left this null.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateException>();

        // And it really was a unique violation — on a rule that is not this repository's to speak for.
        await Assert.That(SqlStateOf(escaped)).IsEqualTo(PostgresErrorCodes.UniqueViolation);
        await Assert.That(ConstraintNameOf(escaped)).IsEqualTo(UserEmailIndex);
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(PasskeyPublicKeyConfiguration.WebAuthnCredentialIdIndexName);

        // Nor the other unique rule this save now writes against — wrapped_account_keys' PRIMARY KEY,
        // which is where the uniqueness of the client-minted factor id lives now that the key is
        // factor_id and credential_id is an ordinary non-unique column. TryAddAsync has two 23505
        // filters and they answer differently — one returns false, the other throws — so a stranger's
        // violation widened into either is a lie, and only naming both says this arrangement escaped
        // both.
        await Assert.That(ConstraintNameOf(escaped))
            .IsNotEqualTo(WrappedAccountKeysConfiguration.PrimaryKeyName);
    }

    /// <summary>
    /// The address both the seeded account and the intruding row carry. A constant because the
    /// collision is the arrangement: two literals that happened to match would be a coincidence a
    /// reader has to verify.
    /// </summary>
    private const string TakenEmail = "person@example.com";

    /// <summary>
    /// The unique index <c>users.email</c> carries, spelled out rather than read off
    /// <c>UserConfiguration</c>. A test that took its expectation from the configuration the schema was
    /// rendered from would agree with it by construction; the habit is
    /// <c>RepositoryConstraintAttributionTests</c>', which spells the same name as a literal for the
    /// same reason.
    /// </summary>
    private const string UserEmailIndex = "IX_users_email";

    /// <summary>
    /// Fixed UTC instant for the rows these tests write themselves. PostgreSQL <c>timestamptz</c>
    /// rejects a non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The WebAuthn handle the seeded passkey carries. Nothing here verifies a signature, so the
    /// bytes are arbitrary — but there are
    /// <see cref="PasskeyPublicKey.MinWebAuthnCredentialIdLength" /> of them, because a shorter handle
    /// is refused by the domain factory and the seed would fail before the act.
    /// </summary>
    private static readonly byte[] WebAuthnCredentialId =
        [.. Enumerable.Range(1, PasskeyPublicKey.MinWebAuthnCredentialIdLength).Select(value => (byte)value)];

    /// <summary>
    /// A second handle, differing from <see cref="WebAuthnCredentialId" /> in every byte, for the
    /// registration that must be refused by somebody else's rule rather than by its own.
    /// </summary>
    private static readonly byte[] UnregisteredWebAuthnCredentialId =
        [.. Enumerable.Range(1, PasskeyPublicKey.MinWebAuthnCredentialIdLength).Select(value => (byte)(value + 128))];

    /// <summary>
    /// The COSE key registered passkeys carry here. Four bytes: nothing in this file verifies a
    /// signature, and the only rule the column holds is that the key is between one byte and
    /// <see cref="PasskeyPublicKey.MaxCoseKeyLength" />.
    /// </summary>
    private static readonly byte[] CoseKey = [0xA5, 0x01, 0x02, 0x03];

    /// <summary>
    /// Builds the four rows a registration writes — the credential the key hangs off, the key itself,
    /// the counter a clone gives itself away against, and the factor's share of the account keys —
    /// without writing any of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Through the domain factories rather than assembled from loose ids, for the reason
    /// <c>RepositoryTestHost.SeedPasskeyAsync</c> gives: <see cref="PasskeyPublicKey.Register" />,
    /// <see cref="PasskeySignatureCounter.Start" /> and <see cref="WrappedAccountKeys.For" /> copy the
    /// owner and the type off the credential itself, which are the columns the composite foreign keys
    /// compare against <c>credentials(id, user_id, type)</c>. A test that built them from ids of its
    /// own could arrange a shape no ceremony can produce, and would then be measuring a schema nobody
    /// ships.
    /// </para>
    /// <para>
    /// The factor identifier and both envelopes come from <see cref="WrappedKeyFixture.Mint" />, so
    /// each call gets its own: <c>factor_id</c> is the table's primary key —
    /// <c>PK_wrapped_account_keys</c> — unique across the whole table rather than per account, and a
    /// value shared between two calls would refuse the second
    /// registration with a <c>23505</c> no test in this file is reading.
    /// </para>
    /// </remarks>
    private static (Credential Credential, PasskeyPublicKey PublicKey, PasskeySignatureCounter Counter,
        WrappedAccountKeys WrappedAccountKeys)
        NewPasskeyFor(Guid userId, byte[] webAuthnCredentialId)
    {
        Credential credential = Credential.CreatePasskey(userId, SeedInstant);
        WrappedKeyFixture keys = WrappedKeyFixture.Mint();

        return (
            credential,
            PasskeyPublicKey.Register(credential, webAuthnCredentialId, CoseKey, CoseAlgorithm.Es256),
            PasskeySignatureCounter.Start(credential, value: 0),
            WrappedAccountKeys.For(
                credential, keys.Factor, keys.PrivateKeyEnvelope, keys.AccountKeysEnvelope, SeedInstant));
    }

    /// <summary>
    /// A registration whose manifest promotion was overtaken is refused as a conflict naming the moved
    /// factor set, and writes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The first execution of that <c>catch</c>, not a regression guard for it.</b>
    /// <c>PasskeyRepository.IsManifestPromotionLost</c> narrows on
    /// <c>entry.Entity is FactorManifest &amp;&amp; entry.State == EntityState.Modified</c>, and until
    /// this case nothing in either suite had ever raised a <c>DbUpdateConcurrencyException</c> on this
    /// save — so the predicate was held by reading it. What it is being asked here is a question only a
    /// real batch can answer: <c>TryAddAsync</c> emits four INSERTs and one UPDATE together, and the
    /// filter demands that <em>every</em> entry EF attributes the conflict to is the manifest. A
    /// provider that attributed the whole batch would leave the raw EF exception escaping as a 500, and
    /// the assertion below is the difference between the two.
    /// </para>
    /// <para>
    /// <b>It is also the only thing in the repository that would redden deleting
    /// <c>.IsConcurrencyToken()</c> from <c>FactorManifestConfiguration</c>.</b> That call has no
    /// relational artifact — no column, no constraint, nothing a schema census can read — so without
    /// this case removing it changes no test in either direction. With it gone the loser's UPDATE
    /// carries no <c>WHERE rotation_epoch = @original</c>, matches the winner's row, and quietly
    /// overwrites the winner's manifest under the winner's generation: two factor sets, one row, and
    /// whichever list lost is a set of factors no client can learn exists.
    /// </para>
    /// <para>
    /// <b>The winner commits through a context of its own</b>, which is the arrangement every race in
    /// this file uses and for the reason
    /// <see cref="TryAddAsync_WhenTheHandleIsAlreadyRegistered_ReturnsFalse" /> gives: written through
    /// the context under test it would travel inside the same unit of work, where the loser's UPDATE
    /// could not fail to see it. The winner promotes and saves nothing else, because a promotion alone
    /// is what "another change to the factor set landed first" is at the row level — which of the two
    /// routes committed it is a fact this repository cannot see and must not depend on.
    /// </para>
    /// <para>
    /// <b>The answer is asserted as the translated conflict AND as its kind.</b> The type alone would
    /// be satisfied by the neighbouring <c>FactorAlreadyRegistered</c> catch, whose remedy is the
    /// opposite work — mint a fresh factor identifier and re-wrap the account keys, against re-seal a
    /// manifest over a generation that moved. A caller told the wrong one re-does work that was never
    /// wrong and leaves the thing that was.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryAddAsync_WhenTheManifestGenerationMovedFirst_ThrowsFactorSetMovedAndWritesNothing()
    {
        // Arrange — one account at the generation the seeding left it at.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");

        // The loser: a whole registration, and a manifest read and promoted before the winner exists.
        // Read FIRST, so the entity's original values really are the generation this attempt started
        // from — the order is the arrangement rather than tidiness.
        await using BudgetoidDbContext loserDb = CreateDb(host);
        var loser = new PasskeyRepository(loserDb);
        (Credential credential, PasskeyPublicKey publicKey, PasskeySignatureCounter counter,
            WrappedAccountKeys wrappedAccountKeys) = NewPasskeyFor(userId, UnregisteredWebAuthnCredentialId);
        FactorManifest losersManifest = await PromotedManifestAsync(loser, userId);

        // The winner: the same row, promoted and committed by another session. Its bytes are its own,
        // so the read-back below can say WHOSE generation survived rather than only that one did.
        ManifestFixture winnersManifest = ManifestFixture.Mint();
        await using (BudgetoidDbContext winnerDb = CreateDb(host))
        {
            var winner = new PasskeyRepository(winnerDb);
            FactorManifest stored = await winner.FindFactorManifestAsync(userId)
                ?? throw new InvalidOperationException("The seeded account holds no factor manifest.");
            stored.Promote(winnersManifest.Manifest, stored.RotationEpoch + 1);
            await winnerDb.SaveChangesAsync();
        }

        // Act
        Exception? escaped = await CaptureAsync(
            () => loser.TryAddAsync(credential, publicKey, counter, wrappedAccountKeys, losersManifest));

        // Assert — the premise first: the two attempts really did claim the same generation, or the
        // token had nothing to refuse and everything below is about a race that did not happen.
        await Assert.That(losersManifest.RotationEpoch).IsEqualTo(SeededRotationEpoch + 1);

        // Translated, not raw. A DbUpdateConcurrencyException reaching the pipeline is a 500 telling a
        // caller whose arithmetic was right that the server broke.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<ConflictException>();
        await Assert.That(((ConflictException)escaped!).Kind).IsEqualTo(ConflictKind.FactorSetMoved);

        // And it says something, because a 409 with no detail leaves a client with no idea whether to
        // retry — and this caller has real work to do before they can.
        await Assert.That(string.IsNullOrWhiteSpace(escaped.Message)).IsFalse();

        // Nothing of the loser's survives. All four rows, each by the loser's own id rather than by a
        // count, because the promise of the one save is that the factor and the generation land
        // together or not at all.
        await using BudgetoidDbContext verify = CreateDb(host);
        await Assert.That(await verify.Credentials.AnyAsync(row => row.Id == credential.Id)).IsFalse();
        await Assert.That(await verify.PasskeyPublicKeys.AnyAsync(row => row.CredentialId == credential.Id))
            .IsFalse();
        await Assert.That(await verify.PasskeySignatureCounters.AnyAsync(row => row.CredentialId == credential.Id))
            .IsFalse();
        await Assert.That(await verify.WrappedAccountKeys.AnyAsync(row => row.CredentialId == credential.Id))
            .IsFalse();

        // And the row still holds the WINNER'S generation — both halves of it. The epoch alone would be
        // satisfied by a loser whose UPDATE landed anyway, because both attempts computed the same
        // number: it is the BYTES that say which of the two factor sets the account is now claiming.
        FactorManifest survivor = await verify.FactorManifests.SingleAsync(row => row.UserId == userId);
        await Assert.That(survivor.RotationEpoch).IsEqualTo(SeededRotationEpoch + 1);
        await Assert.That(survivor.Manifest.ToArray())
            .IsEquivalentTo(winnersManifest.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A concurrency conflict over some other tracked row is not dressed up as a moved factor set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The narrowing half of the case above, and without it that one is satisfied by a
    /// <c>catch (DbUpdateConcurrencyException)</c> with no <c>when</c> clause at all.</b> The two
    /// belong together for the reason this class already pairs
    /// <see cref="TryAddAsync_WhenTheHandleIsAlreadyRegistered_ReturnsFalse" /> with
    /// <see cref="TryAddAsync_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape" />: a
    /// translation with no narrowing is a repository speaking for rules that are not its own.
    /// </para>
    /// <para>
    /// <b>The mis-attribution mechanism is the one that file's remarks describe.</b>
    /// <c>SaveChangesAsync</c> flushes everything the scoped context is tracking, not only what the
    /// repository was handed — so a conflict reaching that <c>catch</c> says only that <em>some</em>
    /// tracked row went out from under this unit of work. Here the row is a <b>bystander's signature
    /// counter</b>, removed out of band and then removed again through the tracker, which is a
    /// zero-row DELETE EF raises on; the registration itself is beyond reproach, its handle is one no
    /// row carries, and its manifest promotion is from the generation the seeding left. So the only
    /// thing that can decide the answer is whether the filter reads the entries.
    /// </para>
    /// <para>
    /// <b>The expected behaviour is that it PROPAGATES.</b> A 500 naming a conflict this method does
    /// not model beats a 409 telling a caller that the account's factor set moved — work they would
    /// then do, correctly, to a request that was never refused for that.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TryAddAsync_WhenAnotherTrackedRowConflicts_LetsTheConflictEscape()
    {
        // Arrange — an account and a bystander passkey whose counter this context will track.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid bystanderId = await host.SeedPasskeyAsync(userId, WebAuthnCredentialId);

        await using BudgetoidDbContext db = CreateDb(host);
        var repository = new PasskeyRepository(db);

        PasskeySignatureCounter counter = await db.PasskeySignatureCounters
            .SingleAsync(tracked => tracked.CredentialId == bystanderId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        int removedOutOfBand = await DeleteSignatureCounterAsync(admin, bystanderId);
        db.PasskeySignatureCounters.Remove(counter);

        // The registration is flawless: a handle nothing holds, a freshly minted factor, and a manifest
        // promoted from the generation the seeding left.
        (Credential credential, PasskeyPublicKey publicKey, PasskeySignatureCounter newCounter,
            WrappedAccountKeys wrappedAccountKeys) = NewPasskeyFor(userId, UnregisteredWebAuthnCredentialId);
        FactorManifest manifest = await PromotedManifestAsync(repository, userId);

        // Act
        Exception? escaped = await CaptureAsync(
            () => repository.TryAddAsync(credential, publicKey, newCounter, wrappedAccountKeys, manifest));

        // Assert — the premise first, or the exception below was raised by something this test did not
        // arrange.
        await Assert.That(removedOutOfBand).IsEqualTo(1);

        // Raw, not translated. A ConflictException here is this repository claiming a rule it does not
        // hold, and the kind it would claim is the one whose remedy is real work.
        await Assert.That(escaped).IsNotNull();
        await Assert.That(escaped).IsTypeOf<DbUpdateConcurrencyException>();
    }

    /// <summary>
    /// The generation <c>RepositoryTestHost.SeedUserAsync</c> files an account's first manifest at,
    /// which is where every account in this file starts.
    /// </summary>
    /// <remarks>
    /// Read off <see cref="FactorManifest.MinimumRotationEpoch" /> rather than written out as
    /// <c>1</c>, and the difference is which claim is being made. <c>FactorManifestTests</c> writes the
    /// floor out because it is <em>about</em> the floor, and a test that read the constant it checks
    /// would compare a constant with itself. Nothing here is about the floor: these cases need the
    /// generation the seeding actually left, and a literal would silently become the wrong arrangement
    /// the day that moved — the assertions would then be reading a number nobody stored.
    /// </remarks>
    private static int SeededRotationEpoch => FactorManifest.MinimumRotationEpoch;

    /// <summary>
    /// Reads the account's manifest through the repository and promotes it to the next generation —
    /// the two steps every caller of <see cref="PasskeyRepository.TryAddAsync" /> performs, in the
    /// order the handler performs them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Through <see cref="PasskeyRepository.FindFactorManifestAsync" /> and never through a context
    /// query of its own, because the tracking is the point.</b> The step is checked against the
    /// generation the instance was loaded at, and EF builds <c>WHERE rotation_epoch = @original</c>
    /// from the value snapshotted at that load — so a helper that built a detached instance with
    /// <see cref="FactorManifest.For" /> would hand every case here an entity whose original values are
    /// its current ones, and the save under test would carry a predicate guarding nothing.
    /// </para>
    /// <para>
    /// It throws rather than returning null on a miss, so an account seeded without a manifest fails at
    /// the arrangement instead of somewhere inside the act — which is the same distinction production
    /// draws by raising rather than branching.
    /// </para>
    /// </remarks>
    private static async Task<FactorManifest> PromotedManifestAsync(
        PasskeyRepository repository,
        Guid userId)
    {
        FactorManifest manifest = await repository.FindFactorManifestAsync(userId)
            ?? throw new InvalidOperationException(
                "The seeded account holds no factor manifest, so there is no generation to promote.");

        manifest.Promote(ManifestFixture.Mint().Manifest, manifest.RotationEpoch + 1);

        return manifest;
    }

    /// <summary>
    /// Names the constraint PostgreSQL actually refused on, or <see langword="null" /> when the
    /// escaping exception never reached the database at all.
    /// <c>RepositoryConstraintAttributionTests</c>' helper, spelled out here because each file in this
    /// folder owns the readers its own assertions need.
    /// </summary>
    private static string? ConstraintNameOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.ConstraintName
            : null;

    /// <summary>
    /// The SQLSTATE PostgreSQL refused with, or <see langword="null" /> when nothing did. Read beside
    /// the constraint name so a narrowing test can say the violation it staged really is the kind the
    /// filter has to tell apart.
    /// </summary>
    private static string? SqlStateOf(Exception? exception) =>
        exception is DbUpdateException { InnerException: PostgresException postgresException }
            ? postgresException.SqlState
            : null;

    /// <summary>
    /// Removes one <c>credentials</c> row, standing in for the request that won the race.
    /// </summary>
    /// <remarks>
    /// On its own connection, and that is the whole point rather than tidiness: issued through the
    /// repository's own context it would travel inside that context's unit of work, where the DELETE
    /// under test could not fail to see it. Committed by another session is the only version of this
    /// state a real race produces. The dependent <c>passkey_public_keys</c> and
    /// <c>passkey_signature_counters</c> rows leave with it by the database's own cascade, exactly as
    /// they would have under the winner.
    /// </remarks>
    private static async Task<int> DeleteCredentialAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new("delete from credentials where id = @id", admin);
        command.Parameters.AddWithValue("id", credentialId);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Removes one signature counter out of band, which is what makes the tracked copy of it a delete
    /// nothing will match. On its own connection for the reason
    /// <see cref="DeleteCredentialAsync" /> gives.
    /// </summary>
    private static async Task<int> DeleteSignatureCounterAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new(
            "delete from passkey_signature_counters where credential_id = @id",
            admin);
        command.Parameters.AddWithValue("id", credentialId);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="action" /> and hands back whatever escaped, or <see langword="null" />
    /// when nothing did. Deliberately untyped, as in <see cref="UserRepositoryTests" />: the question
    /// this file asks is <i>which</i> exception surfaces, so catching a specific one in the helper
    /// would decide the answer before the assertion reads it — and the failure this test is written
    /// to catch is a <c>DbUpdateConcurrencyException</c> arriving where a
    /// <see cref="NotFoundException" /> was promised.
    /// </summary>
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
    /// Builds a context with no ambient budget, which is safe here because <c>Credential</c> carries
    /// no budget query filter — a credential belongs to a person, not to a tenant.
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
