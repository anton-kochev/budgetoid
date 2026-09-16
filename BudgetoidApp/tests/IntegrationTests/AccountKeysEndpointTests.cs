using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Domain.Sessions;
using Domain.Users;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That a signed-in browser can read back the wrapped copies of the account's content key and index key
/// that <b>every factor the account holds</b> stores — one pair per registered passkey, ten per set of
/// recovery codes, across every credential they hang off — and nobody else's, ever.
/// </summary>
/// <remarks>
/// <para>
/// Driven over real HTTP rather than against <c>GetAccountKeysHandler</c>, because half of what is
/// measured here is <em>which account the request arrives as</em>. Nothing in the request names a
/// session, a credential or an account: the owner is read off the context this request's own
/// authentication resolved. A handler tested in isolation would be handed the thing these tests exist to
/// check the pipeline produces, and would be asked for liveness the pipeline owns.
/// </para>
/// <para>
/// <b>No test here names a type belonging to this endpoint</b> — no request record, no response record,
/// no handler, no read service. They address the route over HTTP and read the wire body, so nothing
/// that happens to the endpoint can take one of them out of the build: unmap the route, rename the
/// response record or drop a member from it and every test here still compiles and fails on a status
/// or on a body, which is the difference between a red test that is telling us something and one that
/// is telling us nothing. The one production type the arrangements name is <see cref="CredentialType" />,
/// to say which credential opens a seeded session; it belongs to no part of this endpoint's contract, so
/// naming it cannot make a test agree with the thing it measures.
/// </para>
/// <para>
/// <b>The two reading tests seed a <em>second</em> credential holding factors of its own, and it must
/// arrive.</b> That is the reverse of what this file used to claim, and the reversal is the change.
/// An account really does hold a passkey and a set of recovery codes at once — eleven wrapped rows
/// across two credentials — and <em>which</em> of them the browser can open is decided by an
/// authenticator this server never hears from: re-authentication looks a credential up by account and
/// the assertion options carry no <c>allowCredentials</c>. So a read narrowed to the credential that
/// opened the session refuses a factor that was just presented and just verified, and the second
/// credential is what makes that narrowing visible. With one credential seeded, "this credential's rows"
/// and "this account's rows" are the same set and every narrowing is green.
/// </para>
/// <para>
/// <b><see cref="AccountKeys_ForARecoveryCodesSession_CarryAllElevenFactorsOfTheAccount" /> is the
/// most important test in this file</b>, and it carries two claims that fail independently.
/// <c>wrapped_account_keys</c> is keyed on <c>factor_id</c> and a set of recovery codes files
/// <b>ten</b> rows under one <c>credential_id</c>, so a projection reaching for
/// <c>SingleOrDefault</c> — or a response type of one pair rather than a list — is correct for every
/// passkey in the product and drops nine of every ten recovery-code envelopes; and the eleventh row,
/// under the passkey beside the set, is the one a credential-narrowed read drops. Nothing on the server
/// can see either happen: they surface in a browser months later, on the day somebody who has already
/// lost their authenticator redeems a code, is handed a session, and finds the account still locked.
/// </para>
/// <para>
/// <b>The envelopes are minted per row rather than filled, and that is the second half of the same
/// argument.</b> <c>RepositoryTestHost</c>'s default pair differs by <em>column</em> and is identical on
/// every <em>row</em>, which is enough while an account holds one factor and blind at ten: a projection
/// handing back row A's envelope for row B compares equal to the expectation on every row.
/// <see cref="WrappedKeyFixture" /> already mints what is needed — random bytes per call, with the byte
/// after the version saying which of the two an envelope is — so every arrangement here passes its own
/// pair in and the seeder's fillers are never reached.
/// </para>
/// <para>
/// <b>Nothing here pins the route's authorization metadata, and the omission is deliberate.</b>
/// <see cref="AnonymousSurfaceTests" />, <see cref="AcceptsEndedSessionTests" /> and
/// <see cref="LockedSessionTests" /> each read their marker off the whole route table and compare the
/// set against a written-out list, so this route acquiring <c>AllowAnonymous</c>,
/// <c>AcceptsEndedSession</c> or <c>AllowsLockedSession</c> reddens one of them until somebody argues
/// for it in the same commit. A fourth pin naming this one route would restate a claim that already has
/// an owner and would go stale the day the owner's shape changed.
/// </para>
/// </remarks>
public sealed class AccountKeysEndpointTests
{
    // EVERY ACCOUNT HERE IS SEEDED WITHOUT A MANIFEST, AND THAT IS THIS FILE ASSERTING OWNERSHIP RATHER
    // THAN OPTING OUT OF THE PRODUCT'S SHAPE. RepositoryTestHost files an account's first manifest by
    // default, because that is what registration does. This file is the one that reads the manifest
    // back, so the row it reads has to be the row it wrote: a seeded one collides with SeedManifestAsync
    // on PK_factor_manifests, and — worse, because it is silent — it turns the two cases about an ABSENT
    // manifest into cases about a present one carrying bytes nobody chose.
    //
    // What is lost by the flag is nothing these cases measured. GET /api/me/account-keys is keyed on the
    // account and reads one row or none, so an account with no manifest is a state the read is written
    // to answer rather than one it is spared.
    private const string AccountKeysPath = "/api/me/account-keys";

    /// <summary>
    /// The neighbouring route that returns no key material, used as the control for the one response
    /// header this endpoint states for itself.
    /// </summary>
    private const string CredentialsPath = "/api/me/credentials";

    /// <summary>The account under test in most of the file.</summary>
    private const string Subject = "google-account-keys";

    /// <summary>
    /// The bystander account: the one whose envelopes must not appear in the first account's answer, and
    /// whose mere existence is what makes the owner half of the read measurable at all.
    /// </summary>
    private const string OtherSubject = "google-account-keys-bystander";

    /// <summary>How many factors a set of recovery codes carries — one per code.</summary>
    private const int RequiredCodeCount = 10;

    /// <summary>
    /// How many factors an ordinary account holds: a set of recovery codes and one registered passkey.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="RequiredCodeCount" /> rather than written as <c>11</c>, so the two numbers
    /// cannot drift apart — and named at all because "eleven" is the claim the widening turns on, and a
    /// bare literal beside a bare <c>10</c> reads as an arbitrary fixture size.
    /// </remarks>
    private const int FactorsPerAccount = RequiredCodeCount + 1;

    /// <summary>
    /// A factor identifier whose hex carries letters in every group, so that "the canonical lower-case
    /// hyphenated spelling" is a claim with something to be wrong about. The all-digit UUID a random
    /// mint occasionally produces renders identically in either case, and a test seeded with one would
    /// pass against a response that upper-cased everything.
    /// </summary>
    private static readonly Guid LetteredFactorId = new("c1d2e3f4-5a6b-7c8d-9e0f-a1b2c3d4e5f6");

    /// <summary>The member of the response body the factors arrive on.</summary>
    /// <remarks>
    /// A wire spelling written out here rather than read off the response record, which is deliberate
    /// and is the rule this whole file keeps: no test here names a type belonging to this endpoint, so
    /// renaming the member on the record reddens these tests instead of moving them along with it.
    /// </remarks>
    private const string FactorsMember = "factors";

    /// <summary>The member the account's authenticated list of factor public keys arrives on.</summary>
    private const string ManifestMember = "manifest";

    /// <summary>The member carrying which generation the manifest beside it belongs to.</summary>
    private const string RotationEpochMember = "rotationEpoch";

    /// <summary>
    /// The generation a seeded manifest claims, and the one number in this file chosen against two
    /// wrong implementations rather than one.
    /// </summary>
    /// <remarks>
    /// <b>Neither 0 nor 1, and both exclusions are the point.</b> 0 is the absence of a manifest row —
    /// the state of every account in the product — so a response hard-wiring it is correct everywhere
    /// today and would be green against a seed of 0. 1 is <c>FactorManifest.MinimumRotationEpoch</c>,
    /// the floor a stored row may claim, so an implementation returning a hard-coded floor is green
    /// against a seed of 1. Seven is neither, and it is small enough that a failure message reads.
    /// </remarks>
    private const int SeededRotationEpoch = 7;

    /// <summary>
    /// The manifest bytes a seeded account's row carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Chosen so the standard base64 alphabet and base64url disagree about them</b>, which is what
    /// makes the alphabet claim a claim: the first three bytes render as four <c>+</c> under one
    /// alphabet and four <c>-</c> under the other, the next three render as four <c>/</c> against four
    /// <c>_</c>, and the length is deliberately not a multiple of three so a padded encoder emits
    /// <c>==</c>.
    /// <see cref="AccountKeys_ManifestIsUnpaddedBase64UrlInTheSameAlphabetAsTheEnvelopes" /> asserts
    /// that disagreement about these exact bytes before it reads the body, so the probe is proven to
    /// discriminate rather than assumed to.
    /// </para>
    /// <para>
    /// <b>Thirty-seven bytes, and the width is the second thing chosen rather than a leftover.</b> This
    /// fixture and the one below it were both exactly <b>ten</b> bytes, which made a whole class of
    /// defect unmeasurable: a projection narrowing the column — <c>substring(manifest, 1, 10)</c>, a
    /// <c>varchar(10)</c>-shaped read, a buffer sized to a constant somebody assumed — is the identity
    /// function on a ten-byte value, so every assertion in this file stayed green over a truncating
    /// read. Thirty-seven is past every round number a truncation is likely to be written to (8, 10,
    /// 16, 20, 32) and is not itself round, so a cut at any of them is visible in the decoded bytes;
    /// it is also ≡ 1 (mod 3), which is what keeps the padding half of the alphabet probe alive.
    /// <see cref="AccountKeys_ForAManifestAtTheMaximumWidth_CarryEveryByteOfIt" /> holds the other end
    /// of the same question, at the cap.
    /// </para>
    /// <para>
    /// Not an AEAD envelope and not an encapsulation: a manifest is an authenticated list of public
    /// keys under a framing this repository does not ship, and the column refuses only emptiness and a
    /// width above <c>FactorManifest.MaximumBytes</c>. Bytes of any shape inside that are a legal row,
    /// which is why nothing here builds one to a version-and-width recipe the way the two envelope
    /// payloads are built.
    /// </para>
    /// </remarks>
    private static readonly byte[] SeededManifest =
    [
        // The alphabet probe's own bytes, first and unchanged: four '+', then four '/', then the
        // leftover that forces '=='.
        0xFB, 0xEF, 0xBE, 0xFF, 0xFF, 0xFF, 0x4D, 0x41, 0x4E, 0x7C,

        // The tail that makes a truncation visible. Ascending and distinct, so a cut, a rotation and a
        // repeated block each read differently in the failure message — and every value is at or above
        // 0x80, which is what keeps this array disjoint from the bystander's below.
        0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88,
        0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F, 0x90, 0x91,
        0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A,
    ];

    /// <summary>
    /// A second account's manifest, sharing not one byte with <see cref="SeededManifest" />.
    /// </summary>
    /// <remarks>
    /// Distinguishable at every position rather than merely unequal, so a body carrying a prefix of the
    /// wrong account's manifest is as visible as one carrying the whole of it. The same width as
    /// <see cref="SeededManifest" />, for the reason argued there: at ten bytes each, a truncating read
    /// served both accounts a value that was still whole. Every byte is below 0x26 and every byte of
    /// the other array is above it, so the two share none.
    /// </remarks>
    private static readonly byte[] BystanderManifest =
    [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A,
        0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14,
        0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E,
        0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25,
    ];

    /// <summary>
    /// A passkey session is handed the account's factors, including the one filed under a credential
    /// that did not open it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The positive control for every refusal below and for the empty answer above them. A route that was
    /// never mapped refuses every caller and satisfies both refusals; a route mapped behind a policy
    /// nobody can clear satisfies them while the feature does not exist; a route answering <c>[]</c> to
    /// everybody satisfies the empty-account test perfectly. This is the test that says the door opens
    /// for somebody and that something comes through it.
    /// </para>
    /// <para>
    /// <b>The second credential's factor is asserted to <em>arrive</em>, and that reverses what this test
    /// used to claim.</b> It previously read the same arrangement and required the bystander's factor to
    /// be absent from the body, on the theory that only the credential which opened the session can
    /// derive a key-encryption key the browser is holding. That theory is false in this product:
    /// <c>PasskeyReauthentication</c> looks a credential up by account and the assertion options carry no
    /// <c>allowCredentials</c>, so the authenticator picks which of the account's credentials answers,
    /// and the session's credential is not it in the one flow that matters —
    /// generating a new set of codes from a session a code opened. The old expectation therefore pinned
    /// the defect. <c>GetAccountKeysHandler</c> carries the argument.
    /// </para>
    /// <para>
    /// The smallest shape of the widening: two credentials, one factor each. The eleven-row shape is
    /// <see cref="AccountKeys_ForARecoveryCodesSession_CarryAllElevenFactorsOfTheAccount" />'s.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForAPasskeySession_CarryEveryFactorTheAccountHolds()
    {
        // Arrange — a session opened by a passkey, one factor under that passkey, and a second
        // credential on the same account carrying a factor that must arrive with it.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid sessionCredentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);

        WrappedKeyFixture[] own = await SeedFactorsAsync(host, sessionCredentialId, count: 1);
        WrappedKeyFixture[] bystander = await SeedBystanderCredentialAsync(host, signedIn.UserId, count: 1);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — the media type only, never the whole Content-Type header. The charset the framework
        // appends is a framework detail this feature makes no claim about.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");

        // One comparison rather than a count and a lookup: rendered whole, sorted and joined, it says
        // "exactly these factors carrying exactly these envelopes" — so a dropped row, a duplicated row,
        // a stranger's row and a swapped envelope each arrive named in the failure message. Both
        // credentials' factors are on the expected side, which is the whole retarget.
        await Assert.That(ArrivedRows(await ReadFactorsAsync(response)))
            .IsEqualTo(ExpectedRows([.. own, .. bystander]));
    }

    /// <summary>
    /// An account holding a passkey beside a set of recovery codes, asked on a session one of the codes
    /// opened, is handed all <b>eleven</b> factors, each carrying its own pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape every real account is in, and the flow the narrowing broke.</b> Somebody redeems a
    /// recovery code, so the session opens over the set; they ask for a new set, which is gated on a
    /// fresh <em>passkey</em> assertion, so the key-encryption key the browser now holds is the
    /// passkey's. A read narrowed to the session's credential hands back the ten code envelopes and not
    /// the one that can be opened — every unwrap fails and the client tells a person who has just
    /// presented a valid factor to present another. This test is the eleventh row.
    /// </para>
    /// <para>
    /// <b>The count is the smaller half of what this measures.</b> Ten distinct factor identifiers refuse
    /// a <c>SingleOrDefault</c>, a <c>FirstOrDefault</c> and a nullable single-pair return type; eleven
    /// refuse a read keyed on a credential; and eleven distinct <em>pairs of envelopes</em>, matched per
    /// row, refuse a projection that answers the right number of rows with one row's bytes repeated, or
    /// with the pairs rotated against the identifiers. The last of those is invisible to any count and to
    /// any assertion over identifiers alone, and it is the one that puts a browser in front of an
    /// envelope whose associated data it cannot rebuild.
    /// </para>
    /// <para>
    /// <b>It previously required the passkey's factor to be absent</b>, which was the narrowing written
    /// down as an expectation. The assertion moved to the other side of the comparison rather than being
    /// deleted, so the file still says something about that row.
    /// </para>
    /// <para>
    /// The eleven identifiers are asserted distinct in the arrangement rather than taken on trust. They
    /// are minted independently, and a seeder that reused one would leave the comparison below reading
    /// ten rows against ten — a green result over an arrangement that never happened.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForARecoveryCodesSession_CarryAllElevenFactorsOfTheAccount()
    {
        // Arrange — the session opens over the set, which is the credential the ten rows hang off, and a
        // passkey beside it carries the eleventh.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setCredentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);

        WrappedKeyFixture[] codeFactors = await SeedFactorsAsync(host, setCredentialId, RequiredCodeCount);
        WrappedKeyFixture[] passkeyFactor = await SeedBystanderCredentialAsync(
            host, signedIn.UserId, count: 1);
        WrappedKeyFixture[] own = [.. codeFactors, .. passkeyFactor];

        // The arrangement really is eleven distinct factors, or the comparison below reads fewer rows
        // than it thinks it does and passes for a reason that has nothing to do with the endpoint.
        await Assert.That(own.Select(factor => factor.FactorId).Distinct(StringComparer.Ordinal).Count())
            .IsEqualTo(FactorsPerAccount);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The count on its own line, so a read that dropped the credential it was not opened on names
        // the number rather than printing eleven rendered rows against ten.
        JsonArray entries = await ReadFactorsAsync(response);
        await Assert.That(entries.Count).IsEqualTo(FactorsPerAccount);
        await Assert.That(ArrivedRows(entries)).IsEqualTo(ExpectedRows(own));
    }

    /// <summary>
    /// Two reads of unchanged rows hand the factors back in <b>the same sequence</b> — whatever that
    /// sequence is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the contract, written down for the first time.</b> <c>GetAccountKeysHandler</c> spends
    /// a paragraph on determinism and the read service orders by the primary key to buy it, and until
    /// this case nothing in the suite read the promise at all: every other comparison in this file
    /// sorts both sides before comparing, which is the correct thing for them to do and is exactly why
    /// they are blind here. A response whose row order varied between two reads of one unchanged
    /// account satisfies all of them.
    /// </para>
    /// <para>
    /// <b>It deliberately does not pin <em>which</em> sequence.</b> <c>uuid</c> collation is
    /// provider-defined, an in-memory implementation and PostgreSQL may disagree about which of two
    /// rows comes first with neither being wrong, and the port promises determinism and nothing about
    /// the particular order. An expectation naming a sorted sequence would therefore be a claim the
    /// product has not made and would redden on a correct change of provider. The endpoint is compared
    /// against itself instead, which is the shape of the promise.
    /// </para>
    /// <para>
    /// <b>What this catches, stated narrowly so nobody credits it with more.</b> It reddens on a read
    /// whose order genuinely varies between requests — rows collected through a set or a dictionary,
    /// a projection assembled per request from an unordered lookup, a plan the server is free to
    /// parallelise differently. It does <em>not</em> redden merely because <c>.OrderBy</c> was deleted:
    /// an unordered scan of an unchanged, unvacuumed table hands the same rows back in the same
    /// physical order twice, and a test cannot make PostgreSQL choose otherwise on demand. Holding that
    /// would take pinning the sequence, which is the thing the paragraph above refuses to do.
    /// </para>
    /// <para>
    /// Ten factors rather than two, because a sequence of two agrees with its own reversal half the
    /// time by luck and a run that flakes one time in two teaches people to re-run rather than to look.
    /// The identifiers are minted with <see cref="Guid.CreateVersion7" /> by the seeder, so seed order
    /// and ascending order are the same sequence here — which is a further reason this case compares
    /// two responses rather than a response against an expectation somebody typed out.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ReadTwice_CarryTheFactorsInTheSameSequence()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setCredentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);
        await SeedFactorsAsync(host, setCredentialId, RequiredCodeCount);

        // Act — the same client, the same route, nothing written in between.
        JsonArray firstRead = await ReadFactorsAsync(await signedIn.Client.GetAsync(AccountKeysPath));
        JsonArray secondRead = await ReadFactorsAsync(await signedIn.Client.GetAsync(AccountKeysPath));

        // Assert — both reads really carried the rows, or the comparison below is two empty strings
        // agreeing with each other.
        await Assert.That(firstRead.Count).IsEqualTo(RequiredCodeCount);
        await Assert.That(secondRead.Count).IsEqualTo(RequiredCodeCount);

        // In arrival order, unsorted on both sides — which is the whole difference between this case
        // and every other comparison in this file.
        await Assert.That(ArrivalSequence(secondRead)).IsEqualTo(ArrivalSequence(firstRead));
    }

    /// <summary>
    /// That <c>wrappedPrivateKey</c> carries the wrapped private key and <c>encapsulatedAccountKeys</c>
    /// the encapsulated pair, on every row — and that each member carries its own suite's width.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The transposition this test is about compiles, is invisible to a fake-backed handler test, and
    /// used to be invisible here too.</b> Swapping the two members in
    /// <c>AccountKeyReadService</c>'s projection is a well-typed edit — both are
    /// <c>ReadOnlyMemory&lt;byte&gt;</c> — and a handler test over a fake reads back whatever the fake
    /// was handed, so it cannot see the swap at all. Nothing on the server has a symptom: what separates
    /// the two values is the cryptography, which lives in the browser. The failure surfaces months
    /// later, in somebody's browser, as an account whose keys will not open — and by then the swap is in
    /// every response the endpoint has ever served.
    /// </para>
    /// <para>
    /// <b>The widths differing is what makes it cheaply checkable, and that is why the width assertion
    /// is here rather than folded away as redundant.</b> A wrapped private key is 167 bytes of AEAD
    /// framing over a PKCS#8 P-256 private key; an encapsulated pair of account keys is 158 bytes of
    /// encapsulation framing over a 64-byte plaintext. A transposed projection therefore puts a 158-byte
    /// value in a member the client will slice by the AEAD layout and a 167-byte value in one it will
    /// slice by the encapsulation layout, and <em>the response says so</em>. Under the arrangement this
    /// replaced both members were 61 bytes carrying the same version byte, and no assertion over lengths
    /// could have caught anything.
    /// </para>
    /// <para>
    /// <b>So do not "simplify" the width lines away on the grounds that the byte comparison below
    /// already covers them.</b> It does today, because the fixture mints distinguishable payloads. The
    /// width assertion is the one that survives a fixture somebody later makes symmetrical, and it is
    /// the one whose failure message says which member is which rather than printing two hex strings.
    /// Both widths are read off <c>WrappedAccountKeys</c>, each from its own suite's constant, so a
    /// cross-read is a change to this file rather than a drift in it.
    /// </para>
    /// <para>
    /// Asserted per row over the ten-factor arrangement rather than over a single pair, because a
    /// projection that swaps the two members swaps them on every row and a projection that mixes rows up
    /// leaves each row's two members correct: separating "the right envelope" from "the right row's
    /// envelope" is what the two tests are for, and neither implies the other.
    /// </para>
    /// <para>
    /// The bytes are compared after decoding, so a difference is a difference in the envelope rather than
    /// in how it was spelled. <see cref="AccountKeys_EnvelopesAreUnpaddedBase64Url" /> owns the spelling.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_NeverSwapTheWrappedPrivateKeyAndTheEncapsulatedAccountKeys()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setCredentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);
        WrappedKeyFixture[] own = await SeedFactorsAsync(host, setCredentialId, RequiredCodeCount);

        // The two envelopes of one factor really do differ, or "they were not swapped" is a claim about
        // two values nothing could tell apart.
        await Assert.That(Convert.ToHexString(own[0].PrivateKeyEnvelope))
            .IsNotEqualTo(Convert.ToHexString(own[0].AccountKeysEnvelope));

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as an empty row set nobody would recognise as a 404.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadFactorsAsync(response);
        Dictionary<string, WrappedKeyFixture> seeded =
            own.ToDictionary(factor => factor.FactorId, StringComparer.Ordinal);

        // Every arriving row, against the pair its own factor was sealed with. Read out of the seeded set
        // by factor identifier rather than by position, so this says nothing about ordering — which
        // AccountKeys_ForARecoveryCodesSession_CarryAllElevenFactorsOfTheAccount has already
        // compared as a set, and which the endpoint promises nothing particular about.
        await Assert.That(entries.Count).IsEqualTo(RequiredCodeCount);
        foreach (JsonNode? entry in entries)
        {
            JsonObject row = AsObject(entry);
            WrappedKeyFixture factor = seeded[row["factorId"]!.GetValue<string>()];

            await Assert.That(DecodedHex(row, "wrappedPrivateKey"))
                .IsEqualTo(Convert.ToHexString(factor.PrivateKeyEnvelope));
            await Assert.That(DecodedHex(row, "encapsulatedAccountKeys"))
                .IsEqualTo(Convert.ToHexString(factor.AccountKeysEnvelope));

            // Each member carries ITS OWN suite's width. Hex is two characters per byte, so the
            // comparison is against twice the domain constant — read from each suite's own constant and
            // never from the neighbour's, because the two numbers are the whole of what makes a
            // transposition visible on this response.
            await Assert.That(DecodedHex(row, "wrappedPrivateKey").Length)
                .IsEqualTo(WrappedAccountKeys.WrappedPrivateKeyLength * 2);
            await Assert.That(DecodedHex(row, "encapsulatedAccountKeys").Length)
                .IsEqualTo(WrappedAccountKeys.EncapsulatedAccountKeysLength * 2);
        }
    }

    /// <summary>
    /// Two established accounts, each asking for itself — and neither is shown an envelope of the
    /// other's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>wrapped_account_keys</c> is <b>policed</b> by <c>user_isolation</c>, so this is not the same
    /// claim the credential list and the recovery-code count make about their exempt tables: a read that
    /// dropped the owner predicate here would still be scoped, by PostgreSQL, underneath the statement.
    /// What this holds is the layer above that — the read service names the owner explicitly, so the
    /// scoping exists twice, and the second copy is the one that survives a policy missed on a table
    /// added later. It also catches the failure a policy cannot: a request that resolved the
    /// <em>wrong</em> account into <c>app.current_user_id</c> is scoped perfectly to somebody else.
    /// </para>
    /// <para>
    /// <b>Both directions, and the order of the arrangement is load-bearing rather than incidental.</b>
    /// Account A is established first, so an unscoped read — or one that took <c>First()</c> — hands B
    /// the row that was written first, which is A's. Seed B first and the same broken read answers B
    /// correctly and the test passes for a reason that has nothing to do with the feature. A is asked
    /// too, because a read that had been made to take the <em>last</em> row would satisfy B alone.
    /// </para>
    /// <para>
    /// The two accounts hold different numbers of factors on purpose, so a read returning the whole table
    /// is visible in the length as well as in the identifiers. Each response is checked against its own
    /// rows whole and against the other's as raw text, the shape <c>CredentialListEndpointTests</c> uses:
    /// an envelope carrying a stranger's key material is a leak whether or not the member this test reads
    /// is correct.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForASecondAccount_CarryThatAccountsEnvelopesAndNotTheFirsts()
    {
        // Arrange — A whole first, so an unscoped read hands B the row that was written first.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        ApiFactory.SignedInClient first = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes, withFactorManifest: false);
        WrappedKeyFixture[] firstFactors = await SeedFactorsAsync(
            host, await SessionCredentialIdAsync(admin, first.UserId), RequiredCodeCount);

        ApiFactory.SignedInClient second = await host.Factory.CreateSignedInClientAsync(OtherSubject, withFactorManifest: false);
        WrappedKeyFixture[] secondFactors = await SeedFactorsAsync(
            host, await SessionCredentialIdAsync(admin, second.UserId), count: 1);

        // Act — B first, since it is the caller the ordering above was arranged to trap.
        HttpResponseMessage secondResponse = await second.Client.GetAsync(AccountKeysPath);
        HttpResponseMessage firstResponse = await first.Client.GetAsync(AccountKeysPath);

        // Assert
        await AssertCarriedExactlyAsync(secondResponse, secondFactors, firstFactors);
        await AssertCarriedExactlyAsync(firstResponse, firstFactors, secondFactors);
    }

    /// <summary>
    /// That <c>factorId</c> arrives in the canonical lower-case hyphenated spelling, read off the
    /// response as text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted against the raw body rather than against a deserialised <see cref="Guid" />, which
    /// would fold the very difference under test.</b> <c>Guid.Parse</c> accepts upper-case hex, the
    /// braced and parenthesised forms and the undashed one, and renders all of them identically — so a
    /// test that parsed the member would call every one of those spellings correct and could never fail.
    /// </para>
    /// <para>
    /// <b>The spelling is the contract because the identifier is associated data.</b> Both of a factor's
    /// envelopes were sealed against these exact bytes, and a client that rebuilds the associated data
    /// from a different rendering of the same UUID opens neither of them — permanently, for that factor,
    /// with no error naming the cause. The client folds the spellings it is handed and the server
    /// normalises nothing, which is what makes "the two sides agree on the bytes" true rather than
    /// hopeful; this is the read-back end of that agreement.
    /// </para>
    /// <para>
    /// All three wrong spellings are named rather than only the upper-case one, because a serializer
    /// configured with a different converter, or a projection that rendered the value itself, reaches for
    /// <c>"N"</c> and <c>"B"</c> as readily as for a case change.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_FactorIdIsTheCanonicalLowerCaseHyphenatedSpelling()
    {
        // Arrange — a factor identifier with hex letters in every group, so a case fold has something to
        // change.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid credentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);
        await SeedFactorAsync(host, credentialId, WrappedKeyFixture.MintFor(LetteredFactorId));

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — the status first, so an empty 404 body cannot pass as a payload that contains no
        // upper-case spelling.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        string canonical = LetteredFactorId.ToString("D", CultureInfo.InvariantCulture);

        await Assert.That(payload).Contains($"\"{canonical}\"");
        await Assert.That(payload).DoesNotContain(canonical.ToUpperInvariant());
        await Assert.That(payload).DoesNotContain(LetteredFactorId.ToString("N", CultureInfo.InvariantCulture));
        await Assert.That(payload).DoesNotContain(LetteredFactorId.ToString("B", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// That both envelopes arrive as unpadded base64url — no <c>=</c>, no <c>+</c> and no <c>/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wire form the whole product agrees on, and the one the client's decoder is <b>stricter</b>
    /// about than the server's: it refuses padding and refuses the standard alphabet outright, so an
    /// endpoint emitting either hands a browser a value it will not decode at all. The symptom is an
    /// account that cannot be opened by a factor whose stored bytes are perfectly correct.
    /// </para>
    /// <para>
    /// Padding is the case worth naming: the two payloads are 167 and 158 bytes, and neither is a
    /// multiple of three, so the standard encoder appends <c>=</c> characters to every one of them —
    /// this is not a corner a fixture had to be chosen to reach. The two alphabet characters are checked over the payload as a whole rather
    /// than per member, because a member is only base64url if nothing anywhere in it says otherwise.
    /// </para>
    /// <para>
    /// The decodability of what arrives is asserted beside the spelling. A member emitted as an empty
    /// string, as a JSON array of numbers, or as hex carries none of the three forbidden characters
    /// either, and would satisfy an absence check on its own.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_EnvelopesAreUnpaddedBase64Url()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid credentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);
        WrappedKeyFixture[] own = await SeedFactorsAsync(host, credentialId, count: 1);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadFactorsAsync(response);
        await Assert.That(entries.Count).IsEqualTo(1);

        JsonObject row = AsObject(entries[0]);

        // Each member against ITS OWN suite's width, because the two payloads are 167 and 158 bytes and
        // no single expectation is right about both. One shared length here would pass on a transposed
        // projection only while both suites agreed, which they no longer do — and which is exactly what
        // AccountKeys_NeverSwapTheWrappedPrivateKeyAndTheEncapsulatedAccountKeys reads as a rule rather
        // than as a coincidence.
        (string Member, int Width)[] members =
        [
            ("wrappedPrivateKey", own[0].PrivateKeyEnvelope.Length),
            ("encapsulatedAccountKeys", own[0].AccountKeysEnvelope.Length),
        ];

        foreach ((string member, int width) in members)
        {
            string encoded = row[member]!.GetValue<string>();

            await Assert.That(encoded).DoesNotContain("=");
            await Assert.That(encoded).DoesNotContain("+");
            await Assert.That(encoded).DoesNotContain("/");

            // And it really is the payload, decoded — which is what stops the three absences above
            // passing over a member that carries no base64url at all.
            await Assert.That(Base64UrlText.Decode(encoded).Length).IsEqualTo(width);
        }
    }

    /// <summary>
    /// That a row carries exactly <c>factorId</c>, <c>wrappedPrivateKey</c> and <c>encapsulatedAccountKeys</c> —
    /// no fourth member, and no third.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a pin rather than a red-green case: once the route exists it is green the day it is
    /// written, and that is the point rather than an apology.</b> Nothing else in this file goes red when
    /// a fourth member starts arriving — every other test reads members by name and keeps passing beside a
    /// <c>credentialId</c>, a <c>userId</c> or a <c>createdAtUtc</c>, and the two-account test only refuses
    /// values belonging to the <em>other</em> account, so a member disclosing the caller's own identifiers
    /// sails through it. The defect this exists to catch is one a later reader adds — a projection widened
    /// because the whole row was to hand. A test that only went red once would have to be written after
    /// the widening had already shipped to a client. Owed, therefore, is a manufactured red: add a fourth
    /// member to the response type and confirm this test names it.
    /// </para>
    /// <para>
    /// <b>Both directions, and neither is the interesting one on its own.</b> A member that went missing
    /// is a browser that cannot rebuild the associated data, or cannot find one of the two envelopes, and
    /// therefore an account nobody can open — a lockout rather than a leak, but no less a regression. A
    /// member that arrived is disclosure. A joined comparison of the whole ordered member list is the one
    /// assertion that says both.
    /// </para>
    /// <para>
    /// <b>Never <c>ContainsKey</c>, and never a count.</b> A containment check over member names can never
    /// fail: every widening leaves the three expected names present and the check green. A count says
    /// "3 != 4" and leaves the next reader hunting for which member arrived; the members are therefore
    /// joined and compared whole, so the failure message prints the offending property by name beside the
    /// three that belong there.
    /// </para>
    /// <para>
    /// <b>Why each refused member is refused, rather than a list somebody has to take on trust.</b>
    /// <c>credentialId</c> is the id a revocation route addresses a credential by, and it is also the join
    /// that <c>account-keys.md</c> deliberately does <em>not</em> give a client — a browser locates its
    /// pair by trying each in turn, so an id here is a capability handed over for no use.
    /// <c>userId</c> is the value every policy in the database is keyed on and the one identifier a
    /// response body may never carry into a client log. <c>createdAtUtc</c> is refused on the flattest
    /// ground of the three: <b>whoever is asking already has that instant.</b> Every one of these rows
    /// carries the creation instant of the credential it hangs off — the three write paths that produce
    /// one (<c>RegisterAccountHandler</c>, <c>CompleteRegistrationHandler</c> and
    /// <c>GenerateRecoveryCodesHandler</c>) each take a single clock reading and stamp the credential and
    /// all of its factors from it in one save — and <c>GET /api/me/credentials</c>, which this very
    /// session reaches under this very fallback policy, answers with that same instant as the
    /// credential's <c>createdAtUtc</c>. So the member would disclose nothing the client is not already
    /// entitled to read, and would buy that nothing by widening a response made of key material.
    /// </para>
    /// <para>
    /// <b>Two reasons that used to be given here are false, written down so that neither is restored.</b>
    /// The first was that the member exposes a timeline of somebody's recovery history. There is no
    /// timeline: a set's ten codes are written from one clock reading in one <c>SaveChanges</c>, so all
    /// ten share a single instant and no sequence of issuings exists for a member to leak. The second was
    /// that this is the argument <c>ProhibitedColumnVocabulary</c> makes against a <c>last_login</c>. It
    /// is not — that vocabulary refuses records of <em>use</em>, which is the category
    /// <c>last_login</c> is classified under, and it lets <c>created_at_utc</c> through, which is why the
    /// table behind this route is allowed to carry one at all.
    /// </para>
    /// <para>
    /// Read over the ten-factor arrangement and reduced to the <em>distinct</em> shapes, so it says "every
    /// row carries exactly these members, however many rows there are" rather than counting rows in the
    /// shape assertion's clothes. It cannot pass on an empty array — nothing joins to the empty string but
    /// nothing — and the row count is asserted beside it anyway, so a widening applied to one branch of a
    /// projection still prints its own member list next to the right one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_RowCarriesTheFactorAndTheTwoEnvelopesAndNothingElse()
    {
        // Arrange — ten rows, so a widening that landed on one of them is still inspected.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setCredentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);
        await SeedFactorsAsync(host, setCredentialId, RequiredCodeCount);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as a member list nobody would recognise as a 404.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadFactorsAsync(response);
        await Assert.That(entries.Count).IsEqualTo(RequiredCodeCount);

        // Each row's members ordered before joining, so a fourth member produces the same message
        // whichever order the serializer emitted it in — a red that reads differently between runs is a
        // red people stop trusting.
        string[] shapes =
        [
            .. entries.Select(entry => string.Join(
                ", ",
                AsObject(entry).Select(member => member.Key).Order(StringComparer.Ordinal))),
        ];

        string distinctShapes = string.Join(
            " | ",
            shapes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        // The expectation is ordered on this side too, so the comparison stays a set comparison however a
        // later rename reshuffles which member sorts first.
        await Assert.That(distinctShapes)
            .IsEqualTo(string.Join(", ", RowMembers.Order(StringComparer.Ordinal)));

        // And none of the three refused names anywhere in the body, which is the half the census above
        // cannot see: a member nested inside another object, or one folded into a container some later
        // widening added, leaves the top-level shape intact.
        string payload = await ReadPayloadAsync(signedIn.Client);
        foreach (string refused in MembersNoRowMayCarry)
        {
            await Assert.That(payload).DoesNotContain(refused);
        }
    }

    /// <summary>
    /// An account holding no factors is answered <c>200</c> with an empty array, and never a 404.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pinned explicitly because a 404 is the plausible wrong answer</b> — there really are no rows to
    /// read, and a handler written as "find the factors, then project them" reaches for one naturally.
    /// It is also the answer a reader will argue for on the grounds that the state should not exist.
    /// </para>
    /// <para>
    /// <b>The reason is the client, and it is no longer the enumeration oracle.</b> That argument rested
    /// on the answer being narrowed by a session id a caller could guess; nothing is narrowed by a
    /// caller-supplied identifier now, and an authenticated request can only ever ask about its own
    /// account. What holds is downstream: <c>AccountKeyCustodyService</c> reads an empty list as
    /// <c>unopened</c> — "present another factor" — and reads any failed read at all, a 404 included, as
    /// <c>unreachable</c>, whose advice is "try the same factor again in a minute". A 404 would hand
    /// somebody whose account genuinely holds nothing openable the one instruction that can never work.
    /// </para>
    /// <para>
    /// <b>Both halves are stated</b>, because "not 404" is not the same claim as "200 with an empty
    /// array": a route answering 204, or 200 with an empty body, satisfies the first and leaves the
    /// browser with nothing to iterate. The status assertion comes first so a parse failure on an empty
    /// body reads as the status it is.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForAnAccountWithNoFactors_IsAnEmptyArrayAndNeverANotFound()
    {
        // Arrange — a signed-in account that has had nothing filed against any of its credentials. The
        // set arm of the seeding writes one credentials row and no wrapped keys, which is exactly this
        // state without anything having to be deleted to reach it.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes, withFactorManifest: false);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — both halves, so the answer this test refuses is named rather than merely excluded.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        await Assert.That((await ReadFactorsAsync(response)).Count).IsEqualTo(0);
    }

    /// <summary>
    /// An account holding a manifest is handed that manifest — base64url decoding to exactly the stored
    /// bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case that tells a served manifest from a hard-wired <see langword="null" /> at a width and
    /// a generation registration cannot reach.</b> Registration now writes a <c>factor_manifests</c> row,
    /// but it writes exactly one shape of one: the client's bytes at
    /// <c>FactorManifest.MinimumRotationEpoch</c>. Every account seeded rather than registered — which is
    /// every account this file arranges — still answers <see langword="null" /> at epoch 0, so a read
    /// service that never looked at the table would satisfy every other case here. This one seeds the row
    /// behind the product's back, on the elevated connection, and holds the read for accounts the
    /// registration route never touched.
    /// </para>
    /// <para>
    /// <b>What it holds is the whole path, not a projection.</b> The bytes cross a lateral in the one
    /// statement the read issues, a <c>ReadOnlyMemory&lt;byte&gt;?</c> on <c>AccountKeyCustody</c>, and
    /// a base64url encoder at the edge; a break anywhere along it — a predicate on the wrong column, a
    /// value truncated by the projection, an encoder swapped for the standard alphabet — arrives here
    /// as a hex mismatch naming the bytes.
    /// </para>
    /// <para>
    /// <b>The comparison is over the decoded bytes, not over the encoded text.</b> Comparing base64url
    /// against base64url would pass on an implementation that read the right row and spelled it with the
    /// wrong alphabet, and fail on one that read the right row and spelled it correctly if the
    /// expectation here were built by a second encoder. Hex on both sides of one decode is the
    /// comparison this file already makes about the two envelopes.
    /// </para>
    /// <para>
    /// A factor is seeded beside the manifest, so the two levels of the body are both populated and
    /// neither claim can be satisfied by the other being empty.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForAnAccountHoldingAManifest_CarryItsExactBytes()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedFactorsAsync(host, await SessionCredentialIdAsync(admin, signedIn.UserId), count: 1);
        await SeedManifestAsync(host, signedIn.UserId, SeededManifest, SeededRotationEpoch);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as an absent manifest.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadBodyAsync(response);

        // Served at all before decoded, so "no manifest came back" and "the wrong bytes came back"
        // arrive as two different messages rather than as one decode blowing up on a null. Indexing a
        // JsonObject answers a C# null both for a member that is absent and for one that is explicitly
        // null, so this single line refuses both — which is exactly what is wanted here and exactly what
        // AccountKeys_AbsentManifest_IsNullAndNeverAnEmptyString has to work around to tell them apart.
        await Assert.That(body[ManifestMember]).IsNotNull();
        await Assert.That(Convert.ToHexString(
                Base64UrlText.Decode(body[ManifestMember]!.GetValue<string>())))
            .IsEqualTo(Convert.ToHexString(SeededManifest));
    }

    /// <summary>
    /// A manifest at the widest width the column will hold arrives whole — every byte of it, in
    /// position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case that makes "the bytes arrive" a claim about a blob rather than about a handful of
    /// bytes.</b> Every other manifest in this file is tens of bytes wide, and a read that narrows the
    /// column — a <c>substring</c>, a buffer sized to a constant, a parameter typed to a width somebody
    /// guessed — is invisible at that size and catastrophic at this one. A manifest is the sole
    /// authenticated carrier of every factor's public key, so a value cut at any line stops naming
    /// whichever factor sat past it: the account is stored as though it still had a way back in, the
    /// row is well-formed, and the loss surfaces on the day somebody reaches for the factor that is
    /// gone. <c>FactorManifest</c> argues at length that this is refused and never truncated on the way
    /// in; this is the read-back end of the same rule.
    /// </para>
    /// <para>
    /// <b>Seeded at <see cref="FactorManifest.MaximumBytes" /> rather than at a literal <c>4096</c>, and
    /// that departs from <c>FactorManifestTests</c> deliberately.</b> That file writes the bound out so
    /// a drift in the constant cannot move both sides of its comparison; here the constant is not the
    /// subject at all — the claim is "the widest row this schema permits survives the read", whatever
    /// that width is, and the seeder validates against the same constant in any case, so a literal
    /// would only offer a second number to fall out of step.
    /// </para>
    /// <para>
    /// <b>The bytes are compared by position and reported as the first disagreement, not as two hex
    /// strings.</b> Eight thousand hex characters on each side of an <c>IsEqualTo</c> produce a failure
    /// message an assertion library truncates and nobody reads; the first differing index and the two
    /// bytes at it say where a cut, a shift or a repeated block began. Measured on a deliberately
    /// broken run of this case, the message reads
    /// <c>byte 2000: expected 0xC1, arrived 0x3E</c> for a flipped byte and
    /// <c>length: expected 4096 bytes, arrived 10</c> for a truncation.
    /// <see cref="FirstDifference" /> carries the rest of that argument.
    /// </para>
    /// <para>
    /// The pattern is not a constant fill. A run of one repeated byte is its own truncation for every
    /// length that survives a length check, so a cut would only be visible in the width — which is
    /// asserted, but is the weaker half. The bytes cycle over a stride coprime with 256, so any
    /// window of the value is distinguishable from any other.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForAManifestAtTheMaximumWidth_CarryEveryByteOfIt()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        byte[] manifest = WidestManifest();
        await SeedManifestAsync(host, signedIn.UserId, manifest, SeededRotationEpoch);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadBodyAsync(response);
        await Assert.That(body[ManifestMember]).IsNotNull();

        byte[] decoded = Base64UrlText.Decode(body[ManifestMember]!.GetValue<string>());

        // The width first, so a truncation reads as the number it is rather than as a byte mismatch
        // several thousand positions in.
        await Assert.That(decoded.Length).IsEqualTo(FactorManifest.MaximumBytes);

        // And every byte, reported as the first position that disagrees.
        await Assert.That(FirstDifference(decoded, manifest)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// That account is handed its stored generation, and never the absent answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Kept beside the case above rather than folded into it, because the two refuse different
    /// implementations.</b> That one refuses a read that never looks at the bytes; this one refuses a
    /// read that answers a <em>number</em> from nowhere. An implementation could plausibly grow one and
    /// not the other — a join that projected the manifest column and left the epoch hard-wired is a
    /// one-line omission with no symptom on this side.
    /// </para>
    /// <para>
    /// <b>The seeded generation is neither 0 nor 1</b>, so both of the two answers a hard-coded
    /// implementation would reach for are caught. <see cref="SeededRotationEpoch" /> carries that
    /// argument in full.
    /// </para>
    /// <para>
    /// No factor is seeded here, and that is deliberate: a manifest is keyed on the account and a
    /// wrapped row on the factor, so the epoch must arrive for an account holding no factors at all.
    /// An implementation deriving the generation from the factor rows — counting them, or reading it
    /// off the first — is red on an empty set rather than green on a populated one.
    /// </para>
    /// <para>
    /// <b>The bytes are asserted here too, and that is not a restatement of the case above.</b> That
    /// one seeds a factor beside the manifest; this one seeds none. Between them they refuse an
    /// implementation that serves the manifest only when the factor list came back non-empty — a read
    /// rooted on <c>wrapped_account_keys</c> rather than on <c>users</c> does exactly that, answers
    /// both levels correctly for every populated account, and drops the manifest of the account this
    /// case describes. Without this line that implementation is green in both places: correct there,
    /// and here judged only on a number it reads off the same lost row's neighbour.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForAnAccountHoldingAManifest_CarryItsStoredRotationEpoch()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);
        await SeedManifestAsync(host, signedIn.UserId, SeededManifest, SeededRotationEpoch);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadBodyAsync(response);
        await Assert.That(body[RotationEpochMember]!.GetValue<int>()).IsEqualTo(SeededRotationEpoch);

        // Served at all before decoded, so "no manifest came back for an account holding no factors"
        // and "the wrong bytes came back" arrive as two messages rather than as one decode blowing up
        // on a null.
        await Assert.That(body[ManifestMember]).IsNotNull();
        await Assert.That(Convert.ToHexString(
                Base64UrlText.Decode(body[ManifestMember]!.GetValue<string>())))
            .IsEqualTo(Convert.ToHexString(SeededManifest));

        // And the empty factor list really is what this account is in, or the paragraph above is
        // describing an arrangement that did not happen.
        await Assert.That(FactorsOf(body).Count).IsEqualTo(0);
    }

    /// <summary>
    /// An account holding no manifest row is answered <c>null</c> at generation 0, with a 200 and never a
    /// 404.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A live state rather than a legacy one, and a decision rather than a gap.</b> Epoch 0 is the
    /// <em>absence</em> of a row — <c>CK_factor_manifests_rotation_epoch</c> refuses anything below 1, so
    /// 0 can never collide with a stored generation. Registration writes a manifest, so an account
    /// created since that landed is not in this state; every account created before it is, nothing
    /// backfills, and an account seeded rather than registered reaches it too. The 404 is the thing to
    /// refuse by name: it is the right
    /// instinct almost everywhere else, and here it would give a client the one instruction that can
    /// never work, because <c>AccountKeyCustodyService</c> reads a failed read as "try the same factor
    /// again in a minute".
    /// </para>
    /// <para>
    /// The factors array is asserted to arrive populated on the same body, so this cannot pass on a
    /// response that failed to carry anything at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForAnAccountWithNoManifest_AreNullAtEpochZeroAndNeverANotFound()
    {
        // Arrange — a signed-in account with a factor and no factor_manifests row, which is the state
        // every account is in without anything having to be deleted to reach it.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedFactorsAsync(host, await SessionCredentialIdAsync(admin, signedIn.UserId), count: 1);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — both halves of the status, so the answer this refuses is named rather than excluded.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The member is declared and carries no value. Indexing answers a C# null for a JSON null, so
        // the presence half is ContainsKey and the value half is the index — two lines because
        // JsonNode spells the two states identically, which is the whole subject of the case below.
        JsonObject body = await ReadBodyAsync(response);
        await Assert.That(body.ContainsKey(ManifestMember)).IsTrue();
        await Assert.That(body[ManifestMember]).IsNull();

        // Present before read, so a renamed member reddens by name instead of dying as a bare
        // NullReferenceException on the index below it.
        await Assert.That(body.ContainsKey(RotationEpochMember)).IsTrue();
        await Assert.That(body[RotationEpochMember]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(FactorsOf(body).Count).IsEqualTo(1);
    }

    /// <summary>
    /// The absent manifest is <c>null</c> on the wire and never <c>""</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The empty string is legal base64url for zero bytes, and that is the whole of the argument.</b>
    /// A client handed <c>""</c> cannot tell "this account has no manifest" from "this account has a
    /// manifest and it names nobody" — two states that call for two different next steps, one of them
    /// being to go and enrol a factor. <c>null</c> is the only spelling on this wire that keeps them
    /// apart, and a serializer setting or a projection that reached for
    /// <c>string.Empty</c> instead would satisfy every other assertion in this file.
    /// </para>
    /// <para>
    /// <b>The raw payload is read beside the parsed node because the parsed node cannot answer this
    /// question on its own.</b> Indexing a <see cref="JsonObject" /> answers a C# <see langword="null" />
    /// for a member that is explicitly <c>null</c> <em>and</em> for one that is not there at all, so a
    /// check written only that way passes on a body carrying no <c>manifest</c> member — which is a
    /// third spelling, and a client reading <c>undefined</c>. <see cref="JsonObject.ContainsKey" />
    /// separates those two, and the substring check is a claim about the bytes rather than about what
    /// a parser made of them, which is what refuses <c>"manifest":""</c>.
    /// </para>
    /// <para>
    /// All three assertions are needed and none of them is a restatement: presence, absence of a value,
    /// and the exact spelling that absence went over the wire in.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_AbsentManifest_IsNullAndNeverAnEmptyString()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);
        string payload = await response.Content.ReadAsStringAsync();

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = JsonNode.Parse(payload) as JsonObject
            ?? throw new InvalidOperationException("The endpoint answered something other than a JSON object.");

        await Assert.That(body.ContainsKey(ManifestMember)).IsTrue();
        await Assert.That(body[ManifestMember]).IsNull();
        await Assert.That(payload).Contains($"\"{ManifestMember}\":null");
        await Assert.That(payload).DoesNotContain($"\"{ManifestMember}\":\"\"");
    }

    /// <summary>
    /// A second account's manifest is never served, in either direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The negative half only, and the omission is deliberate rather than an oversight.</b> That a
    /// caller is handed <em>its own</em> bytes is
    /// <see cref="AccountKeys_ForAnAccountHoldingAManifest_CarryItsExactBytes" />'s claim; restating it
    /// here would add a second failure saying nothing the first does not, and two reds with one cause
    /// is how a suite teaches people to skim its failures. What this adds is the half that case cannot
    /// make with one account in the fixture: that neither body carries the <em>other</em> account's
    /// manifest.
    /// </para>
    /// <para>
    /// <b>Both accounts hold a manifest of their own, which is what makes this a measurement rather
    /// than a pin.</b> Two seeded rows and two served bodies mean the two searches run over payloads
    /// that really do carry a manifest each — so an absence here is the endpoint having scoped the
    /// read, not the feature having nothing to disclose. A version of this case with one seeded row,
    /// or written before anything served a manifest at all, would pass over two bodies both carrying
    /// <c>null</c> and would be green for a reason that has nothing to do with the owner predicate.
    /// <c>factor_manifests</c> is policed by <c>user_isolation</c> on <c>user_id</c>, so a predicate
    /// dropped altogether answers empty rather than wrong on this table; what arrives here is the
    /// failure the policy cannot catch — a read keyed on the <em>wrong</em> account, or one that read
    /// the table through a context the policy never saw.
    /// </para>
    /// <para>
    /// <b>The two epochs differ by one, and both are read.</b> The bytes and the generation are two
    /// columns of the one row, and an implementation can lose them separately: a read that fetched the
    /// right row's bytes beside the wrong row's epoch passes every search over the payload. Off-by-one
    /// neighbours rather than distant numbers, because the failure worth catching is a row taken from
    /// the account next door, not a number invented from nothing.
    /// </para>
    /// <para>
    /// Searched over the raw payload rather than over a parsed member, so a manifest that arrived
    /// nested inside some later container is found too — the same reason
    /// <see cref="AccountKeys_RowCarriesTheFactorAndTheTwoEnvelopesAndNothingElse" /> searches the
    /// payload beside its member census. The two seeded manifests are asserted distinct first, or the
    /// search is over one value wearing two names.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForASecondAccount_NeverCarryTheFirstsManifest()
    {
        // Arrange — A first, so an unscoped read hands B the row that was written first.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient first = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);
        await SeedManifestAsync(host, first.UserId, SeededManifest, SeededRotationEpoch);

        ApiFactory.SignedInClient second = await host.Factory.CreateSignedInClientAsync(OtherSubject, withFactorManifest: false);
        await SeedManifestAsync(host, second.UserId, BystanderManifest, SeededRotationEpoch + 1);

        string firstEncoded = Base64UrlText.Encode(SeededManifest);
        string secondEncoded = Base64UrlText.Encode(BystanderManifest);
        await Assert.That(firstEncoded).IsNotEqualTo(secondEncoded);

        // Act — B first, since it is the caller the ordering above was arranged to trap.
        string secondPayload = await (await second.Client.GetAsync(AccountKeysPath))
            .Content.ReadAsStringAsync();
        string firstPayload = await (await first.Client.GetAsync(AccountKeysPath))
            .Content.ReadAsStringAsync();

        // Assert
        await Assert.That(secondPayload).DoesNotContain(firstEncoded);
        await Assert.That(firstPayload).DoesNotContain(secondEncoded);

        // Each body carries its own manifest, which is what makes the two absences above a claim about
        // scoping rather than about an endpoint with nothing to disclose.
        await Assert.That(secondPayload).Contains(secondEncoded);
        await Assert.That(firstPayload).Contains(firstEncoded);

        // And its own generation. The two epochs differ by one, so a body that fetched the stranger's
        // row and its own bytes — or its own row through somebody else's key — is visible on the
        // number as well as in the search.
        await Assert.That(EpochOf(secondPayload)).IsEqualTo(SeededRotationEpoch + 1);
        await Assert.That(EpochOf(firstPayload)).IsEqualTo(SeededRotationEpoch);
    }

    /// <summary>
    /// The body carries the manifest, the generation and the factors, and no fourth member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sibling of the per-row census, at the level the row census cannot see.</b>
    /// <see cref="AccountKeys_RowCarriesTheFactorAndTheTwoEnvelopesAndNothingElse" /> reads the members
    /// of a <em>factor</em> and is blind to everything above it — the body could grow a
    /// <c>nextCursor</c>, a <c>userId</c> or a second copy of the account's keys and that census would
    /// stay green. Until this case, the only thing holding the top level was that a rename of
    /// <c>factors</c> made six other tests die on a lookup, which names the helper rather than the
    /// member.
    /// </para>
    /// <para>
    /// <b>Rendered as an ordered, joined string rather than compared as a set</b>, so a fourth member
    /// arrives in the failure message by name and the message reads the same however the serializer
    /// ordered the body. The expectation is ordered on this side too, so a later rename cannot make the
    /// comparison depend on which member happens to sort first.
    /// </para>
    /// <para>
    /// The account holds a factor, so this is read off a populated body: a member that only appears
    /// when something came back is still a member.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_BodyCarriesTheManifestTheEpochAndTheFactorsAndNothingElse()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedFactorsAsync(host, await SessionCredentialIdAsync(admin, signedIn.UserId), count: 1);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — the status first, so a problem-details body does not pass for a shape census.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject body = await ReadBodyAsync(response);

        await Assert.That(string.Join(
                ", ",
                body.Select(member => member.Key).Order(StringComparer.Ordinal)))
            .IsEqualTo(string.Join(", ", BodyMembers.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The manifest crosses in the same alphabet the two envelopes do: base64url, unpadded, with no
    /// <c>+</c> and no <c>/</c> anywhere in the body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written beside <see cref="AccountKeys_EnvelopesAreUnpaddedBase64Url" /> rather than as a row
    /// added to its table, and the reason is which red each would produce.</b> That test iterates a
    /// written member list and decodes each member to its own suite's width; a <c>manifest</c> row
    /// added to it would decode a member that is legitimately <c>null</c> for every account holding no
    /// manifest row — which is every account in the product — so the row would have to carry a
    /// condition, and a member list with a condition in it stops being a list. This case asks the
    /// alphabet question of the <b>whole body</b> instead, which needs no such branch: whatever is in
    /// the payload is in the payload.
    /// </para>
    /// <para>
    /// <b>All three subjects are seeded, so none of the three absences is vacuous.</b> A factor is
    /// filed and a manifest is filed, so the two envelopes and the manifest all really cross this body
    /// — and the manifest's presence is asserted on the parsed body before the payload is searched, so
    /// a member that vanished cannot be what makes the three characters absent.
    /// </para>
    /// <para>
    /// <b>The probe proves itself before it is used.</b> The seeded manifest's bytes are asserted to
    /// render with <c>+</c>, <c>/</c> and <c>=</c> under the standard alphabet, so "the body carries
    /// none of the three" is a statement about the encoder rather than about bytes that could never
    /// have produced them. <see cref="SeededManifest" /> carries that argument.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ManifestIsUnpaddedBase64UrlInTheSameAlphabetAsTheEnvelopes()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedFactorsAsync(host, await SessionCredentialIdAsync(admin, signedIn.UserId), count: 1);
        await SeedManifestAsync(host, signedIn.UserId, SeededManifest, SeededRotationEpoch);

        // The probe really does distinguish the two alphabets, or the three absences below are a claim
        // about bytes that could not have spent either character in the first place.
        string standard = Convert.ToBase64String(SeededManifest);
        await Assert.That(standard).Contains("+");
        await Assert.That(standard).Contains("/");
        await Assert.That(standard).Contains("=");

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);
        string payload = await response.Content.ReadAsStringAsync();

        // Assert — the status first, so an error body carrying none of the three cannot pass for a
        // correctly encoded one.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The member carries a VALUE, not merely a name. ContainsKey alone is true of the JSON null
        // every account without a manifest row answers with, so a read that lost the row entirely
        // would satisfy it — and the three absences below are true of a null as well. IsNotNull is
        // what makes them a claim about an encoder rather than about a body with nothing in it.
        await Assert.That((await ReadBodyAsync(response))[ManifestMember]).IsNotNull();

        foreach (string refused in StandardAlphabetOnly)
        {
            await Assert.That(payload).DoesNotContain(refused);
        }
    }

    /// <summary>
    /// The response is <c>Cache-Control: no-store</c>, and the neighbouring route beside it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one endpoint in the product that returns key material, and the only one that states its own
    /// cacheability.</b> <c>SecurityHeadersMiddleware</c> argues at length for owning no global value, so
    /// this header exists in exactly one place — a direct write in the route delegate — and nothing else
    /// in the suite reads it. Until this test, the header was held by an argument in a comment: deleting
    /// the line reddened nothing anywhere, and the bytes would have gone on being served into a shared
    /// cache, a disk cache and a back-button restore with every assertion in this file still green.
    /// </para>
    /// <para>
    /// <b>Compared to the exact string rather than through
    /// <see cref="System.Net.Http.Headers.CacheControlHeaderValue" />.</b> A parsed read would answer
    /// <c>NoStore</c> true for <c>no-store, no-cache, private, max-age=0</c> as readily as for
    /// <c>no-store</c> alone, and the endpoint's own remarks refuse that spelling on the grounds that the
    /// four together read as more careful and store more — <c>no-cache</c> permits storage,
    /// <c>private</c> permits a browser cache, and <c>max-age=0</c> without <c>no-store</c> permits a
    /// stale-serving cache to keep the bytes. An exact comparison is the only one that can say so.
    /// </para>
    /// <para>
    /// <b>The neighbouring route is the control, and without it this test passes on the thing the
    /// middleware refuses to be.</b> A blanket <c>Cache-Control</c> written for every response would
    /// satisfy the assertion above while settling the question in the one place that knows least about
    /// what was returned. <c>GET /api/me/credentials</c> is the nearest route that carries no key
    /// material, so it is the one that must come back with no such header at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_AreAnsweredWithNoStore()
    {
        // Arrange — a populated answer, so the header is read off a response that really carried key
        // material rather than off an empty array.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedFactorsAsync(host, await SessionCredentialIdAsync(admin, signedIn.UserId), count: 1);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);
        HttpResponseMessage neighbour = await signedIn.Client.GetAsync(CredentialsPath);

        // Assert — the status first on both, so a header missing because the request failed reads as the
        // failure it is.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(HeaderOf(response, "Cache-Control")).IsEqualTo("no-store");

        await Assert.That(neighbour.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(HeaderOf(neighbour, "Cache-Control")).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A handle whose session has been revoked is refused, and the same client was answered a moment
    /// earlier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The handler deliberately asks nothing about liveness, and nothing pinned that the pipeline
    /// still does.</b> While <c>GetAccountKeysHandler</c> held an <c>ISessionRepository</c> the rule was
    /// a restraint written in a comment — "do not use this for liveness" — and the widening removed the
    /// dependency, which makes the restraint structural but leaves the <em>other</em> half unheld: that
    /// something, somewhere, still refuses a dead handle on the one route returning key material.
    /// <c>SessionCookieAuthenticationTests</c> makes that claim over its own route, and a route is not
    /// covered by a claim made about a different one — this route could acquire
    /// <c>AcceptsEndedSession</c>, or be moved outside the fallback policy, with that file still green.
    /// </para>
    /// <para>
    /// <b>The live request is the control and it comes first.</b> A route that refused everybody, a route
    /// behind a policy nobody can clear, and a seeded cookie that never worked all satisfy the refusal on
    /// their own; only the same client, on the same route, answered <c>200</c> before the row was stamped
    /// makes the 401 a verdict on the revocation.
    /// </para>
    /// <para>
    /// <b>Revoked rather than expired, and stamped through SQL rather than by waiting.</b> Revocation is
    /// a column the application role may write and expiry is a clock nothing here can move; stamping
    /// <c>revoked_at_utc</c> is the state a sign-out and a credential revocation both leave behind, and
    /// it is reached in one statement. The count of stamped rows is asserted, because an <c>update</c>
    /// that matched nothing would leave the second request answering 401 for no reason at all — and a
    /// second matched row would mean the account was seeded twice.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForARevokedSession_AreRefusedWithUnauthorized()
    {
        // Arrange — a signed-in account with a factor to hand back, so the control has a body.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedFactorsAsync(host, await SessionCredentialIdAsync(admin, signedIn.UserId), count: 1);

        // Act — the same client and the same route, either side of the stamp.
        HttpResponseMessage live = await signedIn.Client.GetAsync(AccountKeysPath);
        int stamped = await RevokeSessionsOfAsync(admin, signedIn.UserId);
        HttpResponseMessage ended = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — the control first, so a broken control is not hidden behind the refusal it qualifies.
        await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stamped).IsEqualTo(1);
        await Assert.That(ended.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A session opened by a federated credential is refused, and it is this gate's refusal rather than
    /// the CSRF control's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The session is seeded through the database, the way every locked session in the suite is.</b>
    /// The only credential type that opens one is <see cref="CredentialType.Federated" />, and the
    /// federated path mints no session cookie, so a test that waited for a sign-in to produce one would
    /// never reach the gate at all — which is exactly how a gate ships broken and green.
    /// </para>
    /// <para>
    /// <b>The accepting arm is a second account rather than the same one, and that departs from
    /// <see cref="LockedSessionTests" /> for a reason.</b> That file insists on one account so that a
    /// refusal cannot be explained by the tenant filter matching nothing. Here it cannot be explained
    /// that way in any case: <c>wrapped_account_keys</c> refuses a row against a federated credential
    /// outright, so a locked session's honest answer is the empty array — an application with no gate at
    /// all answers this request <c>200 []</c>, not a 403. What the control is needed for is narrower and
    /// still necessary: a route that refuses everybody satisfies the refusal perfectly.
    /// </para>
    /// <para>
    /// <b>The title is asserted because both refusals on this path are 403.</b>
    /// <see cref="FirstPartyRequestMiddleware" /> answers one to a request without the client header,
    /// before anything looks at a cookie — so a status read on its own would leave this test green
    /// against an application with no gate in it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForALockedSession_AreRefusedWithForbidden()
    {
        // Arrange — a locked session, and a full one on a second account as the control that the route
        // answers somebody at all.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient locked = await host.Factory.CreateSignedInClientAsync(
            Subject, kind: SessionKind.Locked, withFactorManifest: false);
        ApiFactory.SignedInClient full = await host.Factory.CreateSignedInClientAsync(OtherSubject, withFactorManifest: false);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedFactorsAsync(host, await SessionCredentialIdAsync(admin, full.UserId), count: 1);

        // Act
        HttpResponseMessage lockedResponse = await locked.Client.GetAsync(AccountKeysPath);
        HttpResponseMessage fullResponse = await full.Client.GetAsync(AccountKeysPath);

        // Assert — the control first, so a broken control is not hidden behind the refusal it qualifies.
        await Assert.That(fullResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // The refusal, and that it is this gate's refusal rather than the CSRF control's.
        await Assert.That(lockedResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await TitleOfAsync(lockedResponse)).IsNotEqualTo(FirstPartyRequestMiddleware.Title);
    }

    /// <summary>
    /// A caller presenting no session cookie is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a host that leaves the application's own authentication standing, so the 401 is produced by the
    /// cookie scheme being challenged rather than by a test handler standing in the default's place. This
    /// route authenticates from the cookie and from nothing else, and a refusal produced by some other
    /// scheme would be a fact about the fixture.
    /// </para>
    /// <para>
    /// It catches exactly one thing: the fallback policy being deleted, or this route being marked
    /// <c>AllowAnonymous</c>, either of which answers this request <c>200</c>. The second is also caught
    /// by <see cref="AnonymousSurfaceTests" />, which reads the marker off the route table; the first is
    /// caught by nothing else, because that test issues no request.
    /// </para>
    /// <para>
    /// <b>The refusal is the fallback policy's, and it lands on a mapped route.</b> <c>Program.cs</c>
    /// calls <c>MapAccountKeyEndpoints</c>, and that group states no authorization metadata of any kind
    /// — no <c>RequireAuthorization</c>, no <c>AllowAnonymous</c> — so this request matches a real
    /// endpoint and is challenged by the policy covering every route that declares nothing. That is
    /// what makes the paragraph above a claim about <em>this</em> route rather than about the pipeline
    /// in general.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_WithoutASessionCookie_IsRefusedWithUnauthorized()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();

        // Act — the factory's own client, which carries the first-party header and no cookie, so the
        // CSRF control lets it through and nothing authenticates it. GetAsync rather than GetStreamAsync:
        // the latter throws on any non-2xx, so a route that answered 200 to an anonymous caller would
        // fail as a transport error rather than as the status assertion it is.
        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync(AccountKeysPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    /// <summary>The two members carrying an envelope, named once so no assertion spells either twice.</summary>
    private static readonly string[] EnvelopeMembers = ["wrappedPrivateKey", "encapsulatedAccountKeys"];

    /// <summary>
    /// Every member one row carries, in the order the census joins them. Spread from
    /// <see cref="EnvelopeMembers" /> rather than typed again, so the two envelope names have one spelling
    /// in this file and a rename cannot leave the census agreeing with a stale copy of itself.
    /// </summary>
    private static readonly string[] RowMembers = ["factorId", .. EnvelopeMembers];

    /// <summary>
    /// The three members a row may never carry. Each is argued in the census's own remarks; listed here
    /// so the argument and the assertion cannot drift into different sets.
    /// </summary>
    private static readonly string[] MembersNoRowMayCarry = ["credentialId", "userId", "createdAtUtc"];

    /// <summary>
    /// The three characters that belong to the standard base64 alphabet and its padding, and to
    /// base64url's neither.
    /// </summary>
    /// <remarks>
    /// Listed rather than written into each assertion so the three cannot drift apart between the two
    /// tests that read them, and named for what they are: <c>+</c> and <c>/</c> are indexes 62 and 63
    /// under the alphabet this API does not use, and <c>=</c> is padding the client's decoder refuses
    /// outright rather than tolerates.
    /// </remarks>
    private static readonly string[] StandardAlphabetOnly = ["+", "/", "="];

    /// <summary>
    /// The three members the response body carries, and no fourth.
    /// </summary>
    /// <remarks>
    /// Built from the three constants the rest of this file reads rather than spelled out again, so a
    /// rename lands in one place and the census cannot end up agreeing with a stale copy of itself —
    /// the arrangement <see cref="RowMembers" /> keeps one level down.
    /// </remarks>
    private static readonly string[] BodyMembers = [ManifestMember, RotationEpochMember, FactorsMember];

    /// <summary>
    /// Asserts both directions of one caller's answer: that the rows are exactly the factors that
    /// credential holds, and that no identifier or envelope of the other account appears in the body it
    /// was sent.
    /// </summary>
    /// <remarks>
    /// The body is read once as text and parsed from that text, because the negative half needs the
    /// payload exactly as it went over the wire — a value hidden behind an escape sequence is a leak a
    /// search over a re-rendered document would report as absent.
    /// </remarks>
    private static async Task AssertCarriedExactlyAsync(
        HttpResponseMessage response,
        IReadOnlyList<WrappedKeyFixture> own,
        IReadOnlyList<WrappedKeyFixture> other)
    {
        // The status first, so a body that is missing because the request failed reads as the failure it
        // is rather than as a parse error several lines further down.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        JsonObject body = JsonNode.Parse(payload) as JsonObject
            ?? throw new InvalidOperationException("The endpoint answered something other than a JSON object.");

        await Assert.That(ArrivedRows(FactorsOf(body))).IsEqualTo(ExpectedRows(own));

        foreach (WrappedKeyFixture stranger in other)
        {
            await Assert.That(payload).DoesNotContain(stranger.FactorId);
            await Assert.That(payload).DoesNotContain(stranger.WrappedPrivateKey);
            await Assert.That(payload).DoesNotContain(stranger.EncapsulatedAccountKeys);
        }
    }

    /// <summary>
    /// Every row of a response body rendered whole, sorted and joined — the arriving half of every
    /// "exactly these factors carrying exactly these envelopes" comparison in this file.
    /// </summary>
    /// <remarks>
    /// <b>Sorted, so it is a set comparison and not an order one.</b> The endpoint promises that two
    /// reads of unchanged rows agree and promises nothing about the <em>particular</em> sequence —
    /// <c>uuid</c> collation is provider-defined, and an in-memory implementation and PostgreSQL may
    /// disagree about which of two rows comes first with neither being wrong. A caller comparing arrival
    /// order would be pinning a sequence the contract deliberately leaves open. Joined rather than
    /// counted, so a failure names the row that arrived, went missing or came back wrong.
    /// </remarks>
    private static string ArrivedRows(JsonArray entries) =>
        Join(entries.Select(entry => Render(
            AsObject(entry)["factorId"]!.GetValue<string>(),
            AsObject(entry)["wrappedPrivateKey"]!.GetValue<string>(),
            AsObject(entry)["encapsulatedAccountKeys"]!.GetValue<string>())));

    /// <summary>
    /// The factor identifiers of a response body <b>in the order they arrived</b>, joined.
    /// </summary>
    /// <remarks>
    /// <b>The deliberate opposite of <see cref="ArrivedRows" />, and the two must not be merged.</b>
    /// That one sorts, because every comparison against a written-out expectation has to be a set
    /// comparison — the endpoint promises no particular sequence and an expectation naming one would
    /// pin a claim the product has not made. This one preserves arrival order, because the single case
    /// that reads it compares two responses against each other rather than against an expectation, and
    /// order is the only thing it is about. Sorting here would make that case pass on any response at
    /// all.
    /// </remarks>
    private static string ArrivalSequence(JsonArray entries) =>
        string.Join(
            "\n",
            entries.Select(entry => AsObject(entry)["factorId"]!.GetValue<string>()));

    /// <summary>The expected half of the same comparison, rendered the same way.</summary>
    private static string ExpectedRows(IEnumerable<WrappedKeyFixture> factors) =>
        Join(factors.Select(factor =>
            Render(factor.FactorId, factor.WrappedPrivateKey, factor.EncapsulatedAccountKeys)));

    private static string Render(string factorId, string content, string index) =>
        $"{factorId} content={content} index={index}";

    private static string Join(IEnumerable<string> rows) =>
        string.Join("\n", rows.Order(StringComparer.Ordinal));

    /// <summary>
    /// One response header read as raw text, or the empty string when the response carries none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raw rather than through the typed accessor, because the typed one folds the difference the
    /// cache-control test is about: <c>CacheControlHeaderValue.NoStore</c> is true for the four-directive
    /// spelling as well as for <c>no-store</c> alone.
    /// </para>
    /// <para>
    /// Both collections are asked. <see cref="HttpResponseMessage.Headers" /> is where a general header
    /// lands, but a value the framework classified as a content header would otherwise read as absent —
    /// and "absent" is exactly what the control below asserts, so a lookup that could miss a present
    /// header would make the control pass over the thing it exists to refuse.
    /// </para>
    /// </remarks>
    private static string HeaderOf(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? string.Join(", ", values)
            : response.Content.Headers.TryGetValues(name, out IEnumerable<string>? contentValues)
                ? string.Join(", ", contentValues)
                : string.Empty;

    /// <summary>
    /// Stamps <c>revoked_at_utc</c> on every live session of one account, on the container superuser,
    /// and answers how many rows it reached.
    /// </summary>
    /// <remarks>
    /// The instant is a second in the past rather than <c>now()</c>, so a comparison written as strictly
    /// "before" cannot read the row as still live on a clock that has not ticked.
    /// </remarks>
    private static async Task<int> RevokeSessionsOfAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "update sessions set revoked_at_utc = @revokedAt "
            + "where user_id = @userId and revoked_at_utc is null",
            admin);
        command.Parameters.AddWithValue("revokedAt", DateTime.UtcNow.AddSeconds(-1));
        command.Parameters.AddWithValue("userId", userId);

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A manifest of exactly <see cref="FactorManifest.MaximumBytes" /> bytes, no two neighbouring
    /// windows of which are alike.
    /// </summary>
    /// <remarks>
    /// The stride is odd and therefore coprime with 256, so the sequence runs the whole byte range
    /// before it repeats and every 256-byte window is a rotation of a different one. The offset keeps
    /// index 0 off value 0, so a value that arrived shifted by a position is visible at the very first
    /// byte rather than only deep inside.
    /// </remarks>
    private static byte[] WidestManifest()
    {
        byte[] manifest = new byte[FactorManifest.MaximumBytes];

        for (int index = 0; index < manifest.Length; index++)
        {
            manifest[index] = (byte)(0x11 + (index * 7));
        }

        return manifest;
    }

    /// <summary>
    /// The first position at which two byte sequences disagree, rendered for a human — or the empty
    /// string when they are identical.
    /// </summary>
    /// <remarks>
    /// <b>A rendered difference rather than a whole-value comparison, because the value under test is
    /// four kilobytes.</b> Comparing two 8192-character hex strings, or two 4096-element collections,
    /// produces a failure message the assertion library truncates — so the reader is shown the opening
    /// bytes of two values that are identical there and is told nothing about where they parted. One
    /// index and two bytes says whether a value was cut, shifted or rebuilt, and it says it in a line.
    /// <b>The caller compares against <c>string.Empty</c> rather than calling <c>IsEmpty()</c>, and the
    /// difference was measured rather than assumed:</b> TUnit's <c>IsEmpty()</c> reports only
    /// <c>Expected to be empty</c> and throws the description away, while <c>IsEqualTo(string.Empty)</c>
    /// prints it — which is the entire reason this helper returns a sentence instead of a
    /// <see langword="bool" />.
    /// </remarks>
    private static string FirstDifference(
        IReadOnlyList<byte> arrived,
        IReadOnlyList<byte> expected)
    {
        for (int index = 0; index < Math.Min(arrived.Count, expected.Count); index++)
        {
            if (arrived[index] != expected[index])
            {
                return $"byte {index}: expected 0x{expected[index]:X2}, arrived 0x{arrived[index]:X2}";
            }
        }

        return arrived.Count == expected.Count
            ? string.Empty
            : $"length: expected {expected.Count} bytes, arrived {arrived.Count}";
    }

    /// <summary>
    /// The <c>rotationEpoch</c> of a response body read from its raw payload, for the cases that hold
    /// the payload as text.
    /// </summary>
    private static int EpochOf(string payload) =>
        (JsonNode.Parse(payload) as JsonObject
         ?? throw new InvalidOperationException("The endpoint answered something other than a JSON object."))
        [RotationEpochMember]!.GetValue<int>();

    /// <summary>One envelope member, decoded and rendered as hex so a failure prints the bytes.</summary>
    private static string DecodedHex(JsonObject row, string member) =>
        Convert.ToHexString(Base64UrlText.Decode(row[member]!.GetValue<string>()));

    /// <summary>
    /// The <c>credentials.id</c> of the credential that opened this account's session, read on the
    /// container superuser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read back rather than returned from the seeding, and the alternative was to widen a shared
    /// record.</b> <c>RepositoryTestHost.SignedInOwner</c> and <c>ApiFactory.SignedInClient</c> are
    /// destructured at call sites across the suite, and a fourth member on either would be carried by
    /// every one of them for the sake of the one file that reads it. This lookup costs one statement and
    /// touches nothing.
    /// </para>
    /// <para>
    /// It is also the <em>better</em> source, not merely the cheaper one: what the endpoint reads is
    /// <c>sessions.credential_id</c>, so taking the value off that column measures the credential the
    /// request will really resolve rather than the one the seeder believes it wrote. A seeder that filed
    /// the session against another credential would fail here instead of leaving every assertion above
    /// it green about rows nothing will ever return.
    /// </para>
    /// <para>
    /// Exactly one row, refused otherwise. Each seeded sign-in writes one session, so two means the
    /// account was seeded twice and a caller taking "the first" would be scoped to whichever came back
    /// first; none means the sign-in wrote nothing and every later assertion is about a session that does
    /// not exist.
    /// </para>
    /// </remarks>
    private static async Task<Guid> SessionCredentialIdAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select credential_id from sessions where user_id = @userId", admin);
        command.Parameters.AddWithValue("userId", userId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"No session is filed under account '{userId}'.");
        }

        Guid credentialId = reader.GetGuid(0);

        return await reader.ReadAsync()
            ? throw new InvalidOperationException(
                $"Account '{userId}' holds more than one session; this lookup assumes exactly one.")
            : credentialId;
    }

    /// <summary>
    /// Files <paramref name="count" /> factors against one credential, each with a fresh identifier and
    /// its own pair of envelopes, and hands back what was written.
    /// </summary>
    /// <remarks>
    /// <see cref="WrappedKeyFixture" /> rather than the seeder's default fillers, because those are
    /// identical on every row and would make a cross-row mix-up invisible at ten factors — see the
    /// remarks on the class. The fixtures are returned rather than read back out of the database: they
    /// are what the seeding was <em>handed</em>, so an assertion against them is a claim about what the
    /// endpoint did with the row rather than about what the seeder happened to store.
    /// </remarks>
    private static async Task<WrappedKeyFixture[]> SeedFactorsAsync(
        PostgresTestHost host,
        Guid credentialId,
        int count)
    {
        List<WrappedKeyFixture> factors = [];
        for (int index = 0; index < count; index++)
        {
            WrappedKeyFixture factor = WrappedKeyFixture.Mint();
            await SeedFactorAsync(host, credentialId, factor);
            factors.Add(factor);
        }

        return [.. factors];
    }

    /// <summary>
    /// Files one factor against one credential, over the superuser connection this host owns.
    /// </summary>
    /// <remarks>
    /// Through <c>RepositoryTestHost</c>'s connection-string seeder rather than an instance of it, which
    /// is the seam that file describes: the sign-in harness these tests use lives on
    /// <see cref="PostgresTestHost" />, which holds an admin connection string and no
    /// <c>RepositoryTestHost</c>, and a copy of the seeding here would be the fourth.
    /// </remarks>
    private static Task SeedFactorAsync(PostgresTestHost host, Guid credentialId, WrappedKeyFixture factor) =>
        RepositoryTestHost.SeedWrappedAccountKeysOnAsync(
            host.ConnectionString,
            credentialId,
            factor.Factor,
            wrappedPrivateKey: factor.PrivateKeyEnvelope,
            encapsulatedAccountKeys: factor.AccountKeysEnvelope);

    /// <summary>
    /// Files the one <c>factor_manifests</c> row an account may hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On <see cref="PostgresTestHost.ConnectionString" />, which is the elevated connection, and
    /// that is not a convenience.</b> The application role holds <c>SELECT</c> on this table and no
    /// write privilege of any shape, so there is no path through the API that can bring this row into
    /// existence and no seeding call over the app connection that would not meet <c>42501</c>. Every
    /// other arrangement in this file already uses the same connection for the same reason: the
    /// fixture writes what the product cannot.
    /// </para>
    /// <para>
    /// Through the entity factory, the way the wrapped-key rows beside it are seeded — so the row is
    /// one the application could really have written if it had the grant, rather than one whose column
    /// list a test typed out.
    /// </para>
    /// </remarks>
    private static Task SeedManifestAsync(
        PostgresTestHost host,
        Guid userId,
        byte[] manifest,
        int rotationEpoch) =>
        RepositoryTestHost.SeedFactorManifestOnAsync(
            host.ConnectionString, userId, manifest, rotationEpoch);

    /// <summary>
    /// Files a second credential on an existing account, with factors of its own — the rows that must
    /// arrive beside the session credential's, and whose absence is the defect this file was retargeted
    /// to catch.
    /// </summary>
    /// <remarks>
    /// A passkey, because it is the credential type the seeder can add more than one of: an account holds
    /// exactly one federated credential and at most one set of recovery codes, and the database refuses a
    /// wrapped-key row against a federated one in any case. What it stands for is any second credential
    /// on the account, which is the state every real account is in — a passkey beside a set of codes.
    /// </remarks>
    private static async Task<WrappedKeyFixture[]> SeedBystanderCredentialAsync(
        PostgresTestHost host,
        Guid userId,
        int count)
    {
        Guid credentialId = await RepositoryTestHost.SeedPasskeyOnAsync(
            host.ConnectionString, userId, RandomNumberGenerator.GetBytes(16));

        return await SeedFactorsAsync(host, credentialId, count);
    }

    /// <summary>The response body read as text, for the assertions that search it whole.</summary>
    private static async Task<string> ReadPayloadAsync(HttpClient client) =>
        await (await client.GetAsync(AccountKeysPath)).Content.ReadAsStringAsync();

    /// <summary>
    /// The response body as a JSON <b>object</b>, refusing anything that is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The body used to be the factors array itself, and the refusal moved up a level with it rather
    /// than being relaxed.</b> The contract is now one object carrying the account's manifest, the
    /// generation that manifest belongs to, and the factors — so a lenient read here would let a bare
    /// array, a <c>null</c> body or a problem-details document pass as "no rows arrived", which reads as
    /// a projection bug rather than as the shape change it is.
    /// </para>
    /// <para>
    /// <b>The descent lives in the helper rather than in the seven callers, and that is the decision.</b>
    /// Every one of those callers wants the factors and nothing else, so putting the two-step parse in
    /// each of them would be the same four lines written seven times — and the fourth or fifth copy is
    /// where somebody writes <c>body["factors"] as JsonArray ?? []</c> and turns a body that stopped
    /// carrying factors at all into an empty list nobody notices. One place to be strict is one place to
    /// stay strict. What it costs is that a caller can no longer say anything about the body's top level
    /// through this helper, which is why <see cref="ReadBodyAsync" /> is public to the file beside it and
    /// is what the manifest cases read.
    /// </para>
    /// </remarks>
    private static async Task<JsonObject> ReadBodyAsync(HttpResponseMessage response) =>
        await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()) as JsonObject
        ?? throw new InvalidOperationException("The endpoint answered something other than a JSON object.");

    /// <summary>
    /// The factors of a response body, refusing a body carrying no <c>factors</c> array.
    /// </summary>
    /// <remarks>
    /// A missing member and a member that is not an array are one failure with one message: in both
    /// cases the response stopped offering the list this endpoint exists to hand back, and telling the
    /// two apart would buy a reader nothing they cannot see in the body the message is about.
    /// </remarks>
    private static JsonArray FactorsOf(JsonObject body) =>
        body[FactorsMember] as JsonArray
        ?? throw new InvalidOperationException(
            $"The response body carries no '{FactorsMember}' array.");

    /// <summary>The factors of the response, read one level down from its top-level object.</summary>
    private static async Task<JsonArray> ReadFactorsAsync(HttpResponseMessage response) =>
        FactorsOf(await ReadBodyAsync(response));

    /// <summary>
    /// One array element as an object, refusing anything that is not one.
    /// </summary>
    /// <remarks>
    /// Checked rather than forgiven: an element that is a bare string or <see langword="null" /> would
    /// give an empty member list through a lenient read, and an empty rendering is what the comparisons
    /// above read as "the rows are wrong" — a red nobody could interpret. Failing here says the row
    /// stopped being an object at all.
    /// </remarks>
    private static JsonObject AsObject(JsonNode? entry) =>
        entry as JsonObject
        ?? throw new InvalidOperationException("A row was something other than a JSON object.");

    /// <summary>
    /// The <c>title</c> of a problem-details body, or the empty string when the body carries none.
    /// </summary>
    private static async Task<string> TitleOfAsync(HttpResponseMessage response)
    {
        string raw = await response.Content.ReadAsStringAsync();

        return JsonNode.Parse(raw) is JsonObject body && body["title"] is JsonNode title
            ? title.GetValue<string>()
            : string.Empty;
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every request
    /// here authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();

        return host;
    }
}
