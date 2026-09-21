using Application.KeyRotations.CompleteKeyRotation;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.ReadServices;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace IntegrationTests;

/// <summary>
/// The completion of a content-key rotation, driven against a real PostgreSQL over the least-privilege
/// application role: whether the promotion reaches the database at all, and whether every one of the
/// account's twelve factors and its manifest move <b>together</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS IS THE ONLY PLACE IN THE SUITE WHERE "THESE TWELVE ROWS AND THIS MANIFEST MOVED TOGETHER" IS
/// OBSERVABLE, AND THAT IS THE WHOLE REASON THE FILE EXISTS.</b> The grouping is stated in exactly one
/// place in the product — the signature of <c>IKeyRotationRepository.PromoteAsync</c>, which takes the
/// manifest and the factor rows as one call — and nothing at the unit tier can check that the signature
/// was honoured. <c>CompleteKeyRotationHandlerTests</c> drives an in-memory fake whose identity map is a
/// <em>model</em> of the change tracker rather than the change tracker, so a handler that mutated every
/// entity and handed <c>PromoteAsync</c> an empty list, or an adapter whose factor read forgot
/// <c>AsTracking</c>, satisfies every byte-level assertion up there while writing nothing here. Both
/// cost the account everything: twelve rows still encapsulating the superseded content key, a manifest
/// that may or may not have moved with them, and no exception anywhere.
/// </para>
/// <para>
/// <b>THE READ-BACK IS ON A CONTEXT THAT DID NOT WRITE THE ROWS, AND SUBSTITUTING THE WRITING ONE
/// RETIRES THE TEST.</b> EF answers a second read of a row it is tracking with the instance it already
/// holds, so re-reading through the acting context would report the promoted objects whether or not a
/// single <c>UPDATE</c> was ever emitted — which is precisely the failure this file is here to catch.
/// A fresh context is what makes every assertion below a statement about PostgreSQL.
/// </para>
/// <para>
/// <b>THE ACT RUNS ON <see cref="RepositoryTestHost.AppConnectionString" /> AND NOT ON THE CONTAINER
/// SUPERUSER</b>, <c>ResealChunkTests</c>' rule and for its reason: PostgreSQL skips every privilege
/// check for a superuser, so a promotion driven on the host's own connection would pass whatever the
/// grant matrix says. Unlike that file, the grants this path needs are already in place —
/// <c>GRANT UPDATE (encapsulated_account_keys) ON wrapped_account_keys</c> and
/// <c>GRANT UPDATE (manifest, rotation_epoch) ON factor_manifests</c> — so a <c>42501</c> here would be
/// the promotion reaching for a column outside those lists, which is a real answer rather than a
/// pending grant.
/// </para>
/// <para>
/// <b>Every arrangement and every read-back is made on the container superuser connection; only the act
/// runs on the app role.</b> <c>AppRoleGrantsTests</c>' idiom, and load-bearing for the same reason it
/// is there: a policed connection reports a row it cannot see exactly as it reports one that did not
/// change, so an assertion about bytes made through the app role could pass over a policy failure.
/// </para>
/// <para>
/// <b>Twelve factors under three credentials, which is not a round number chosen for effect.</b> Two
/// passkeys and a card of ten recovery codes: a set of codes is ten separate secrets under a single
/// <see cref="Credential" />, so it is ten <c>wrapped_account_keys</c> rows, and a promotion that moved
/// "the factor" — or that moved the passkeys and left the card behind — is the shape that reddens
/// nothing and costs somebody their way back in. Each row is seeded with a filler of its own and each
/// staged seal with another, in two blocks that do not overlap, so "promoted" and "left alone" cannot
/// read the same for any factor.
/// </para>
/// <para>
/// <b>The ORDER the two sets come back in is not arranged here, and that is stronger than arranging
/// it.</b> <c>CompleteKeyRotationHandlerTests</c> stages its seals in the reverse of its factor order
/// on purpose, because an in-memory fake answers in insertion order and a positional pairing would
/// otherwise be indistinguishable from a keyed one. Here both sets come back from SQL with no
/// <c>ORDER BY</c> at all, so nothing guarantees the two agree — a handler zipping them is wrong here
/// by construction rather than by arrangement.
/// </para>
/// <para>
/// <b>What this file does NOT hold, said once so nothing below is read as covering it.</b> The gate's
/// own correctness — the <c>IS DISTINCT FROM</c> spelling that decides whether a never-stamped row is
/// outstanding — is <c>RotationCompletenessTests</c>'; the account here carries no narrative row at all,
/// so the gate answers complete without a stamp being written, which is what a presence-aware read is
/// supposed to do and is not what is being measured. Where the save sits relative to the transactional
/// delegate is control flow and is held by
/// <c>CompleteKeyRotationHandlerTests.HandleAsync_PromotesInsideTheUnitOfWork</c> alone. And the
/// optimistic concurrency token on <c>rotation_epoch</c> firing on a racing promotion belongs beside
/// the sibling paths in <c>FactorManifestPromotionTests</c>.
/// </para>
/// </remarks>
public sealed class KeyRotationCompletionTests
{
    /// <summary>
    /// A rotation whose every narrative row is stamped promotes all twelve factors and the manifest,
    /// and the rows read that way back on a context that did not write them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The offenders are collected and asserted empty rather than compared one at a time</b>, so a
    /// failure names every factor that did not move instead of the first — the habit
    /// <c>BeginKeyRotationHandlerTests</c> keeps, and the one a truncated string assertion would take
    /// away.
    /// </para>
    /// <para>
    /// <b>Each factor is matched against its OWN staged seal</b>, never against "some new bytes". A
    /// handler that paired the two sets positionally would give every row a well-formed 158-byte value
    /// that only some other factor's private key can open: twelve good rows, no exception, no SQLSTATE,
    /// and an account that opens with none of them.
    /// </para>
    /// <para>
    /// <b>The staging row is asserted to still stand.</b> A completion deletes nothing — the role holds
    /// no <c>DELETE</c> on either rotation table, so a handler reaching for one would answer
    /// <c>42501</c> rather than tidying up, and a premature delete would destroy the only copies of a
    /// generation these rows have just been rewritten under.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Completion_PromotesEveryFactorAndTheManifest()
    {
        // Arrange — an account with its budget and the manifest registration files, at the floor.
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner = await host.SeedOwnerAsync(GoogleSubject, OwnerEmail);

        // Read rather than chosen: SeedOwnerAsync mints the bytes per call, so this is the only
        // statement of what the account holds now, and the staged manifest below has to differ from it.
        await using BudgetoidDbContext seeding = SuperuserDb(host);
        byte[] storedManifest = (await seeding.FactorManifests
                .SingleAsync(manifest => manifest.UserId == owner.UserId))
            .Manifest
            .ToArray();

        // TWO PASSKEYS AND A CARD OF TEN CODES. The recovery-codes credential is added here rather than
        // through a seeder because ten factors hang off one row of `credentials` — SeedPasskeyAsync has
        // no equivalent, and a set is the arrangement this file is about.
        Credential recoveryCodes = Credential.CreateRecoveryCodes(owner.UserId, SeedInstant);
        seeding.Credentials.Add(recoveryCodes);
        await seeding.SaveChangesAsync();

        List<Guid> factorIds = [];
        Dictionary<Guid, byte[]> storedFactorKeys = [];

        foreach (int passkey in Enumerable.Range(0, PasskeyCount))
        {
            Guid credentialId = await host.SeedPasskeyAsync(
                owner.UserId,
                WebAuthnCredentialId((byte)passkey));
            await AddFactorAsync(host, credentialId, factorIds, storedFactorKeys);
        }

        foreach (int _ in Enumerable.Range(0, RecoveryCodeSetSize))
        {
            await AddFactorAsync(host, recoveryCodes.Id, factorIds, storedFactorKeys);
        }

        // The premise: twelve rows, because every sweep below is vacuous if the account turned out to
        // hold one.
        await Assert.That(factorIds.Count).IsEqualTo(PasskeyCount + RecoveryCodeSetSize);

        // STAGED THROUGH THE REAL ADAPTER, so what the act reads back is what a begin would have left —
        // and through the loaded rows, because KeyRotationSeal.For takes the entity and refuses a great
        // deal that a fabricated row would sail past.
        Guid rotationId = Guid.CreateVersion7();
        byte[] stagedManifest = ManifestFixture.Mint().Manifest;
        Dictionary<Guid, byte[]> stagedSealKeys = [];

        await using (BudgetoidDbContext staging = SuperuserDb(host))
        {
            KeyRotationRepository repository = new(staging);
            Credential passkey = await staging.Credentials
                .FirstAsync(credential =>
                    credential.UserId == owner.UserId && credential.Type == CredentialType.Passkey);
            IReadOnlyDictionary<Guid, WrappedAccountKeys> factors =
                await repository.ListFactorsAsync(owner.UserId);
            KeyRotation rotation = KeyRotation.Begin(
                passkey,
                rotationId,
                stagedManifest,
                StagedRotationEpoch,
                SeedInstant);

            List<KeyRotationSeal> seals = [];

            for (int index = 0; index < factorIds.Count; index++)
            {
                byte[] payload = RepositoryTestHost.EncapsulatedAccountKeysPayload(
                    (byte)(StagedFillerBase + index));
                stagedSealKeys[factorIds[index]] = payload;
                seals.Add(KeyRotationSeal.For(rotation, factors[factorIds[index]], payload));
            }

            await repository.StageAsync(rotation, seals);
        }

        // The premise that makes every comparison below mean something: no factor's stored value is any
        // factor's staged value, so "promoted" and "left alone" cannot read the same.
        await Assert.That(storedFactorKeys.Values
                .Any(stored => stagedSealKeys.Values.Any(staged => staged.SequenceEqual(stored))))
            .IsFalse();
        await Assert.That(stagedManifest.SequenceEqual(storedManifest)).IsFalse();

        // Act — on the least-privilege role, through the real ports, inside a real transaction.
        await using BudgetoidDbContext acting = AppDb(host, owner);
        CompleteKeyRotationHandler handler = new(
            new KeyRotationRepository(acting),
            new RotationCompletenessReadService(acting),
            new TestUserContext(owner.UserId),
            new DbContextPersistenceState(acting),
            new DbContextTransactionalExecutor(acting));

        await handler.HandleAsync(new CompleteKeyRotationCommand(rotationId));

        // Assert — THROUGH A CONTEXT THAT DID NOT WRITE THE ROWS. See the class remarks: re-reading
        // through `acting` would answer with the tracked instances and would pass over a handler that
        // emitted no UPDATE at all.
        await using BudgetoidDbContext verify = SuperuserDb(host);
        Dictionary<Guid, byte[]> promoted = await verify.WrappedAccountKeys
            .Where(keys => keys.UserId == owner.UserId)
            .ToDictionaryAsync(
                keys => keys.FactorId,
                keys => keys.EncapsulatedAccountKeys.ToArray());

        // The set first, so a sweep that found nothing to compare cannot read as agreement.
        await Assert.That(promoted.Count).IsEqualTo(factorIds.Count);

        Guid[] notAdopted =
        [
            .. factorIds.Where(factorId =>
                !promoted.TryGetValue(factorId, out byte[]? now)
                || !now.SequenceEqual(stagedSealKeys[factorId])),
        ];
        await Assert.That(notAdopted).IsEmpty();

        // And the manifest moved with them: the staged bytes, at a generation exactly one above the
        // stored one.
        FactorManifest manifest = await verify.FactorManifests
            .SingleAsync(row => row.UserId == owner.UserId);
        await Assert.That(manifest.Manifest.ToArray().SequenceEqual(stagedManifest)).IsTrue();
        await Assert.That(manifest.RotationEpoch).IsEqualTo(PromotedRotationEpoch);
        await Assert.That(manifest.RotationEpoch)
            .IsEqualTo(FactorManifest.MinimumRotationEpoch + 1);

        // The staging row and its seals are still standing — a completion removes neither, and the role
        // holds no DELETE on either table.
        await Assert.That(await verify.KeyRotations.AnyAsync(staged => staged.UserId == owner.UserId))
            .IsTrue();
        await Assert.That(await verify.KeyRotationSeals
                .CountAsync(staged => staged.UserId == owner.UserId))
            .IsEqualTo(factorIds.Count);
    }

    /// <summary>How many passkeys the account holds, and how many factors a card of codes is.</summary>
    /// <remarks>
    /// Two rather than one, because a set-shaped bug that moved "the passkey" would be invisible on an
    /// account holding exactly one of everything.
    /// </remarks>
    private const int PasskeyCount = 2;

    /// <inheritdoc cref="PasskeyCount" />
    private const int RecoveryCodeSetSize = 10;

    /// <summary>The provider subject and address the seeded account's federated credential carries.</summary>
    private const string GoogleSubject = "google-rotation-completion-owner";

    /// <inheritdoc cref="GoogleSubject" />
    private const string OwnerEmail = "rotation-completion@example.com";

    /// <summary>
    /// The two blocks of fillers this file's payloads carry — the twelve rows as they stand, and the
    /// twelve values their seals stage.
    /// </summary>
    /// <remarks>
    /// Neither overlaps the other and neither is
    /// <see cref="RepositoryTestHost.SeededAccountKeysFiller" />, which is what the default seeding
    /// writes: a row that had kept the seeder's own payload is therefore distinguishable from one that
    /// kept this file's, and both from one that adopted a seal.
    /// </remarks>
    private const byte StoredFillerBase = 0x40;

    /// <inheritdoc cref="StoredFillerBase" />
    private const byte StagedFillerBase = 0x10;

    /// <summary>
    /// The generation the staged run carries, and the one the account ends at.
    /// </summary>
    /// <remarks>
    /// <see cref="RepositoryTestHost" /> seeds an account's first manifest at
    /// <see cref="FactorManifest.MinimumRotationEpoch" />, which is where registration files it, so this
    /// account has never rotated and the completion is its first. <see cref="PromotedRotationEpoch" />
    /// is written out as a literal beside the expression that derives it, so "rose by exactly one" is
    /// checked against a number this file states rather than only against arithmetic it performed.
    /// </remarks>
    private const int StagedRotationEpoch = FactorManifest.MinimumRotationEpoch + 1;

    /// <inheritdoc cref="StagedRotationEpoch" />
    private const int PromotedRotationEpoch = 2;

    /// <summary>
    /// Fixed UTC instant for the rows this file writes itself. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Files one more factor against <paramref name="credentialId" />, carrying a payload nothing else
    /// in this file carries.
    /// </summary>
    /// <remarks>
    /// The filler counts up with the factor's position, because the default seeding writes the same two
    /// values on every row — so a read that returned row A's payload for row B would be invisible on a
    /// defaulted account and is visible here. <see cref="RepositoryTestHost.SeedWrappedAccountKeysAsync" />
    /// says so in its own remarks.
    /// </remarks>
    private static async Task AddFactorAsync(
        RepositoryTestHost host,
        Guid credentialId,
        List<Guid> factorIds,
        Dictionary<Guid, byte[]> storedFactorKeys)
    {
        byte[] payload = RepositoryTestHost.EncapsulatedAccountKeysPayload(
            (byte)(StoredFillerBase + factorIds.Count));
        Guid factorId = await host.SeedWrappedAccountKeysAsync(
            credentialId,
            Guid.CreateVersion7(),
            encapsulatedAccountKeys: payload);

        factorIds.Add(factorId);
        storedFactorKeys[factorId] = payload;
    }

    /// <summary>
    /// A WebAuthn handle of the minimum legal width, distinguished by <paramref name="seed" />.
    /// </summary>
    /// <remarks>
    /// Nothing here verifies a signature, so the bytes are arbitrary — but there are
    /// <see cref="PasskeyPublicKey.MinWebAuthnCredentialIdLength" /> of them, because a shorter handle
    /// is refused by the domain factory and the seeding would fail before the act. Two passkeys need
    /// two distinct handles: the column is unique.
    /// </remarks>
    private static byte[] WebAuthnCredentialId(byte seed) =>
    [
        .. Enumerable
            .Range(1, PasskeyPublicKey.MinWebAuthnCredentialIdLength)
            .Select(value => (byte)(value + seed)),
    ];

    /// <summary>
    /// A context on the container superuser, for arranging and for reading back.
    /// </summary>
    /// <remarks>
    /// No ambient budget, which is safe because nothing this file seeds or reads back carries the
    /// <c>BudgetIsolation</c> filter — every table on the completion path is policed on the user
    /// instead. The act's context is the other one.
    /// </remarks>
    private static BudgetoidDbContext SuperuserDb(RepositoryTestHost host) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options);

    /// <summary>
    /// A context on the <b>least-privilege</b> connection, carrying the session settings both isolation
    /// policies read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>SessionContextInterceptor</c> is wired in rather than the settings being written by
    /// hand</b>, <c>ResealChunkTests</c>' rule: it is a connection-opened interceptor, so it is what
    /// survives EF closing and re-opening the connection between operations and what survives a
    /// retrying execution strategy replaying the unit of work.
    /// </para>
    /// <para>
    /// <b>Both settings, because both policies are in play.</b> <c>user_isolation</c> scopes
    /// <c>wrapped_account_keys</c>, <c>key_rotations</c>, <c>key_rotation_seals</c> and
    /// <c>factor_manifests</c> on <c>app.current_user_id</c>; the completeness gate's five
    /// budget-filtered arms read <c>app.current_budget_id</c>. An unset setting reaches a policy as
    /// <c>''::uuid</c> and raises <c>22P02</c> rather than reading the wrong rows.
    /// </para>
    /// </remarks>
    private static BudgetoidDbContext AppDb(RepositoryTestHost host, RepositoryTestHost.SeededOwner owner)
    {
        TestBudgetContext budgetContext = new(owner.BudgetId);
        TestUserContext userContext = new(owner.UserId);

        return new BudgetoidDbContext(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.AppConnectionString)
                .AddInterceptors(new SessionContextInterceptor(budgetContext, userContext))
                .Options,
            budgetContext);
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();

        return host;
    }
}
