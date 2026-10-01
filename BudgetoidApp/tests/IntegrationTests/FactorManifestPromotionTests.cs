using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Application.Passkeys;
using Domain.Security;
using Domain.Users;
using Infrastructure.Persistence.Provisioning;
using Npgsql;
using TestSupport;
using TUnit.Assertions.Enums;

namespace IntegrationTests;

/// <summary>
/// What the three routes that change an account's set of recovery factors leave in
/// <c>factor_manifests</c> — read back, over real HTTP, on all of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file exists because nothing read the row back.</b> Every route here gained a manifest and an
/// epoch, each loads the account's row and promotes it inside the unit of work that files or removes the
/// factor, and every suite that drives them asserted the status, the rows on the factor tables and
/// nothing else. So each of these passed: a repository read that added <c>AsNoTracking()</c> and emitted
/// no UPDATE at all; a promotion performed on an entity and then never saved; a load hoisted above the
/// <c>ChangeTracker.Clear()</c> on the retried route, so the promotion rode on a detached instance. Each
/// leaves a 200 and a correctly registered factor behind, and each leaves the account's one
/// authenticated statement of which factors exist naming the set it had <em>before</em> — discovered by
/// the person on the day they reach for a factor no client could learn about.
/// </para>
/// <para>
/// <b>The cases come in matched triples, and the revocation arm is the one a reader should not assume
/// follows from its siblings.</b> Registration and issuing <em>add</em> a factor, so a stale manifest
/// there names a set missing something; revocation <em>removes</em> one, and the row it leaves behind
/// names an authenticator the person has just taken away — very often one they took away because
/// somebody else has it. It is also the only one of the three whose factor's share of the account keys
/// leaves by the database's own cascade rather than being written by the request, and the only one whose
/// promotion rides a <c>DELETE</c>'s save while a second save — the session sweep's — has already run
/// inside the same transaction. That last fact is why
/// <see cref="Revocation_StoresExactlyTheManifestAndEpochItWasPosted" /> is the single most valuable
/// case in the arm: a manifest loaded in front of either <c>DiscardTrackedEntities</c> is detached, emits
/// no UPDATE, and answers 200 having moved nothing — and the unit-level fake for that route has no change
/// tracker, so nothing outside this file can see it.
/// </para>
/// <para>
/// <b><see cref="Registration_WithAnEpochThatIsNotTheNextGeneration_IsRefusedAndMovesNothing" /> and its
/// twin are the most valuable cases here, and the <c>+2</c> argument is why.</b> A request carrying
/// <c>N + 1</c> cannot tell a server that validates the client's number from one that ignores it and
/// computes <c>N + 1</c> for itself: both store the same value, both answer 200, and the read-back
/// agrees with both. <c>N + 2</c> separates them in one request — the validating server refuses it with
/// a 400 naming the member, and the computing server accepts it, answers 200 and stores <c>N + 1</c>.
/// The difference matters because the epoch is bound into the manifest as associated data: a server
/// that chooses the number is a server that can store a generation the client did not seal over, and
/// the blob then opens under a generation nothing agrees on.
/// </para>
/// <para>
/// <b>What this file deliberately does not cover.</b> The <em>atomicity</em> half — two promotions
/// started from one generation, where the loser meets EF's concurrency token — is not reachable from a
/// route: the two requests would have to interleave inside one save, and staging that over HTTP buys a
/// timing-dependent test for a branch whose entire input is one row's value.
/// <c>PasskeyRepositoryTests</c> and <c>RecoveryCodeRepositoryTests</c> hold it at the layer that
/// translates it. The <em>replay</em> half on the recovery-code route — a transient failure forcing the
/// retried delegate to run again, so the abandoned attempt's UPDATE goes back with its transaction and
/// the surviving attempt promotes once — needs fault injection into the execution strategy that this
/// suite does not have, and nothing here claims to reach it.
/// </para>
/// <para>
/// <b>Every arrangement drives the product's own path.</b> The account is seeded whole, the passkey is
/// registered through both real legs of the ceremony, and the card is issued through the real route —
/// so the generation a case promotes from is one this application wrote rather than a number a seeder
/// chose. The reads afterwards go to the container superuser, because what is being asked is what the
/// <em>row</em> holds and a read through the application would be answered by the same policy the write
/// went through.
/// </para>
/// </remarks>
public sealed class FactorManifestPromotionTests
{
    private const string AccountKeysPath = "/api/me/account-keys";
    private const string RecoveryCodesPath = "/api/me/recovery-codes";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";
    private const string AssertionPath = "/api/passkeys/assertion";

    /// <summary>The account under test.</summary>
    private const string Subject = "google-manifest-promotion";

    /// <summary>How many codes an issued set holds.</summary>
    private const int RequiredCodeCount = 10;

    /// <summary>The exact width of a verifier, decoded.</summary>
    private const int VerifierLength = 32;

    /// <summary>
    /// The error key a refusal on the passkey route carries. Transcribed rather than read off
    /// <c>CompleteRegistrationHandler</c>, which is private to it in any case: this is a wire contract,
    /// and a test taking its expectation from the code under test agrees with whatever that code later
    /// decides the contract is.
    /// </summary>
    private const string RegistrationErrorKey = "Response";

    /// <summary>
    /// The error key a malformed manifest carries on the recovery-code route, which is the member's own
    /// name rather than that route's catch-all. Transcribed, for the reason above.
    /// </summary>
    private const string ManifestErrorKey = "Manifest";

    /// <summary>
    /// The error key an epoch refusal carries on <b>both</b> routes, because it is raised by
    /// <c>FactorManifest.Promote</c> rather than by either handler — the entity keys its refusals on
    /// the property the value lands in, and that keying travels unchanged through both.
    /// </summary>
    private const string RotationEpochErrorKey = "RotationEpoch";

    /// <summary>
    /// The tables a passkey registration is allowed to change, and the whole of the allow-list the
    /// no-side-effects case compares against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>webauthn_challenges</c> is here because the options leg writes the nonce and the finish leg
    /// spends it, which is a row moving for a request that is part of the act. The other five are the
    /// rows the one save writes: the credential, the key a signature verifies against, the counter a
    /// clone gives itself away against, the factor's share of the account keys, and the manifest naming
    /// the new set.
    /// </para>
    /// <para>
    /// <b>Written down rather than derived, and it is meant to be argued over rather than extended.</b>
    /// A table joining this list is a claim that registering a passkey may touch it, and the reason
    /// belongs on the line. The direction that matters is the other one: a row moving on a table that is
    /// <em>not</em> here is the failure this case exists for.
    /// </para>
    /// </remarks>
    private static readonly string[] TablesAPasskeyRegistrationMayTouch =
    [
        "webauthn_challenges",
        "credentials",
        "passkey_public_keys",
        "passkey_signature_counters",
        "wrapped_account_keys",
        "factor_manifests",
    ];

    /// <summary>
    /// The tables a recovery-code issue is allowed to change.
    /// </summary>
    /// <remarks>
    /// <c>sessions</c> and <c>session_tokens</c> are <b>not</b> on it, and their absence is the sharper
    /// half. A regeneration sweeps the sessions the <em>replaced</em> set opened and re-establishes one
    /// over the new card — but the account here holds no previous set, so a first issue sweeps nothing
    /// and establishes nothing, and a row moving on either table would be this route doing something to
    /// a sign-in it was never asked to touch.
    /// </remarks>
    private static readonly string[] TablesARecoveryCodeIssueMayTouch =
    [
        "webauthn_challenges",
        "credentials",
        "recovery_code_hashes",
        "wrapped_account_keys",
        "factor_manifests",
    ];

    /// <summary>
    /// The tables a passkey revocation is allowed to change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written out rather than copied from either list above, because a revocation legitimately
    /// reaches further than both and the extra reach is the part worth arguing over.</b> The four the two
    /// adding routes share are here for the same reasons — <c>webauthn_challenges</c> because the
    /// re-authentication's options leg writes a nonce and the request spends it, <c>credentials</c>
    /// because that row is the act, <c>wrapped_account_keys</c> because the factor's share of the account
    /// keys goes with it, and <c>factor_manifests</c> because the set it belonged to has changed.
    /// </para>
    /// <para>
    /// <b><c>passkey_public_keys</c> and <c>passkey_signature_counters</c> are here for two reasons
    /// each</b>, and only one of them is the cascade: both rows of the revoked passkey leave with its
    /// credential, and the <em>proving</em> passkey's counter is stamped by the gate on the way in. A
    /// revocation cannot be performed without moving a counter that has nothing to do with the credential
    /// being removed.
    /// </para>
    /// <para>
    /// <b><c>sessions</c> and <c>session_tokens</c> are the sharp difference from
    /// <see cref="TablesARecoveryCodeIssueMayTouch" />, where their absence is half the claim.</b> Here
    /// they are present and earned: the revoked passkey opened a session in the arrangement below, the
    /// handler stamps it before the delete, and the row and its token then leave by the cascade from
    /// <c>credentials</c> through <c>sessions</c>. What this list cannot say is that only <em>that</em>
    /// credential's sessions moved — an allow-list is per table —
    /// <c>CredentialRevocationTests.Revocation_LeavesTheAccountsOtherSessionsAlive</c> owns that half and
    /// is the test to read if the question is whose sign-in ended.
    /// </para>
    /// </remarks>
    private static readonly string[] TablesARevocationMayTouch =
    [
        "webauthn_challenges",
        "credentials",
        "passkey_public_keys",
        "passkey_signature_counters",
        "wrapped_account_keys",
        "factor_manifests",
        "sessions",
        "session_tokens",
    ];

    /// <summary>
    /// The tables the no-side-effects cases have to have actually looked at, and found rows in, before
    /// "nothing moved" is worth anything.
    /// </summary>
    /// <remarks>
    /// <b>Every one of them is either narrative-bearing or budget-owned, which is the claim being
    /// made.</b> <c>accounts</c>, <c>payees</c>, <c>categories</c> and <c>category_groups</c> carry the
    /// sealed name and the blind index over it; <c>transactions</c> carries the sealed description;
    /// <c>budgets</c> is the tenant row all five hang off. A digest over an <em>empty</em> table is the
    /// digest of an empty table before and after anything at all, so without this guard the whole case
    /// is satisfied by a budget nobody furnished — the shape in which a test like this dies quietly.
    /// </remarks>
    private static readonly string[] RelationsAFactorChangeMustLeaveAlone =
    [
        "accounts",
        "budgets",
        "categories",
        "category_groups",
        "payees",
        "transactions",
    ];

    /// <summary>
    /// A successful registration stores exactly the manifest bytes and exactly the epoch that were
    /// posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves, because they fail separately and each failure is silent on its own.</b> An epoch
    /// that moved over the old blob leaves the account claiming a generation whose list does not name
    /// the passkey this very request registered. Bytes that landed under the old number leave the next
    /// promotion computing its successor from a generation nobody is at, and the 400 lands on the
    /// blameless request after this one.
    /// </para>
    /// <para>
    /// <b>The bytes are compared against the payload that was posted, ordered, and not merely for
    /// presence.</b> The manifest is the sole authenticated carrier of every factor's public key and
    /// this server can never open it, so "something is stored" is satisfied by a truncation, by a
    /// re-encode, by the previous generation's blob and by another account's row. Comparing the whole
    /// buffer in order is the only assertion that is about <em>these</em> bytes —
    /// <c>ManifestFixture.Mint</c> is random per call and wider than the framing's floor precisely so
    /// that a cut has somewhere to show.
    /// </para>
    /// <para>
    /// <b>What this catches that nothing did.</b> An <c>AsNoTracking()</c> on
    /// <c>PasskeyRepository.FindFactorManifestAsync</c> emits no UPDATE at all and answers 200; a
    /// promotion performed and then not passed to the save does the same. Both leave a correctly
    /// registered passkey and a stale manifest, which is exactly the state no other assertion in the
    /// suite distinguishes from success.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_StoresExactlyTheManifestAndEpochItWasPosted()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        // SIGNED IN OVER A SET OF RECOVERY CODES, NOT OVER A PASSKEY, and that is what makes every
        // count below a fact about the act. The seeding's default opens a full session with a passkey,
        // which files a credentials row, a passkey_public_keys row and a passkey_signature_counters row
        // — so "this account holds no passkey" would be reading the arrangement. A set of recovery
        // codes opens the same full session while writing a single credentials row and touching neither
        // passkey table.
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, signedIn.UserId);

        ManifestFixture posted = ManifestFixture.Mint();

        // Act
        HttpResponseMessage response = await RegisterPasskeyAsync(
            signedIn.Client, device, posted.Text, before.RotationEpoch + 1);

        // Assert — the act succeeded, or the read-back below is about a request that was refused.
        // 201 and not 200: the route answers Created with no Location and no body, because a passkey is
        // a fact about the account rather than a resource this API exposes at an address.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // The premise: the account really did start somewhere, and the bytes really did change. Without
        // this the comparison below is satisfied by a row nobody touched.
        await Assert.That(before.Manifest).IsNotEquivalentTo(posted.Manifest);

        StoredManifest after = await StoredManifestAsync(admin, signedIn.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch + 1);
        await Assert.That(after.Manifest).IsEquivalentTo(posted.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A successful recovery-code issue stores exactly the manifest bytes and exactly the epoch that
    /// were posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The passkey case's claim on the route that runs inside a <b>retried</b> unit of work, and the
    /// failure it adds is one that route alone can have: the manifest is loaded inside the delegate the
    /// execution strategy replays, so a load hoisted above the <c>ChangeTracker.Clear()</c> that opens
    /// each attempt would promote a detached instance, emit an UPDATE guarded by a predicate compared
    /// against itself, and answer 200 over a row that never moved.
    /// </para>
    /// <para>
    /// <b>Ten factors leave and ten arrive here, where the neighbour adds one</b>, so the manifest this
    /// posts describes an almost entirely different set — which is why a stale row on this route is
    /// worse: the generation in force would name ten key pairs this very request deleted, and every one
    /// of them is a way back into the account that no longer exists.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Generation_StoresExactlyTheManifestAndEpochItWasPosted()
    {
        // Arrange — a registered passkey, because the issue has to prove presence with one.
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyOrThrowAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, signedIn.UserId);

        ManifestFixture posted = ManifestFixture.Mint();

        // Act
        HttpResponseMessage response = await IssueSetAsync(
            signedIn.Client, device, signedIn.UserId, posted.Text, before.RotationEpoch + 1);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The premise: the registration above really did promote the row, so this case is promoting
        // from a generation the product wrote rather than from the one the seeding left.
        await Assert.That(before.RotationEpoch).IsEqualTo(FactorManifest.MinimumRotationEpoch + 1);
        await Assert.That(before.Manifest).IsNotEquivalentTo(posted.Manifest);

        StoredManifest after = await StoredManifestAsync(admin, signedIn.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch + 1);
        await Assert.That(after.Manifest).IsEquivalentTo(posted.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// An epoch that is not exactly one greater than the stored generation is refused, and the row is
    /// left where it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>+2</c> is the argument this case exists for, and it is the only one that answers "does the
    /// server validate the client's number or compute its own?"</b> Sending <c>N + 1</c> cannot: a
    /// server that ignores the member and writes <c>stored + 1</c> stores the same value, answers the
    /// same 200, and reads back identically. <c>N + 2</c> parts them in one request — refused here,
    /// accepted and silently rewritten to <c>N + 1</c> there. It is also the argument the concurrency
    /// token cannot cover: the token's predicate is <c>WHERE rotation_epoch = @original</c>, which
    /// <c>N + 17</c> satisfies exactly as <c>N + 1</c> does, so a guard relaxed here is not half a check
    /// but no check at all.
    /// </para>
    /// <para>
    /// <c>0</c> is the generation already in force — a replayed request, or a client that read and
    /// forgot to add — and <c>-1</c> is a generation that has been left behind, which is the shape a
    /// client rebuilding from a cached answer produces. Neither is the discriminator and both pin the
    /// sides of the step: a guard written <c>&gt;=</c> admits the first, one written <c>!=</c> against
    /// the wrong operand admits the second.
    /// </para>
    /// <para>
    /// <b>The refusal is keyed on <c>rotationEpoch</c> and not on this route's catch-all</b>, which is
    /// the fact that makes it actionable: it is raised by <c>FactorManifest.Promote</c> rather than by
    /// the handler, so it names the one member the caller can correct. A refusal keyed on
    /// <c>Response</c> would send a client's error to a control that shows them a ceremony failure.
    /// </para>
    /// <para>
    /// <b>And nothing was written</b> — not the credential, not the wrapped keys, and not the epoch. A
    /// 400 that had already promoted the row in memory and saved something would satisfy the status
    /// assertion perfectly.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(2)]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Registration_WithAnEpochThatIsNotTheNextGeneration_IsRefusedAndMovesNothing(
        int offsetFromStored)
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        // SIGNED IN OVER A SET OF RECOVERY CODES, NOT OVER A PASSKEY, and that is what makes every
        // count below a fact about the act. The seeding's default opens a full session with a passkey,
        // which files a credentials row, a passkey_public_keys row and a passkey_signature_counters row
        // — so "this account holds no passkey" would be reading the arrangement. A set of recovery
        // codes opens the same full session while writing a single credentials row and touching neither
        // passkey table.
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, signedIn.UserId);

        // Act
        HttpResponseMessage response = await RegisterPasskeyAsync(
            signedIn.Client,
            device,
            ManifestFixture.Mint().Text,
            before.RotationEpoch + offsetFromStored);

        // Assert — refused, and refused for the epoch rather than for anything else this request
        // carried. Every other member is genuine, so the member named is the member that was wrong.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorKeysOfAsync(response)).Contains(RotationEpochErrorKey);

        // Nothing moved. Both halves of the row, and the factor tables beside it.
        StoredManifest after = await StoredManifestAsync(admin, signedIn.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch);
        await Assert.That(after.Manifest).IsEquivalentTo(before.Manifest, CollectionOrdering.Matching);
        await Assert.That(await CountAsync(admin, "passkey_public_keys", signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountAsync(admin, "wrapped_account_keys", signedIn.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// The same rule on the recovery-code route: an epoch that is not the next generation is refused,
    /// and the row is left where it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written out rather than shared with the passkey case, because the two refusals travel
    /// different distances.</b> There the epoch is judged before any transaction opens; here it is
    /// judged <em>inside</em> the retried delegate, after the set has been validated and the previous
    /// card would have been deleted. So this case also says that a 400 raised at that depth unwinds
    /// rather than committing what came before it — which on this route means the ten codes the request
    /// carried never became the account's set.
    /// </para>
    /// <para>
    /// <c>+2</c> is the discriminator for the reason the neighbour's remarks give at length, and it is
    /// the argument to keep if anything here is ever trimmed.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(2)]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Generation_WithAnEpochThatIsNotTheNextGeneration_IsRefusedAndMovesNothing(
        int offsetFromStored)
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyOrThrowAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, signedIn.UserId);
        long wrappedKeysBefore = await CountAsync(admin, "wrapped_account_keys", signedIn.UserId);

        // Act
        HttpResponseMessage response = await IssueSetAsync(
            signedIn.Client,
            device,
            signedIn.UserId,
            ManifestFixture.Mint().Text,
            before.RotationEpoch + offsetFromStored);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorKeysOfAsync(response)).Contains(RotationEpochErrorKey);

        // Nothing moved — the generation, the bytes, and the set the request was carrying. The wrapped
        // keys are counted against what the registration above left rather than against zero, because
        // that passkey holds one and a bare "is empty" would be false for a reason the arrangement
        // produced.
        StoredManifest after = await StoredManifestAsync(admin, signedIn.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch);
        await Assert.That(after.Manifest).IsEquivalentTo(before.Manifest, CollectionOrdering.Matching);
        await Assert.That(await CountAsync(admin, "recovery_code_hashes", signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountAsync(admin, "wrapped_account_keys", signedIn.UserId))
            .IsEqualTo(wrappedKeysBefore);
    }

    /// <summary>
    /// A registration carrying no <c>manifest</c> member at all is refused, and writes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Absent rather than null or empty, because absent is what a client that has not been updated
    /// sends.</b> The request record declares the member without <c>required</c>, so an omitted one
    /// binds to <see langword="null" /> and reaches the handler as a request that is well formed in
    /// every other respect — which is precisely the shape that would otherwise register a factor the
    /// account's manifest does not name.
    /// </para>
    /// <para>
    /// <b>"Nothing written" is the half worth having.</b> The status alone is satisfied by a handler
    /// that refused after filing the credential and its share of the account keys, and that state is
    /// invisible afterwards: a passkey the person can sign in with, wrapped keys nothing names, and a
    /// generation that moved for a ceremony nobody completed.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_WithNoManifestMember_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        // SIGNED IN OVER A SET OF RECOVERY CODES, NOT OVER A PASSKEY, and that is what makes every
        // count below a fact about the act. The seeding's default opens a full session with a passkey,
        // which files a credentials row, a passkey_public_keys row and a passkey_signature_counters row
        // — so "this account holds no passkey" would be reading the arrangement. A set of recovery
        // codes opens the same full session while writing a single credentials row and touching neither
        // passkey table.
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, signedIn.UserId);

        byte[] challenge = await BeginCeremonyAsync(signedIn.Client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge, ApiFactory.PasskeyOrigin, signCount: 0, prfEnabled: true);
        WrappedKeyFixture keys = WrappedKeyFixture.Mint();

        // Act — every member the route takes except the two this case is about.
        HttpResponseMessage response = await signedIn.Client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedPrivateKey = keys.WrappedPrivateKey,
            encapsulatedAccountKeys = keys.EncapsulatedAccountKeys,
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorKeysOfAsync(response)).Contains(RegistrationErrorKey);

        // Nothing written: no credential, no key, no counter, none of the factor's share of the account
        // keys — and the generation exactly where it was.
        await Assert.That(await CountAsync(admin, "passkey_public_keys", signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountAsync(admin, "passkey_signature_counters", signedIn.UserId))
            .IsEqualTo(0L);
        await Assert.That(await CountAsync(admin, "wrapped_account_keys", signedIn.UserId)).IsEqualTo(0L);

        StoredManifest after = await StoredManifestAsync(admin, signedIn.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch);
        await Assert.That(after.Manifest).IsEquivalentTo(before.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// An issue carrying no <c>manifest</c> member at all is refused, and writes nothing.
    /// </summary>
    /// <remarks>
    /// The registration case's claim, and the loss it prevents is larger: a card issued whose manifest
    /// still describes the previous ten factors leaves the account's one authenticated statement of its
    /// recovery arrangements naming ten key pairs this request deleted. The credential and the ten code
    /// hashes are counted afterwards for the reason that one counts its four rows.
    /// </remarks>
    [Test]
    public async Task Generation_WithNoManifestMember_IsRefusedAndWritesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyOrThrowAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, signedIn.UserId);
        long wrappedKeysBefore = await CountAsync(admin, "wrapped_account_keys", signedIn.UserId);

        byte[] challenge = await BeginCeremonyAsync(signedIn.Client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(signedIn.UserId),
            signCount: 0);

        // Act — a well-formed set and a genuine proof, with the manifest members left off entirely.
        HttpResponseMessage response = await signedIn.Client.PostAsJsonAsync(RecoveryCodesPath, new
        {
            codes = SubmissionsOf(Verifiers()),
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorKeysOfAsync(response)).Contains(ManifestErrorKey);

        // Nothing written, and the generation exactly where it was.
        await Assert.That(await CountAsync(admin, "recovery_code_hashes", signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountAsync(admin, "wrapped_account_keys", signedIn.UserId))
            .IsEqualTo(wrappedKeysBefore);

        StoredManifest after = await StoredManifestAsync(admin, signedIn.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch);
        await Assert.That(after.Manifest).IsEquivalentTo(before.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A manifest that is not a well-formed envelope is refused, on both routes, in each route's own
    /// spelling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three shapes, and each is refused by a different clause of the same decoder.</b> Text outside
    /// the base64url alphabet never decodes; a payload one byte short of
    /// <see cref="CiphertextEnvelope.MinimumLength" /> decodes and carries no room for a nonce and a tag;
    /// and a payload of a legal width whose leading byte is not
    /// <see cref="CiphertextEnvelope.Version" /> is a framing this deployment does not implement. A
    /// decoder that had lost any one of the three would still refuse the other two.
    /// </para>
    /// <para>
    /// <b>The version case is the one worth keeping if anything here is ever trimmed.</b> Three framings
    /// in this product lead with <c>0x01</c> and nothing in the bytes says which, so the leading byte is
    /// the only thing a reader of a stored manifest can check at all — and a decoder that stopped
    /// looking at it would accept an <c>EncapsulatedValueEnvelope</c> as a manifest and store it.
    /// </para>
    /// <para>
    /// <b>The two routes are asserted in the keys they actually use, which are different.</b> The
    /// passkey route keys every refusal of the response on <c>Response</c>; the recovery-code route keys
    /// this one on the member's own name. Asserting one key for both would be a test agreeing with
    /// itself about a contract two clients read differently.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(MalformedManifest.NotBase64Url)]
    [Arguments(MalformedManifest.OneByteShortOfTheFloor)]
    [Arguments(MalformedManifest.WrongVersionByte)]
    public async Task Registration_WithAMalformedManifest_IsRefusedAndMovesNothing(MalformedManifest shape)
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        // SIGNED IN OVER A SET OF RECOVERY CODES, NOT OVER A PASSKEY, and that is what makes every
        // count below a fact about the act. The seeding's default opens a full session with a passkey,
        // which files a credentials row, a passkey_public_keys row and a passkey_signature_counters row
        // — so "this account holds no passkey" would be reading the arrangement. A set of recovery
        // codes opens the same full session while writing a single credentials row and touching neither
        // passkey table.
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, signedIn.UserId);

        // Act — the epoch is the one the route would accept, so the manifest is the only thing wrong.
        HttpResponseMessage response = await RegisterPasskeyAsync(
            signedIn.Client, device, TextOf(shape), before.RotationEpoch + 1);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorKeysOfAsync(response)).Contains(RegistrationErrorKey);

        await Assert.That(await CountAsync(admin, "wrapped_account_keys", signedIn.UserId)).IsEqualTo(0L);

        StoredManifest after = await StoredManifestAsync(admin, signedIn.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch);
        await Assert.That(after.Manifest).IsEquivalentTo(before.Manifest, CollectionOrdering.Matching);
    }

    /// <inheritdoc cref="Registration_WithAMalformedManifest_IsRefusedAndMovesNothing" />
    [Test]
    [Arguments(MalformedManifest.NotBase64Url)]
    [Arguments(MalformedManifest.OneByteShortOfTheFloor)]
    [Arguments(MalformedManifest.WrongVersionByte)]
    public async Task Generation_WithAMalformedManifest_IsRefusedAndMovesNothing(MalformedManifest shape)
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyOrThrowAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, signedIn.UserId);

        // Act
        HttpResponseMessage response = await IssueSetAsync(
            signedIn.Client, device, signedIn.UserId, TextOf(shape), before.RotationEpoch + 1);

        // Assert — this route names the member rather than the response.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorKeysOfAsync(response)).Contains(ManifestErrorKey);

        await Assert.That(await CountAsync(admin, "recovery_code_hashes", signedIn.UserId)).IsEqualTo(0L);

        StoredManifest after = await StoredManifestAsync(admin, signedIn.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch);
        await Assert.That(after.Manifest).IsEquivalentTo(before.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// An account holding no manifest row at all is a fault rather than a refusal, on both routes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>500 is the decided answer and not an accident, which is why it is pinned rather than
    /// avoided.</b> Registration has written a manifest for every account since the table existed, so
    /// there is no account either route can legitimately find nothing for. The two repairs a reader
    /// reaches for are both worse than the throw: filing a first manifest here would let a route
    /// establish the account's factor set under an epoch and a blob nothing upstream agreed to, and
    /// skipping the promotion would register a factor the manifest does not name. It is deliberately
    /// not a <c>ValidationException</c> either — nothing the caller sent is wrong, so there is no member
    /// to key a 400 on and nothing they could correct.
    /// </para>
    /// <para>
    /// <b>Reaching this state takes a seeding flag, and that is the point rather than a workaround.</b>
    /// <c>RepositoryTestHost</c> files the row by default because the product does; the opt-out exists
    /// so this answer is testable at all, and removing it would make the one state the routes refuse
    /// unreachable from the suite.
    /// </para>
    /// <para>
    /// <b>Both routes, because they raise it from different depths.</b> The passkey route throws before
    /// any transaction opens; the recovery-code route throws inside a delegate an execution strategy
    /// may replay — and an <see cref="InvalidOperationException" /> is not transient, so it must escape
    /// rather than be retried until the strategy gives up.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_ForAnAccountHoldingNoManifestRow_IsAFault()
    {
        // Arrange — the one arrangement the seeding flag exists for.
        await using PostgresTestHost host = await StartHostAsync();
        // Over a set of recovery codes for the reason every other Registration_ case gives: the
        // passkey arm of the seeding would file the very rows the assertions below read as zero.
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes, withFactorManifest: false);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // The premise, or this case is measuring an account in the ordinary state.
        await Assert.That(await CountAsync(admin, "factor_manifests", signedIn.UserId)).IsEqualTo(0L);

        // Act — epoch 1, which is what a client reading an account that reports generation 0 would send.
        HttpResponseMessage response = await RegisterPasskeyAsync(
            signedIn.Client, device, ManifestFixture.Mint().Text, FactorManifest.MinimumRotationEpoch);

        // Assert — a fault, and NOT a 400: the caller has nothing to correct, and telling them their
        // request was malformed would send them rebuilding a payload that was right.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);

        // And the route filed nothing on the way to failing — including a first manifest, which is the
        // repair this throw exists instead of.
        await Assert.That(await CountAsync(admin, "factor_manifests", signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountAsync(admin, "passkey_public_keys", signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountAsync(admin, "wrapped_account_keys", signedIn.UserId)).IsEqualTo(0L);
    }

    /// <inheritdoc cref="Registration_ForAnAccountHoldingNoManifestRow_IsAFault" />
    [Test]
    public async Task Generation_ForAnAccountHoldingNoManifestRow_IsAFault()
    {
        // Arrange — the passkey this issue proves presence with is registered through a route that
        // would itself refuse an account with no manifest, so it is seeded out of band instead.
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient signedIn =
            await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await SeedPasskeyForAsync(host, signedIn.UserId, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountAsync(admin, "factor_manifests", signedIn.UserId)).IsEqualTo(0L);

        // Act
        HttpResponseMessage response = await IssueSetAsync(
            signedIn.Client,
            device,
            signedIn.UserId,
            ManifestFixture.Mint().Text,
            FactorManifest.MinimumRotationEpoch);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(await CountAsync(admin, "factor_manifests", signedIn.UserId)).IsEqualTo(0L);
        await Assert.That(await CountAsync(admin, "recovery_code_hashes", signedIn.UserId)).IsEqualTo(0L);
    }

    /// <summary>
    /// A passkey registration changes the factor tables and the manifest, and no other relation in the
    /// database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The claim is that a factor change re-encrypts nothing.</b> Every narrative column in this
    /// product holds an AEAD envelope sealed under the account's <em>content</em> key, and the manifest
    /// carries every factor's public key — so a reader could reasonably expect adding a factor to
    /// re-seal something. It does not, and must not: the content key is unchanged by the factor set
    /// changing, the new factor receives the account keys <em>encapsulated to</em> its public half, and
    /// a route that re-sealed narrative columns would be rewriting rows it has no key to read. That is
    /// what <c>key_rotations</c> exists for, and it is a different act with a different staging step.
    /// </para>
    /// <para>
    /// <b>Written as a whole-schema sweep with a named allow-list rather than as a list of tables to
    /// check.</b> The relations are discovered off <c>pg_class</c> through the coverage verifier's own
    /// walker, so a table added tomorrow is swept tomorrow with nothing here to remember; what is
    /// written down is only the set a factor change may touch, which is the decision a person should
    /// have to make. A table drifting that is not on that list is the failure — and it catches far more
    /// than the narrative columns the case is named for: a session quietly revoked, a budget renamed, a
    /// transaction restamped.
    /// </para>
    /// <para>
    /// <b>The digest is over row content and not over row counts.</b> A re-seal rewrites a column
    /// without adding or removing a row, so counting would be green over exactly the failure this is
    /// about. <c>string_agg(t::text, …)</c> renders every column of every row, ordered by the rendering
    /// itself so nothing depends on physical order.
    /// </para>
    /// <para>
    /// <b>The budget is furnished first, and that is what stops this being vacuous.</b> An empty
    /// <c>payees</c> digests the same before and after anything at all, so
    /// <see cref="RelationsAFactorChangeMustLeaveAlone" /> is asserted non-empty row by row before the
    /// comparison is allowed to mean anything.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Registration_ChangesNoRelationBesideTheFactorTablesAndTheManifest()
    {
        // Arrange — a furnished budget and one passkey already registered, so the act below is an
        // ordinary second registration rather than an account's first anything.
        await using PostgresTestHost host = await StartHostAsync();
        // SIGNED IN OVER A SET OF RECOVERY CODES, NOT OVER A PASSKEY, and that is what makes every
        // count below a fact about the act. The seeding's default opens a full session with a passkey,
        // which files a credentials row, a passkey_public_keys row and a passkey_signature_counters row
        // — so "this account holds no passkey" would be reading the arrangement. A set of recovery
        // codes opens the same full session while writing a single credentials row and touching neither
        // passkey table.
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await FurnishBudgetAsync(signedIn.Client);
        await RegisterPasskeyOrThrowAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, RelationContents> before = await ContentsOfEveryRelationAsync(admin);

        // Act — a second authenticator, which is the ordinary shape of adding a factor.
        SyntheticAuthenticator second = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        HttpResponseMessage response = await RegisterPasskeyAsync(signedIn.Client, second);

        // Assert — the act happened, or "nothing changed" is true of a request that was refused.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        IReadOnlyDictionary<string, RelationContents> after = await ContentsOfEveryRelationAsync(admin);

        // Non-vacuity first, both halves: the sweep read something, and the relations this case is
        // named for hold rows for it to have compared.
        await Assert.That(before).IsNotEmpty();
        await Assert.That(RelationsWithNoRows(before, RelationsAFactorChangeMustLeaveAlone)).IsEmpty();

        // And the manifest really did move, or the allow-list below is excusing a table nothing wrote
        // to and the whole comparison is about a request that did nothing.
        await Assert.That(before["factor_manifests"].Digest)
            .IsNotEqualTo(after["factor_manifests"].Digest);

        // The claim.
        await Assert.That(DriftOutside(before, after, TablesAPasskeyRegistrationMayTouch)).IsEmpty();
    }

    /// <inheritdoc cref="Registration_ChangesNoRelationBesideTheFactorTablesAndTheManifest" />
    [Test]
    public async Task Generation_ChangesNoRelationBesideTheFactorTablesAndTheManifest()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await FurnishBudgetAsync(signedIn.Client);
        await RegisterPasskeyOrThrowAsync(signedIn.Client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, RelationContents> before = await ContentsOfEveryRelationAsync(admin);

        // Act
        HttpResponseMessage response = await IssueSetAsync(signedIn.Client, device, signedIn.UserId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        IReadOnlyDictionary<string, RelationContents> after = await ContentsOfEveryRelationAsync(admin);

        await Assert.That(before).IsNotEmpty();
        await Assert.That(RelationsWithNoRows(before, RelationsAFactorChangeMustLeaveAlone)).IsEmpty();
        await Assert.That(before["factor_manifests"].Digest)
            .IsNotEqualTo(after["factor_manifests"].Digest);

        // The claim, and on this route the absence of sessions and session_tokens from the allow-list
        // is half of it: a FIRST issue sweeps no session and re-establishes none, so a row moving on
        // either is this route reaching a sign-in it was never asked to touch.
        await Assert.That(DriftOutside(before, after, TablesARecoveryCodeIssueMayTouch)).IsEmpty();
    }

    /// <summary>
    /// A successful revocation stores exactly the manifest bytes and exactly the epoch that were posted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The single most valuable case in this file, and the only thing anywhere that can catch where
    /// the revocation's manifest is loaded.</b> That route runs two saves inside one transaction: the
    /// session sweep's and the delete's, with a <c>DiscardTrackedEntities()</c> between them and another
    /// opening the delegate. Both are <c>ChangeTracker.Clear</c>, so a manifest read in front of either
    /// is <em>detached</em> — <c>Promote</c> mutates an instance nothing will save, no <c>UPDATE</c> is
    /// emitted, and the request answers 200 with the passkey correctly gone and the generation exactly
    /// where it was. No exception, no SQLSTATE, nothing in a log. The unit-level fake for that handler
    /// has no change tracker at all, so a detached promotion reads there exactly as a tracked one does;
    /// every other integration case on the route asserts the status and the rows that left. This reads
    /// the row.
    /// </para>
    /// <para>
    /// <b>Worse here than on either adding route, which is why it is not enough that they are covered.</b>
    /// A stale manifest after a <em>revocation</em> names the authenticator this very request removed, and
    /// the next rotation encapsulates the account's keys to it. A person revokes a passkey because a
    /// device is lost or taken; a promotion that silently did not happen hands the keys straight back to
    /// whoever has it.
    /// </para>
    /// <para>
    /// <b>The bytes are compared whole and in order</b>, for the reason the registration case gives:
    /// "something is stored" is satisfied by a truncation, a re-encode, the previous generation's blob
    /// and another account's row alike. The generation promoted from is one this application wrote —
    /// two registrations ran in the arrangement — rather than the one the seeding left, which the
    /// premise below states.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_StoresExactlyTheManifestAndEpochItWasPosted()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        RevocableAccount account = await ArrangeTwoPasskeysAsync(host);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, account.UserId);

        ManifestFixture posted = ManifestFixture.Mint();

        // Act — proved by the passkey that stays, naming the one that goes.
        HttpResponseMessage response = await RevokePasskeyAsync(
            account, account.RevokedCredentialId, posted.Text, before.RotationEpoch + 1);

        // Assert — the act succeeded, or the read-back below is about a request that was refused.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The premise: the two registrations in the arrangement really did move the row twice, so this
        // case promotes from a generation the product wrote.
        await Assert.That(before.RotationEpoch).IsEqualTo(FactorManifest.MinimumRotationEpoch + 2);
        await Assert.That(before.Manifest).IsNotEquivalentTo(posted.Manifest);

        StoredManifest after = await StoredManifestAsync(admin, account.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch + 1);
        await Assert.That(after.Manifest).IsEquivalentTo(posted.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// The same rule on the revocation route: an epoch that is not the next generation is refused, and
    /// neither the row nor the passkey moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>+2</c> is the discriminator, for the reason
    /// <see cref="Registration_WithAnEpochThatIsNotTheNextGeneration_IsRefusedAndMovesNothing" /> argues
    /// at length: a request carrying <c>N + 1</c> cannot tell a server that validates the client's number
    /// from one that ignores it and computes its own, because both store the same value and answer the
    /// same 200. <c>0</c> and <c>-1</c> pin the two sides of the step.
    /// </para>
    /// <para>
    /// <b>What this arm adds to the two above is where the refusal lands, and it is the latest of the
    /// three.</b> The epoch is judged inside the transactional delegate, <em>after</em> the
    /// re-authentication gate, after the last-passkey floor, and after the session sweep has already
    /// saved. So this case also says that a 400 raised at that depth unwinds rather than committing what
    /// came before it — which here means that a passkey whose sessions were stamped one statement earlier
    /// is still signed in.
    /// </para>
    /// <para>
    /// <b>The passkey is counted whole afterwards, not just the manifest row.</b> A handler that refused
    /// the epoch after issuing the delete would leave the person's authenticator gone and a 400 telling
    /// them nothing happened — and every assertion about the manifest would be perfectly satisfied.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(2)]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Revocation_WithAnEpochThatIsNotTheNextGeneration_IsRefusedAndMovesNothing(
        int offsetFromStored)
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        RevocableAccount account = await ArrangeTwoPasskeysAsync(host);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, account.UserId);
        await AssertPasskeyIsWholeAsync(admin, account.RevokedCredentialId);

        // Act — every other member is genuine, so the member named below is the member that was wrong.
        HttpResponseMessage response = await RevokePasskeyAsync(
            account,
            account.RevokedCredentialId,
            ManifestFixture.Mint().Text,
            before.RotationEpoch + offsetFromStored);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorKeysOfAsync(response)).Contains(RotationEpochErrorKey);

        // Nothing moved: both halves of the row, and the passkey the request named.
        StoredManifest after = await StoredManifestAsync(admin, account.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch);
        await Assert.That(after.Manifest).IsEquivalentTo(before.Manifest, CollectionOrdering.Matching);
        await AssertPasskeyIsWholeAsync(admin, account.RevokedCredentialId);
    }

    /// <summary>
    /// A revocation carrying no <c>manifest</c> member at all is refused, and removes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Absent rather than null or empty, because absent is what a client that has not been updated
    /// sends.</b> The request record declares the member without <c>required</c>, so an omitted one binds
    /// to <see langword="null" /> and reaches the handler as a request that is well formed in every other
    /// respect — a genuine fresh proof over a credential this account really owns. That is precisely the
    /// shape that would otherwise take a factor out of the set while the account's one authenticated
    /// statement of that set went on naming it.
    /// </para>
    /// <para>
    /// <b>"Removes nothing" is the half worth having, and all four rows are counted.</b> The status alone
    /// is satisfied by a handler that refused after issuing the delete, and that state is not
    /// recoverable: the credential, the key a signature verifies against, the counter a clone gives
    /// itself away against and the factor's share of the account keys all leave together by the
    /// database's own cascade, so a route that got as far as the <c>DELETE</c> has taken an
    /// authenticator away for a request it then reported as a 400.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_WithNoManifestMember_IsRefusedAndRemovesNothing()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        RevocableAccount account = await ArrangeTwoPasskeysAsync(host);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, account.UserId);
        await AssertPasskeyIsWholeAsync(admin, account.RevokedCredentialId);

        byte[] challenge = await BeginCeremonyAsync(account.Client, ReauthenticationOptionsPath);
        AssertionResult assertion = account.Proving.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(account.UserId),
            signCount: 0);

        // Act — every member the route takes except the two this case is about.
        HttpResponseMessage response = await account.Client.PostAsJsonAsync(
            RevocationPath(account.RevokedCredentialId),
            new
            {
                credentialId = assertion.CredentialIdBase64Url,
                clientDataJson = assertion.ClientDataJsonBase64Url,
                authenticatorData = assertion.AuthenticatorDataBase64Url,
                signature = assertion.SignatureBase64Url,
                userHandle = assertion.UserHandleBase64Url,
            });

        // Assert — refused, and keyed on the member the caller can correct rather than on a catch-all.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorKeysOfAsync(response)).Contains(ManifestErrorKey);

        // Nothing removed: the credential, its key, its counter and its share of the account keys.
        await AssertPasskeyIsWholeAsync(admin, account.RevokedCredentialId);

        // And the generation exactly where it was.
        StoredManifest after = await StoredManifestAsync(admin, account.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch);
        await Assert.That(after.Manifest).IsEquivalentTo(before.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A manifest that is not a well-formed envelope is refused on the revocation route too, in each of
    /// the three shapes the decoder tells apart.
    /// </summary>
    /// <remarks>
    /// The three shapes and the argument for keeping the version case are
    /// <see cref="Registration_WithAMalformedManifest_IsRefusedAndMovesNothing" />'s. What this arm adds
    /// is the key: this route names the member, as the recovery-code route does and as the passkey
    /// registration route does not — and it is asserted in the spelling this route actually uses rather
    /// than in one borrowed from a sibling, because a client reads the key to decide which control to put
    /// the error on. The passkey is counted whole beside the row for the reason the neighbouring case
    /// gives: a refusal that had already issued the delete satisfies every assertion about the manifest.
    /// </remarks>
    [Test]
    [Arguments(MalformedManifest.NotBase64Url)]
    [Arguments(MalformedManifest.OneByteShortOfTheFloor)]
    [Arguments(MalformedManifest.WrongVersionByte)]
    public async Task Revocation_WithAMalformedManifest_IsRefusedAndMovesNothing(MalformedManifest shape)
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        RevocableAccount account = await ArrangeTwoPasskeysAsync(host);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        StoredManifest before = await StoredManifestAsync(admin, account.UserId);

        // Act — the epoch is the one the route would accept, so the manifest is the only thing wrong.
        HttpResponseMessage response = await RevokePasskeyAsync(
            account, account.RevokedCredentialId, TextOf(shape), before.RotationEpoch + 1);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ErrorKeysOfAsync(response)).Contains(ManifestErrorKey);

        await AssertPasskeyIsWholeAsync(admin, account.RevokedCredentialId);

        StoredManifest after = await StoredManifestAsync(admin, account.UserId);
        await Assert.That(after.RotationEpoch).IsEqualTo(before.RotationEpoch);
        await Assert.That(after.Manifest).IsEquivalentTo(before.Manifest, CollectionOrdering.Matching);
    }

    /// <summary>
    /// A revocation changes the factor tables, the manifest and the revoked credential's sessions — and
    /// no other relation in the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim and the method are
    /// <see cref="Registration_ChangesNoRelationBesideTheFactorTablesAndTheManifest" />'s: a factor
    /// change re-encrypts nothing, asserted as a whole-schema content digest against a written-down
    /// allow-list rather than as a list of tables to check. Read that case's remarks for why the digest
    /// is over row content and why the budget is furnished first.
    /// </para>
    /// <para>
    /// <b>The allow-list is <see cref="TablesARevocationMayTouch" /> and it is longer than either of the
    /// other two</b>, which is the whole reason this case is written out instead of parameterised over a
    /// shared one. Every extra entry on it is argued on that field; the direction that matters here is
    /// still the other one — a row moving on a table that is <em>not</em> there.
    /// </para>
    /// <para>
    /// <b>The revoked passkey signs in before the snapshot, and that is what makes two of those entries
    /// earned rather than precautionary.</b> Without a session on the credential being removed,
    /// <c>sessions</c> and <c>session_tokens</c> would be excusing tables nothing wrote to — an
    /// allow-list entry that can never fire is an entry that quietly stops being a decision.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_ChangesNoRelationBesideTheFactorTablesAndTheManifest()
    {
        // Arrange — a furnished budget, two passkeys, and a real sign-in on the one about to go.
        await using PostgresTestHost host = await StartHostAsync();
        RevocableAccount account = await ArrangeTwoPasskeysAsync(host);
        await FurnishBudgetAsync(account.Client);
        await SignInAsync(host, account.Revoked, account.UserId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, RelationContents> before = await ContentsOfEveryRelationAsync(admin);

        // Act
        HttpResponseMessage response = await RevokePasskeyAsync(account, account.RevokedCredentialId);

        // Assert — the act happened, or "nothing changed" is true of a request that was refused.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        IReadOnlyDictionary<string, RelationContents> after = await ContentsOfEveryRelationAsync(admin);

        // Non-vacuity first, both halves: the sweep read something, and the relations this case is named
        // for hold rows for it to have compared.
        await Assert.That(before).IsNotEmpty();
        await Assert.That(RelationsWithNoRows(before, RelationsAFactorChangeMustLeaveAlone)).IsEmpty();

        // And the two tables the act is about really did move, or the allow-list is excusing tables
        // nothing wrote to and the whole comparison is about a request that did nothing.
        await Assert.That(before["factor_manifests"].Digest)
            .IsNotEqualTo(after["factor_manifests"].Digest);
        await Assert.That(before["sessions"].Digest).IsNotEqualTo(after["sessions"].Digest);

        // The claim.
        await Assert.That(DriftOutside(before, after, TablesARevocationMayTouch)).IsEmpty();
    }

    /// <summary>
    /// The shapes of manifest text this deployment refuses, named so a failing case says which clause
    /// of the decoder stopped looking.
    /// </summary>
    /// <remarks>Public because TUnit builds the parameterised cases from these values.</remarks>
    public enum MalformedManifest
    {
        /// <summary>Text carrying characters the base64url alphabet does not have.</summary>
        NotBase64Url,

        /// <summary>
        /// A payload one byte below <see cref="CiphertextEnvelope.MinimumLength" />, which leaves no
        /// room for the version, the nonce and the tag the framing is made of.
        /// </summary>
        OneByteShortOfTheFloor,

        /// <summary>
        /// A payload of a legal width whose leading byte is not
        /// <see cref="CiphertextEnvelope.Version" /> — a framing this deployment does not implement.
        /// </summary>
        WrongVersionByte,
    }

    /// <summary>The manifest text for one refused shape.</summary>
    /// <remarks>
    /// The two widths are computed off <see cref="CiphertextEnvelope.MinimumLength" /> rather than
    /// written out, because what is being claimed is <em>one byte below the floor</em> and <em>a legal
    /// width</em> — relations to a bound this ring does not own. A literal would stop describing either
    /// the day the framing changed, and would keep passing.
    /// </remarks>
    private static string TextOf(MalformedManifest shape) => shape switch
    {
        // A character outside the alphabet, and one that is not merely the padding or the two
        // substitutions: '+' and '/' would be caught by a decoder that only rejected the standard
        // alphabet's extras, and '=' by one that only rejected padding.
        MalformedManifest.NotBase64Url => new string('!', CiphertextEnvelope.MinimumLength),
        MalformedManifest.OneByteShortOfTheFloor =>
            ManifestFixture.Of(CiphertextEnvelope.MinimumLength - 1).Text,
        MalformedManifest.WrongVersionByte =>
            ManifestFixture.Of(CiphertextEnvelope.MinimumLength, version: 0x02).Text,
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "No text for that shape."),
    };

    /// <summary>What the account's one manifest row holds.</summary>
    /// <param name="Manifest">The bytes, exactly as the column stores them.</param>
    /// <param name="RotationEpoch">The generation the row is at.</param>
    private readonly record struct StoredManifest(byte[] Manifest, int RotationEpoch);

    /// <summary>One relation's row count and a digest over everything in it.</summary>
    /// <param name="Rows">
    /// Carried beside the digest so a non-vacuity guard can say a relation held something to compare,
    /// which a digest cannot: an empty relation has a perfectly good digest.
    /// </param>
    /// <param name="Digest">
    /// An order-independent digest over every column of every row, which is what makes a re-encrypted
    /// column visible — a row count would be identical across exactly the failure this is about.
    /// </param>
    private readonly record struct RelationContents(long Rows, string Digest);

    /// <summary>
    /// Reads the account's manifest row on the container superuser.
    /// </summary>
    /// <remarks>
    /// On the superuser rather than through an application connection, because the question is what the
    /// <em>row</em> holds. Read as raw <c>bytea</c> rather than through the entity's value converter for
    /// the same reason: what a client is handed is what the column holds, so a converter re-encoding on
    /// the way out would be invisible to a comparison made on the far side of it.
    /// </remarks>
    private static async Task<StoredManifest> StoredManifestAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select manifest, rotation_epoch from factor_manifests where user_id = @id",
            admin);
        command.Parameters.AddWithValue("id", userId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("The account holds no factor manifest row.");
        }

        return new StoredManifest((byte[])reader[0], reader.GetInt32(1));
    }

    /// <summary>How many rows of <paramref name="table" /> belong to <paramref name="userId" />.</summary>
    /// <remarks>
    /// The relation name is interpolated because it comes from a constant in this file and never from
    /// anything a caller supplies; the owner is a parameter, as every value in this suite is.
    /// </remarks>
    private static async Task<long> CountAsync(NpgsqlConnection admin, string table, Guid userId)
    {
        await using NpgsqlCommand command = new(
            $"select count(*) from public.\"{table}\" where user_id = @id",
            admin);
        command.Parameters.AddWithValue("id", userId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Every discovered relation's row count and content digest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The relations come from <c>RowLevelSecurityCoverage.DiscoverAsync</c> — the walker the coverage
    /// verifier already uses, which owns the decision about which <c>relkind</c> values hold rows — so a
    /// table added later is swept with nothing here to remember. Views and partitioned-table parents are
    /// skipped because each would report rows that are counted again underneath them.
    /// </para>
    /// <para>
    /// The name is quoted and schema-qualified, because <c>__EFMigrationsHistory</c> is stored exactly
    /// as EF quoted it and an unquoted identifier folds to lower case.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, RelationContents>> ContentsOfEveryRelationAsync(
        NpgsqlConnection admin)
    {
        IReadOnlyList<DiscoveredTable> discovered = await RowLevelSecurityCoverage.DiscoverAsync(admin);
        Dictionary<string, RelationContents> contents = new(StringComparer.Ordinal);

        // Printed rather than asserted on, because the number is not a rule — it moves the day a table
        // is added. What it buys is that a failure in the comparison above says whether the sweep read
        // the schema or read almost nothing, which the drift list alone cannot.
        Console.WriteLine($"Relation sweep: {discovered.Count} discovered.");

        foreach (DiscoveredTable table in discovered)
        {
            if (table.Kind is RelationKind.View or RelationKind.PartitionedTable)
            {
                continue;
            }

            await using NpgsqlCommand command = new(
                $"select count(*)::bigint, "
                + $"coalesce(md5(string_agg(t::text, '|' order by t::text)), '') "
                + $"from public.\"{table.Name}\" t",
                admin);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            contents[table.Name] = new RelationContents(reader.GetInt64(0), reader.GetString(1));
        }

        return contents;
    }

    /// <summary>
    /// One sentence per relation whose contents moved and which was not allowed to, so a failure names
    /// the relation rather than reporting that something drifted.
    /// </summary>
    /// <remarks>
    /// Both directions, because a relation that appeared between the two reads is contents that moved
    /// from nothing and comparing only the before-set would walk straight past it.
    /// </remarks>
    private static IReadOnlyList<string> DriftOutside(
        IReadOnlyDictionary<string, RelationContents> before,
        IReadOnlyDictionary<string, RelationContents> after,
        IReadOnlyCollection<string> allowed) =>
    [
        .. before
            .Where(relation => !allowed.Contains(relation.Key, StringComparer.Ordinal))
            .Where(relation =>
                !after.TryGetValue(relation.Key, out RelationContents now)
                || !string.Equals(now.Digest, relation.Value.Digest, StringComparison.Ordinal))
            .Select(relation => after.TryGetValue(relation.Key, out RelationContents now)
                ? $"{relation.Key}: {relation.Value.Rows} rows -> {now.Rows}, contents changed"
                : $"{relation.Key}: the relation is no longer discovered")
            .Order(StringComparer.Ordinal),
        .. after.Keys
            .Where(name => !allowed.Contains(name, StringComparer.Ordinal) && !before.ContainsKey(name))
            .Select(name => $"{name}: the relation appeared between the two reads")
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The named relations that hold no rows, so a comparison over them cannot be claiming anything.
    /// </summary>
    private static IReadOnlyList<string> RelationsWithNoRows(
        IReadOnlyDictionary<string, RelationContents> contents,
        IReadOnlyCollection<string> required) =>
    [
        .. required
            .Where(name => !contents.TryGetValue(name, out RelationContents relation) || relation.Rows == 0)
            .Select(name => contents.ContainsKey(name)
                ? $"{name}: the sweep read it and found no rows"
                : $"{name}: the sweep never discovered it")
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Runs both authenticated legs of a registration and hands back the finish leg's response.
    /// </summary>
    /// <param name="manifest">
    /// The manifest as the wire spells it. Null mints a fresh well-formed one, which is what every case
    /// not about the manifest wants. A <see cref="string" /> rather than a
    /// <see cref="ManifestFixture" /> so a case can post a spelling no fixture can produce.
    /// </param>
    /// <param name="rotationEpoch">
    /// The generation the request claims. Null reads the account's current one and adds one, which is
    /// the only value the route accepts.
    /// </param>
    private static async Task<HttpResponseMessage> RegisterPasskeyAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        string? manifest = null,
        int? rotationEpoch = null)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);

        // signCount stays at zero, which is what an authenticator backing a synced passkey reports
        // every time: PasskeySignatureCounter.Accept reads a repeated zero as no movement rather than
        // as a clone, so one device can prove presence as many times as a case needs.
        AttestationResult attestation = device.Register(
            challenge, ApiFactory.PasskeyOrigin, signCount: 0, prfEnabled: true);
        WrappedKeyFixture keys = WrappedKeyFixture.Mint();
        int epoch = rotationEpoch ?? await FactorGeneration.NextAsync(client);

        return await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedPrivateKey = keys.WrappedPrivateKey,
            encapsulatedAccountKeys = keys.EncapsulatedAccountKeys,
            manifest = manifest ?? ManifestFixture.Mint().Text,
            rotationEpoch = epoch,
        });
    }

    /// <summary>
    /// The same, failing loudly on any non-success status — for the arrangements, where a silent
    /// refusal would leave the case measuring an account that holds no passkey at all.
    /// </summary>
    private static async Task RegisterPasskeyOrThrowAsync(
        HttpClient client,
        SyntheticAuthenticator device) =>
        (await RegisterPasskeyAsync(client, device)).EnsureSuccessStatusCode();

    /// <summary>
    /// Issues one whole set of recovery codes through the real route and hands back the response.
    /// </summary>
    /// <inheritdoc cref="RegisterPasskeyAsync" path="/param" />
    private static async Task<HttpResponseMessage> IssueSetAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        string? manifest = null,
        int? rotationEpoch = null)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);
        int epoch = rotationEpoch ?? await FactorGeneration.NextAsync(client);

        return await client.PostAsJsonAsync(RecoveryCodesPath, new
        {
            codes = SubmissionsOf(Verifiers()),
            manifest = manifest ?? ManifestFixture.Mint().Text,
            rotationEpoch = epoch,
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
    }

    /// <summary>
    /// An account holding two registered passkeys: one that proves presence and one the revocation cases
    /// name.
    /// </summary>
    /// <param name="Proving">
    /// The authenticator every revocation below signs with. It is never the one removed, so the proof and
    /// the target are two rows — the shape a person uses when a device is lost, and the shape that leaves
    /// the proving passkey's own counter as the only counter a refused request could have moved.
    /// </param>
    /// <param name="Revoked">The authenticator whose <c>credentials</c> row the route names.</param>
    private sealed record RevocableAccount(
        HttpClient Client,
        Guid UserId,
        SyntheticAuthenticator Proving,
        SyntheticAuthenticator Revoked,
        Guid RevokedCredentialId);

    /// <summary>
    /// Seeds an account signed in over a set of recovery codes and registers two passkeys onto it
    /// through the real ceremony.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two, because the floor refuses an account's last passkey</b> — with one registered, every
    /// revocation below would be answered 409 before the manifest decided anything, and each case would
    /// be green over a route that had lost the rule it is named for.
    /// </para>
    /// <para>
    /// <b>Signed in over a set of recovery codes rather than over a passkey</b>, for the reason every
    /// <c>Registration_</c> case above gives: the harness's default opens a full session with a passkey
    /// of its own, which would make "this account holds exactly the two passkeys this helper
    /// registered" false and every row count below a fact about the arrangement.
    /// </para>
    /// <para>
    /// The two registrations move the generation twice, which is why the successful case asserts it
    /// starts at <see cref="FactorManifest.MinimumRotationEpoch" /> plus two — a promotion this
    /// application performed rather than a number the seeding chose.
    /// </para>
    /// </remarks>
    private static async Task<RevocableAccount> ArrangeTwoPasskeysAsync(PostgresTestHost host)
    {
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);

        SyntheticAuthenticator proving = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator revoked = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyOrThrowAsync(signedIn.Client, proving);
        await RegisterPasskeyOrThrowAsync(signedIn.Client, revoked);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        return new RevocableAccount(
            signedIn.Client,
            signedIn.UserId,
            proving,
            revoked,
            await ResolveCredentialIdAsync(admin, revoked));
    }

    /// <summary>
    /// Runs the re-authentication leg and posts one revocation, handing back the response.
    /// </summary>
    /// <inheritdoc cref="RegisterPasskeyAsync" path="/param" />
    /// <remarks>
    /// <c>signCount</c> stays at zero, which is what an authenticator backing a synced passkey reports
    /// every time and what <c>PasskeySignatureCounter.Accept</c> reads as no movement — so the proving
    /// device can answer as many nonces as a case needs without this file keeping a counter ledger.
    /// </remarks>
    private static async Task<HttpResponseMessage> RevokePasskeyAsync(
        RevocableAccount account,
        Guid credentialId,
        string? manifest = null,
        int? rotationEpoch = null)
    {
        byte[] challenge = await BeginCeremonyAsync(account.Client, ReauthenticationOptionsPath);
        AssertionResult assertion = account.Proving.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(account.UserId),
            signCount: 0);
        int epoch = rotationEpoch ?? await FactorGeneration.NextAsync(account.Client);

        return await account.Client.PostAsJsonAsync(RevocationPath(credentialId), new
        {
            manifest = manifest ?? ManifestFixture.Mint().Text,
            rotationEpoch = epoch,
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
    }

    /// <summary>
    /// The route one passkey is revoked at. The segment is a <c>credentials.id</c>, which is a different
    /// id space from the WebAuthn handle the body carries under the same word.
    /// </summary>
    private static string RevocationPath(Guid credentialId) =>
        $"/api/me/credentials/{credentialId}/revocation";

    /// <summary>
    /// Signs in for real — both anonymous assertion legs, which is the only thing that writes a
    /// <c>sessions</c> row and the <c>session_tokens</c> row hanging off it.
    /// </summary>
    /// <remarks>
    /// On a client carrying no token, because a sign-in by definition happens before anyone is signed
    /// in. <c>CredentialRevocationTests</c> carries the same helper, and it is copied rather than shared
    /// for the reason <see cref="FurnishBudgetAsync" /> is: extracting across files is a change to those
    /// files rather than to this one.
    /// </remarks>
    private static async Task SignInAsync(
        PostgresTestHost host,
        SyntheticAuthenticator device,
        Guid userId)
    {
        HttpClient anonymous = host.Factory.CreateClient();
        byte[] challenge = await BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);

        HttpResponseMessage response = await anonymous.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Translates a device's WebAuthn handle into the <c>credentials.id</c> the revocation route names.
    /// The two id spaces meet here and nowhere else: registration answers 201 with no body, so reading
    /// the key material filed under the handle is the only way to learn which row a device produced.
    /// </summary>
    private static async Task<Guid> ResolveCredentialIdAsync(
        NpgsqlConnection admin,
        SyntheticAuthenticator device)
    {
        await using NpgsqlCommand command = new(
            "select credential_id from passkey_public_keys where webauthn_credential_id = @handle",
            admin);
        command.Parameters.AddWithValue("handle", device.CredentialId);

        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"No passkey was registered for that handle, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The four rows a registered passkey owns, all still present.
    /// </summary>
    /// <remarks>
    /// <b>All four, because all four leave together.</b> The credential is the row the route removes; the
    /// public key, the signature counter and the factor's share of the account keys go with it by the
    /// database's own <c>ON DELETE CASCADE</c>. So a refusal that had reached the <c>DELETE</c> takes the
    /// lot, and any one of them counted alone would say as much as all four — while <em>omitting</em> one
    /// would leave a refusal that somehow removed only that row invisible. It is also the arrangement
    /// guard: asserted before each act as well as after, so a zero afterwards is a zero the route
    /// produced rather than one a silently failed registration left.
    /// </remarks>
    private static async Task AssertPasskeyIsWholeAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await Assert.That(await CountByCredentialAsync(admin, "credentials", "id", credentialId))
            .IsEqualTo(1L);
        await Assert.That(
                await CountByCredentialAsync(admin, "passkey_public_keys", "credential_id", credentialId))
            .IsEqualTo(1L);
        await Assert.That(
                await CountByCredentialAsync(
                    admin, "passkey_signature_counters", "credential_id", credentialId))
            .IsEqualTo(1L);
        await Assert.That(
                await CountByCredentialAsync(admin, "wrapped_account_keys", "credential_id", credentialId))
            .IsEqualTo(1L);
    }

    /// <summary>
    /// How many rows of <paramref name="table" /> name <paramref name="credentialId" /> in
    /// <paramref name="column" />.
    /// </summary>
    /// <remarks>
    /// The relation and the column are interpolated because both come from constants in this file and
    /// never from anything a caller supplies; the id is a parameter, as every value in this suite is. The
    /// column varies because <c>credentials</c> carries the id as its primary key and the three tables
    /// hanging off it carry it as <c>credential_id</c>.
    /// </remarks>
    private static async Task<long> CountByCredentialAsync(
        NpgsqlConnection admin,
        string table,
        string column,
        Guid credentialId)
    {
        await using NpgsqlCommand command = new(
            $"select count(*) from public.\"{table}\" where \"{column}\" = @id",
            admin);
        command.Parameters.AddWithValue("id", credentialId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Files a passkey out of band, for the one arrangement that needs one on an account the
    /// registration route would refuse.
    /// </summary>
    /// <remarks>
    /// The public key and the signature counter are the device's own, because this passkey has to
    /// verify a real assertion afterwards — a seeded key with arbitrary bytes would be turned down by
    /// the reauthentication gate and the case would never reach the manifest it is about.
    /// </remarks>
    private static async Task SeedPasskeyForAsync(
        PostgresTestHost host,
        Guid userId,
        SyntheticAuthenticator device) =>
        await RepositoryTestHost.SeedPasskeyOnAsync(
            host.ConnectionString,
            userId,
            device.CredentialId,
            device.CoseKey,
            device.Algorithm);

    /// <summary>
    /// Writes one row into every budget-owned, narrative-bearing table through the product's own
    /// routes, so the sweep above has something to compare.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every name is sealed and indexed through <c>SealedNarrative</c> rather than sent as text, because
    /// these columns are AEAD envelopes and blind indexes: a flat name is a 400 and the furnishing would
    /// never happen. The identifiers are client-minted because each is the associated data its name was
    /// sealed against.
    /// </para>
    /// <para>
    /// Duplicated from <c>ErasureAtomicityTests</c> rather than extracted, which is the local
    /// convention — <c>AccountErasureEndpointTests</c> and <c>ErasureReauthenticationTests</c> each
    /// carry their own copy, and a drive-by extraction across four files is a change to those files
    /// rather than to this one.
    /// </para>
    /// </remarks>
    private static async Task FurnishBudgetAsync(HttpClient client)
    {
        Guid accountId = await CreateAsync(client, "/api/accounts", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Checking"),
            nameKey = SealedNarrative.EncodedIndex("Checking"),
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        Guid categoryGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Essentials"),
            nameKey = SealedNarrative.EncodedIndex("Essentials"),
            description = (string?)null,
        });
        Guid categoryId = await CreateAsync(client, "/api/categories", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Groceries"),
            nameKey = SealedNarrative.EncodedIndex("Groceries"),
            description = (string?)null,
            categoryGroupId,
        });
        Guid payeeId = await CreateAsync(client, "/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Starbucks"),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        await CreateAsync(client, "/api/transactions", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = SealedNarrative.EncodedDescription("Coffee"),
            payeeId,
            categoryId,
        });
    }

    /// <summary>Posts one create and returns the identifier the 201 carries back.</summary>
    private static async Task<Guid> CreateAsync(HttpClient client, string path, object body)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        return json["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// One whole submission per verifier, as the route spells a set: the verifier, a factor of its own,
    /// and the pair of envelopes sealed under that code's key.
    /// </summary>
    /// <remarks>
    /// A set is ten separate secrets under a single <c>credentials</c> row and the client derives a
    /// key-encryption key from each <em>code</em>, so one pair for the whole set would seal the account
    /// under whichever code that pair belonged to and nine of the ten would open nothing. The share is
    /// freshly minted per code because <c>factor_id</c> is <c>PK_wrapped_account_keys</c> and therefore
    /// unique across the whole table rather than per account.
    /// </remarks>
    private static object[] SubmissionsOf(IReadOnlyList<string> verifiers) =>
    [
        .. verifiers.Select(verifier =>
        {
            WrappedKeyFixture keys = WrappedKeyFixture.Mint();

            return new
            {
                verifier,
                factorId = keys.FactorId,
                wrappedPrivateKey = keys.WrappedPrivateKey,
                encapsulatedAccountKeys = keys.EncapsulatedAccountKeys,
            };
        }),
    ];

    /// <summary>
    /// <see cref="RequiredCodeCount" /> distinct verifiers, base64url encoded exactly as a browser
    /// would send them. The code itself never crosses the wire; a verifier is one HKDF branch of it.
    /// </summary>
    private static string[] Verifiers() =>
    [
        .. Enumerable
            .Range(0, RequiredCodeCount)
            .Select(_ => Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength))),
    ];

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        JsonNode options = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// The members a refusal is keyed on, so a case can say <b>which</b> control a client's error is
    /// sent to rather than only that one was.
    /// </summary>
    /// <remarks>
    /// The whole key set rather than one key's sentence: what separates the epoch refusal from every
    /// other refusal these routes make is the name it arrives under, and a containment check over the
    /// set is what says the refusal reached the member the caller can correct.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ErrorKeysOfAsync(HttpResponseMessage response)
    {
        JsonNode body = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        if (body["errors"] is not JsonObject errors)
        {
            throw new InvalidOperationException(
                $"The refusal carries no errors bag. It reads: {body.ToJsonString()}");
        }

        return [.. errors.Select(field => field.Key)];
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every request
    /// here authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();

        return host;
    }
}
