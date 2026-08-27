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
/// that <b>the credential which opened this session</b> can derive a key-encryption key for — one pair
/// for a passkey, ten for a set of recovery codes — and nobody else's, ever.
/// </summary>
/// <remarks>
/// <para>
/// Driven over real HTTP rather than against <c>GetAccountKeysHandler</c>, because half of what is
/// measured here is <em>which session the request arrives as</em>. Nothing in the request names a
/// session, a credential or an account: the session id is read off the claim this request's own
/// authentication produced, and the owner off the resolved context. A handler tested in isolation would
/// be handed both of the things these tests exist to check the pipeline produces.
/// </para>
/// <para>
/// <b>No test here names a type belonging to this endpoint</b> — no request record, no response record,
/// no handler, no read service. They address the route over HTTP and read the wire body, so while the
/// route is unmapped they fail on the status assertion against a real 404 rather than failing to
/// compile, which is the difference between a red test that is telling us something and one that is
/// telling us nothing. The one production type the arrangements name is <see cref="CredentialType" />,
/// to say which credential opens a seeded session; it belongs to no part of this endpoint's contract, so
/// naming it cannot make a test agree with the thing it measures.
/// </para>
/// <para>
/// <b>Every arrangement seeds a <em>second</em> credential holding factors of its own, and that is the
/// load-bearing half of the file.</b> An account really does hold a passkey and a set of recovery codes
/// at once — eleven wrapped rows across two credentials — and only the rows under the credential that
/// just authenticated can be opened by anything the browser is holding. With one credential seeded,
/// "this credential's rows" and "this account's rows" are the same set, `user_isolation` scopes both
/// identically, and a read that dropped the credential predicate entirely would be green everywhere.
/// </para>
/// <para>
/// <b><see cref="AccountKeys_ForARecoveryCodesSession_CarryAllTenFactorsWithTheirOwnEnvelopes" /> is the
/// most important test in this file</b>, and it is the reason the seeding had to change.
/// <c>wrapped_account_keys</c> is keyed on <c>factor_id</c> and a set of recovery codes files
/// <b>ten</b> rows under one <c>credential_id</c>, so a projection reaching for
/// <c>SingleOrDefault</c> — or a response type of one pair rather than a list — is correct for every
/// passkey in the product and drops nine of every ten recovery-code envelopes. Nothing on the server can
/// see that happen: it surfaces in a browser months later, on the day somebody who has already lost
/// their authenticator redeems a code, is handed a session, and finds the account still locked.
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
    private const string AccountKeysPath = "/api/me/account-keys";

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
    /// A factor identifier whose hex carries letters in every group, so that "the canonical lower-case
    /// hyphenated spelling" is a claim with something to be wrong about. The all-digit UUID a random
    /// mint occasionally produces renders identically in either case, and a test seeded with one would
    /// pass against a response that upper-cased everything.
    /// </summary>
    private static readonly Guid LetteredFactorId = new("c1d2e3f4-5a6b-7c8d-9e0f-a1b2c3d4e5f6");

    /// <summary>
    /// A passkey session is handed exactly one pair, and it is the pair filed under the credential that
    /// opened it.
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
    /// <b>The second credential is what makes "that credential's" mean anything.</b> It holds a factor of
    /// its own on the same account, under the same <c>user_id</c>, so a read scoped by owner alone — the
    /// scoping <c>user_isolation</c> would supply underneath any statement at all — hands back two pairs
    /// and fails here. Without it the strongest wrong implementation in the file passes.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForAPasskeySession_CarryTheSessionCredentialsOnePair()
    {
        // Arrange — a session opened by a passkey, one factor under that passkey, and a second
        // credential on the same account carrying a factor that must not arrive.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);

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
        // a stranger's row and a swapped envelope each arrive named in the failure message.
        await Assert.That(ArrivedRows(await ReadArrayAsync(response))).IsEqualTo(ExpectedRows(own));

        // And the other credential's factor is nowhere in the body at all, not merely absent from the
        // member this test reads.
        await Assert.That(await ReadPayloadAsync(signedIn.Client)).DoesNotContain(bystander[0].FactorId);
    }

    /// <summary>
    /// A session opened by a set of recovery codes is handed all ten factors, each carrying its own pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The count is the smaller half of what this measures.</b> Ten distinct factor identifiers refuse
    /// a <c>SingleOrDefault</c>, a <c>FirstOrDefault</c> and a nullable single-pair return type; ten
    /// distinct <em>pairs of envelopes</em>, matched per row, refuse a projection that answers the right
    /// number of rows with one row's bytes repeated, or with the pairs rotated against the identifiers.
    /// The second of those is invisible to any count and to any assertion over identifiers alone, and it
    /// is the one that puts a browser in front of an envelope whose associated data it cannot rebuild.
    /// </para>
    /// <para>
    /// The ten identifiers are asserted distinct in the arrangement rather than taken on trust. They are
    /// minted independently, and a seeder that reused one would leave the comparison below reading nine
    /// rows against nine — a green result over an arrangement that never happened.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForARecoveryCodesSession_CarryAllTenFactorsWithTheirOwnEnvelopes()
    {
        // Arrange — the session opens over the set, which is the credential the ten rows hang off.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setCredentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);

        WrappedKeyFixture[] own = await SeedFactorsAsync(host, setCredentialId, RequiredCodeCount);
        WrappedKeyFixture[] bystander = await SeedBystanderCredentialAsync(host, signedIn.UserId, count: 1);

        // The arrangement really is ten distinct factors, or the comparison below reads fewer rows than
        // it thinks it does and passes for a reason that has nothing to do with the endpoint.
        await Assert.That(own.Select(factor => factor.FactorId).Distinct(StringComparer.Ordinal).Count())
            .IsEqualTo(RequiredCodeCount);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ArrivedRows(await ReadArrayAsync(response))).IsEqualTo(ExpectedRows(own));

        // And nothing of the passkey beside the set arrived.
        await Assert.That(await ReadPayloadAsync(signedIn.Client)).DoesNotContain(bystander[0].FactorId);
    }

    /// <summary>
    /// That <c>wrappedContentKey</c> carries the content envelope and <c>wrappedIndexKey</c> the index
    /// one, on every row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This assertion cannot be weakened into a shape check, and the reason is that the server has no
    /// symptom at all.</b> Both columns are exactly
    /// <c>WrappedAccountKeys.EnvelopeLength</c> bytes, both carry the same leading version byte, and both
    /// are <c>NOT NULL</c> — so a projection that reads each into the other's member satisfies every
    /// check constraint the table holds, every width bound the Application ring restates, and every
    /// assertion that measures a length or a version. Nothing goes red, no row is malformed and no log
    /// line is written. What separates the two envelopes is the <em>purpose</em> bound into the associated
    /// data they were sealed under, which lives in the browser and which this server cannot read.
    /// The failure therefore surfaces months later, in somebody's browser, as an account whose envelopes
    /// will not open — and by then the swap is in every response the endpoint has ever served.
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
    public async Task AccountKeys_NeverSwapTheContentAndIndexEnvelopes()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setCredentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);
        WrappedKeyFixture[] own = await SeedFactorsAsync(host, setCredentialId, RequiredCodeCount);

        // The two envelopes of one factor really do differ, or "they were not swapped" is a claim about
        // two values nothing could tell apart.
        await Assert.That(Convert.ToHexString(own[0].ContentEnvelope))
            .IsNotEqualTo(Convert.ToHexString(own[0].IndexEnvelope));

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as an empty row set nobody would recognise as a 404.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);
        Dictionary<string, WrappedKeyFixture> seeded =
            own.ToDictionary(factor => factor.FactorId, StringComparer.Ordinal);

        // Every arriving row, against the pair its own factor was sealed with. Read out of the seeded set
        // by factor identifier rather than by position, so this says nothing about ordering — which
        // AccountKeys_ForARecoveryCodesSession_CarryAllTenFactorsWithTheirOwnEnvelopes has already
        // compared as a set, and which the endpoint promises nothing particular about.
        await Assert.That(entries.Count).IsEqualTo(RequiredCodeCount);
        foreach (JsonNode? entry in entries)
        {
            JsonObject row = AsObject(entry);
            WrappedKeyFixture factor = seeded[row["factorId"]!.GetValue<string>()];

            await Assert.That(DecodedHex(row, "wrappedContentKey"))
                .IsEqualTo(Convert.ToHexString(factor.ContentEnvelope));
            await Assert.That(DecodedHex(row, "wrappedIndexKey"))
                .IsEqualTo(Convert.ToHexString(factor.IndexEnvelope));
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
            Subject, opensWith: CredentialType.RecoveryCodes);
        WrappedKeyFixture[] firstFactors = await SeedFactorsAsync(
            host, await SessionCredentialIdAsync(admin, first.UserId), RequiredCodeCount);

        ApiFactory.SignedInClient second = await host.Factory.CreateSignedInClientAsync(OtherSubject);
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
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);

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
    /// Padding is the case worth naming: an envelope is
    /// <c>WrappedAccountKeys.EnvelopeLength</c> bytes, which is not a multiple of three, so the standard
    /// encoder appends <c>=</c> characters to every one of them — this is not a corner a fixture had to
    /// be chosen to reach. The two alphabet characters are checked over the payload as a whole rather
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
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(Subject);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid credentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);
        WrappedKeyFixture[] own = await SeedFactorsAsync(host, credentialId, count: 1);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);
        await Assert.That(entries.Count).IsEqualTo(1);

        JsonObject row = AsObject(entries[0]);
        foreach (string member in EnvelopeMembers)
        {
            string encoded = row[member]!.GetValue<string>();

            await Assert.That(encoded).DoesNotContain("=");
            await Assert.That(encoded).DoesNotContain("+");
            await Assert.That(encoded).DoesNotContain("/");

            // And it really is the envelope, decoded — which is what stops the three absences above
            // passing over a member that carries no base64url at all.
            await Assert.That(Base64UrlText.Decode(encoded).Length).IsEqualTo(own[0].ContentEnvelope.Length);
        }
    }

    /// <summary>
    /// That a row carries exactly <c>factorId</c>, <c>wrappedContentKey</c> and <c>wrappedIndexKey</c> —
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
    /// response body may never carry into a client log. <c>createdAtUtc</c> is a usage record beside key
    /// material: it says when each of an account's ten codes was issued, which is a timeline of somebody's
    /// recovery history that the screen reading this endpoint has no use for — the same argument
    /// <c>ProhibitedColumnVocabulary</c> makes against a <c>last_login</c>.
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
            Subject, opensWith: CredentialType.RecoveryCodes);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid setCredentialId = await SessionCredentialIdAsync(admin, signedIn.UserId);
        await SeedFactorsAsync(host, setCredentialId, RequiredCodeCount);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as a member list nobody would recognise as a 404.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);
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
    /// A credential holding no factors is answered <c>200</c> with an empty array, and never a 404.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pinned explicitly because a 404 is the plausible wrong answer</b> — there really are no rows to
    /// read, and a handler written as "find the factors, then project them" reaches for one naturally.
    /// It is also the answer a reader will argue for on the grounds that the state should not exist.
    /// </para>
    /// <para>
    /// <b>A 404 would rebuild the enumeration oracle this endpoint refuses to be.</b> An empty answer is
    /// what a request whose session was never established, whose session has already ended, and whose
    /// session belongs to somebody else all receive, indistinguishably. The moment "no rows" answers
    /// differently from those, a caller learns which of them happened — and on the one route that names
    /// an account's key custody, that is the whole of what an attacker wanted.
    /// </para>
    /// <para>
    /// <b>Both halves are stated</b>, because "not 404" is not the same claim as "200 with an empty
    /// array": a route answering 204, or 200 with an empty body, satisfies the first and leaves the
    /// browser with nothing to iterate. The status assertion comes first so a parse failure on an empty
    /// body reads as the status it is.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeys_ForACredentialWithNoFactors_IsAnEmptyArrayAndNeverANotFound()
    {
        // Arrange — a signed-in account whose session credential has had nothing filed against it. The
        // set arm of the seeding writes one credentials row and no wrapped keys, which is exactly this
        // state without anything having to be deleted to reach it.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);

        // Act
        HttpResponseMessage response = await signedIn.Client.GetAsync(AccountKeysPath);

        // Assert — both halves, so the answer this test refuses is named rather than merely excluded.
        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.NotFound);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        await Assert.That((await ReadArrayAsync(response)).Count).IsEqualTo(0);
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
            Subject, kind: SessionKind.Locked);
        ApiFactory.SignedInClient full = await host.Factory.CreateSignedInClientAsync(OtherSubject);

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
    /// <b>It is green today, and for a reason that has nothing to do with what it will measure once the
    /// route exists — so a reader who runs it now and sees green is not seeing it work.</b> The fallback
    /// policy is applied by the authorization middleware to requests that match <em>no endpoint at all</em>,
    /// so this request is answered 401 by a pipeline that has never heard of <c>/api/me/account-keys</c>,
    /// exactly as it would answer 401 to <c>/api/me/anything-else</c>. Nothing about that green says the
    /// route is authenticated, or that it is mapped, or that it exists. It starts saying something the
    /// moment the route is mapped, and from that moment it is the only request-driven check that the
    /// fallback policy still covers it.
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
    private static readonly string[] EnvelopeMembers = ["wrappedContentKey", "wrappedIndexKey"];

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
        JsonArray entries = JsonNode.Parse(payload) as JsonArray
            ?? throw new InvalidOperationException("The endpoint answered something other than a JSON array.");

        await Assert.That(ArrivedRows(entries)).IsEqualTo(ExpectedRows(own));

        foreach (WrappedKeyFixture stranger in other)
        {
            await Assert.That(payload).DoesNotContain(stranger.FactorId);
            await Assert.That(payload).DoesNotContain(stranger.WrappedContentKey);
            await Assert.That(payload).DoesNotContain(stranger.WrappedIndexKey);
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
            AsObject(entry)["wrappedContentKey"]!.GetValue<string>(),
            AsObject(entry)["wrappedIndexKey"]!.GetValue<string>())));

    /// <summary>The expected half of the same comparison, rendered the same way.</summary>
    private static string ExpectedRows(IEnumerable<WrappedKeyFixture> factors) =>
        Join(factors.Select(factor =>
            Render(factor.FactorId, factor.WrappedContentKey, factor.WrappedIndexKey)));

    private static string Render(string factorId, string content, string index) =>
        $"{factorId} content={content} index={index}";

    private static string Join(IEnumerable<string> rows) =>
        string.Join("\n", rows.Order(StringComparer.Ordinal));

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
            wrappedContentKey: factor.ContentEnvelope,
            wrappedIndexKey: factor.IndexEnvelope);

    /// <summary>
    /// Files a second credential on an existing account, with factors of its own — the rows that must
    /// never arrive.
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
    /// The response body as a JSON <b>array</b>, refusing anything that is not one.
    /// </summary>
    /// <remarks>
    /// The contract is an array rather than an envelope carrying one, and a lenient read would let an
    /// object with an <c>items</c> member pass as "no rows arrived" — which reads as a projection bug
    /// rather than as the shape change it is.
    /// </remarks>
    private static async Task<JsonArray> ReadArrayAsync(HttpResponseMessage response) =>
        await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()) as JsonArray
        ?? throw new InvalidOperationException("The endpoint answered something other than a JSON array.");

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
