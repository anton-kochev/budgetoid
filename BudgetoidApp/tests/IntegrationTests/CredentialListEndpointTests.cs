using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Domain.Users;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That a signed-in person can see every way their account can be signed in to — the one federated
/// credential the provider gates registration with, and every passkey registered beside it — and that
/// what comes back is that account's list and nobody else's.
/// </summary>
/// <remarks>
/// <para>
/// Driven over real HTTP rather than against a handler, because half of what is measured here is
/// <em>which account the request arrives as</em>. Nothing in the request names an account: the identity
/// is resolved from the authenticated subject by the middleware above the route, so a handler tested in
/// isolation would be handed the very thing these tests exist to check the pipeline produces.
/// </para>
/// <para>
/// <b><see cref="Credentials_ForASecondAccount_ListThatAccountsCredentialsAndNotTheFirsts" /> is the
/// most important test in this file.</b> <c>credentials</c> is exempt from row-level security — it is
/// the table a request is resolved <em>out of</em>, so a policy keyed on the identity it resolves would
/// refuse the query that resolves it — which means the owner predicate on this read is scoped by the
/// application and by nothing beneath it. No policy narrows it, no query filter narrows it, and the
/// grant is on the whole table. <c>docs/engineering/data-isolation.md</c> names that test, by that
/// spelling, in its exempt-table access inventory as the only thing that would notice the
/// <c>where user_id</c> going missing. Every other test in this file stays green without it.
/// </para>
/// <para>
/// <b>The federated credential's <c>subject</c> must never reach the wire</b>, and
/// <see cref="Credentials_NeverCarryTheFederatedSubject" /> is the only test that says so. The
/// provider's <c>sub</c> claim is a value the API has no reason to hand back — it is the key the whole
/// identity model turns on, and publishing it to a client is the first half of a client-supplied
/// identity parameter. That test asserts against the response <em>as it went over the wire</em>, because
/// a member the serializer escaped would read as absent through a parsed document.
/// </para>
/// <para>
/// <b>What a list entry deliberately does not carry</b> — no device name, no user-chosen label, no
/// last-used instant, no AAGUID, no transports. Two passkeys are told apart by the day they were
/// registered and by nothing better. The decision-log entry of 2026-08-10 argues each omission: a column
/// on <c>credentials</c> breaks the pinned exemption column set, whose doctrine is <em>move the column,
/// never widen the pin</em>; the AAGUID arrives zeroed because the ceremony requests
/// <c>attestation: "none"</c> precisely so registration collects no device fingerprint; and a "last
/// used" timestamp is a usage record sitting next to the <c>last_login</c> that
/// <c>ProhibitedColumnVocabulary</c> refuses. Do not add coverage suggesting otherwise.
/// </para>
/// <para>
/// <b>Every test here that needs an account signs in over a set of recovery codes, and that is a
/// decision about counting rather than about authentication.</b> The sign-in harness's default opens a
/// full session with a passkey, which files a <c>credentials</c> row, a <c>passkey_public_keys</c> row
/// and a <c>passkey_signature_counters</c> row — so the account would hold one more passkey than the
/// test arranged, and <c>federated == 1</c> beside <c>passkey == 2</c> would be reading the seeding
/// instead of the act. A set of recovery codes opens the same full session while writing a single
/// <c>credentials</c> row and touching neither passkey table, which is what lets those two assertions go
/// on saying exactly what they were written to say. The consequence every arrangement here carries is
/// that an account holds a <b>third</b> type of credential it did not used to, so nothing in this file
/// may count entries in the absolute any more: what an entry count used to claim — "the ones I arranged
/// and no others" — is now said directly, by comparing against the account's own rows read on the
/// container superuser.
/// </para>
/// <para>
/// No test here names a type the endpoint declares — no request record, no response record, no handler.
/// They address the route over HTTP and read the wire body, so while the endpoint is unmapped they fail
/// on the status assertion against a real 404 rather than failing to compile — which is the difference
/// between a red test that is telling us something and one that is telling us nothing. What the
/// arrangements do name is <see cref="CredentialType" />, to say which credential opens the seeded
/// session, and <see cref="UserProvisioningMiddleware.NoAccountTitle" />, to tell this route's two
/// refusals apart. Neither belongs to this endpoint's contract, so naming either cannot make a test
/// agree with the thing it measures.
/// </para>
/// </remarks>
public sealed class CredentialListEndpointTests
{
    private const string CredentialsPath = "/api/me/credentials";

    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";

    /// <summary>The account under test in most of the file.</summary>
    private const string Subject = "google-listing";

    /// <summary>
    /// The bystander account: the one whose credentials must not appear in the first account's list,
    /// and whose mere existence is what makes the owner predicate on this read measurable at all.
    /// </summary>
    private const string OtherSubject = "google-listing-bystander";

    /// <summary>
    /// The federated subject of the account in <see cref="Credentials_NeverCarryTheFederatedSubject" />.
    /// Deliberately long and unlike anything else the response can legitimately contain — a GUID, an
    /// ISO-8601 instant, or the words <c>passkey</c> and <c>federated</c> — so a substring search coming
    /// back empty means the value is genuinely absent rather than that the needle was too ordinary to
    /// find.
    /// </summary>
    private const string DistinctiveSubject = "google-oauth-sub-9d41f7b2c0e84a6fb35e-never-on-the-wire";

    /// <summary>
    /// The members one entry carries, joined and ordered exactly as
    /// <see cref="Credentials_EntryCarriesTheIdTheTypeAndTheDateAndNothingElse" /> builds them, so the
    /// expectation is written down once rather than typed into an assertion twice.
    /// </summary>
    private const string EntryMembers = "createdAtUtc, id, type";

    /// <summary>
    /// The <b>three</b> spellings of <c>type</c>, as the schema itself spells them, joined ordinally.
    /// </summary>
    /// <remarks>
    /// <b>It used to say two, and adding the third is a correction rather than a concession.</b> The
    /// endpoint has to spell every declared <see cref="CredentialType" /> the way its column is spelled,
    /// and <c>recovery_codes</c> is the member that makes that a rule rather than a coincidence — it is
    /// the two-word one, the one a camel-case policy over the member name renders as <c>recoveryCodes</c>
    /// where the column says <c>recovery_codes</c>. This test could not see it before, for the plain
    /// reason that the account it listed never held a set. Now that the session is opened over one, it
    /// can, and the constant says so. Nothing was widened to keep a green test green: the assertion is
    /// the same shape it was, over one more spelling than it used to be able to reach.
    /// </remarks>
    private const string SchemaTypeSpellings = "federated, passkey, recovery_codes";

    /// <summary>
    /// The happy path: one federated credential and <b>two</b> passkeys, and every credential the
    /// account holds arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two passkeys rather than one, because the second registration appearing as a second entry is the
    /// "register a passkey and see it listed" half of what this endpoint is for — observed here rather
    /// than asserted in a test of its own, since a list that grew by one is the only observation that
    /// half admits of. One passkey would also leave "every passkey" and "a passkey" the same set, so a
    /// projection that took the first passkey it found would pass.
    /// </para>
    /// <para>
    /// Both types are asserted by count rather than the total alone: a read that returned the account's
    /// credentials as rows of one type — every one a passkey, or the federated row repeated through a
    /// join — has the right length and the wrong content, and a length on its own could not see it.
    /// </para>
    /// <para>
    /// This test is also the control for the two refusals below. A route that was never mapped refuses
    /// every caller and would satisfy both of them; a route mapped behind a policy nobody can clear
    /// satisfies both while the feature does not exist. This is the test that says the door opens for
    /// somebody.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_ForAnAuthenticatedOwner_ListTheFederatedCredentialAndEveryPasskey()
    {
        // Arrange — the sign-in harness seeds the whole account, which is what mints the federated
        // credential this list must also carry. Opened over a set of recovery codes, so the seeding files
        // no passkey of its own and the two below are the only two there are.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        // The container superuser, never the application role — the expected ids have to be read without
        // the endpoint's own owner predicate standing between the query and the rows.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> ownIds = await ListCredentialIdsAsync(admin, userId);

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert — the media type only, never the whole Content-Type header. The charset the framework
        // appends is a framework detail this feature makes no claim about.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");

        JsonArray entries = await ReadArrayAsync(response);

        // This used to read `entries.Count == 3`, and the number was only ever shorthand for "the ones I
        // arranged and no others" — which is what it now says outright, against the ids the account
        // really holds. The number had to go rather than move to 4: the arrangement's credential count is
        // a consequence of how the sign-in harness seeds, so a pin on it would go red the next time the
        // seeding changed shape and would say nothing about this endpoint. The comparison is strictly the
        // stronger claim — a dropped row, a duplicated row and a stranger's row are each named in the
        // failure message rather than showing up as a length that moved. This is the shape
        // Credentials_ForASecondAccount_ListThatAccountsCredentialsAndNotTheFirsts already had.
        await Assert.That(ArrivedIds(entries)).IsEqualTo(JoinIds(ownIds));

        // Unchanged, and deliberately so: opening the session over a set of recovery codes is what keeps
        // both of these true of the account this test arranged. One federated row, two passkeys.
        await Assert.That(CountOfType(entries, "federated")).IsEqualTo(1);
        await Assert.That(CountOfType(entries, "passkey")).IsEqualTo(2);
    }

    /// <summary>
    /// Two established accounts, each asking for itself — and neither is shown a credential of the
    /// other's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only thing in the codebase that would notice the owner predicate leaving this
    /// read.</b> <c>credentials</c> is exempt from row-level security, so with the predicate gone the
    /// query returns every credential in the table and each caller is handed the other account's
    /// sign-in inventory — how many passkeys a stranger holds, when each was registered, and the ids a
    /// revocation route addresses them by. Nothing beneath the application would refuse that query, and
    /// nothing above it would notice the answer.
    /// </para>
    /// <para>
    /// <b>Both directions, and the ordering of the arrangement is load-bearing rather than
    /// incidental.</b> Account A is established first, so an unfiltered read — or one that took
    /// <c>First()</c> — hands B the row that was written first, which is A's. Seed B first and the same
    /// broken read answers B correctly and the test passes for a reason that has nothing to do with the
    /// feature. A is asked too, because a read that had simply been made to take the <em>last</em> row
    /// would satisfy B alone.
    /// </para>
    /// <para>
    /// The two accounts hold different numbers of credentials on purpose — A has a passkey beside the
    /// federated row and the seeded set that B also has — so a read returning the whole table is visible
    /// in the count as well as in the ids. Each response is checked against its account's ids as a set and
    /// against the other's as raw text, the shape <c>SignedInUserEndpointTests</c> uses: an extra member
    /// carrying a stranger's identifier is a leak whether or not the member this test reads is correct.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_ForASecondAccount_ListThatAccountsCredentialsAndNotTheFirsts()
    {
        // Arrange — A whole first, so an unfiltered read hands B the row that was written first. Both
        // sessions open over a set of recovery codes, which writes no passkey beside the one A registers.
        await using PostgresTestHost host = await StartSignedInHostAsync();

        (HttpClient first, Guid firstUserId, _) = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        await RegisterPasskeyAsync(first, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        (HttpClient second, Guid secondUserId, _) = await host.Factory.CreateSignedInClientAsync(
            OtherSubject, opensWith: CredentialType.RecoveryCodes);

        // The container superuser, never the application role — the expected ids have to be read
        // without the very predicate under test standing between the query and the rows.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> firstIds = await ListCredentialIdsAsync(admin, firstUserId);
        IReadOnlyList<Guid> secondIds = await ListCredentialIdsAsync(admin, secondUserId);

        // The arrangement itself, or every assertion below is a claim about rows nothing wrote. This used
        // to read `firstIds.Count == 2` and `secondIds.Count == 1`; neither number was the claim, and both
        // were only there to say the two accounts hold different amounts, so that a read of the whole
        // table shows up in the length and not just in the ids. Said against each other, that argument
        // survives intact and stops depending on how many rows the sign-in harness happens to seed — which
        // is exactly the reason the absolute numbers had to go rather than be bumped to 3 and 2.
        await Assert.That(secondIds).IsNotEmpty();
        await Assert.That(firstIds.Count).IsNotEqualTo(secondIds.Count);

        // Act — B first, since it is the caller the ordering above was arranged to trap.
        HttpResponseMessage secondResponse = await second.GetAsync(CredentialsPath);
        HttpResponseMessage firstResponse = await first.GetAsync(CredentialsPath);

        // Assert
        await AssertListedExactlyAsync(secondResponse, secondIds, firstIds);
        await AssertListedExactlyAsync(firstResponse, firstIds, secondIds);
    }

    /// <summary>
    /// That every entry carries exactly <c>id</c>, <c>type</c> and <c>createdAtUtc</c>, and no fourth
    /// member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is green the day it is written, and that is the point rather than an apology.</b> Nothing
    /// else in either suite goes red when a fourth member starts arriving in these entries: the happy
    /// path counts types, the isolation test reads ids, and both keep passing beside a
    /// <c>subject</c>, a <c>lastUsedAtUtc</c> or a device label. The defect it exists to catch is one a
    /// later reader adds — a projection widened because the whole row was to hand — and a test that only
    /// went red once would have to be written after the widening had already shipped to a client.
    /// </para>
    /// <para>
    /// <b>Never <c>Contains("id")</c>, and that is the whole shape of the assertion.</b> A containment
    /// check over member names can never fail: every widening leaves the three expected names present
    /// and the assertion green. The members are joined and compared whole, exactly as
    /// <c>SignedInUserEndpointTests.Me_ResponseCarriesTheEmailAndNothingElse</c> does — and joined
    /// rather than counted for the reason that test gives, because a count says "3 != 4" and leaves the
    /// reader to work out which member arrived, while the joined string names it in the failure message.
    /// </para>
    /// <para>
    /// Every entry type is present, because a widening is as likely to land on one branch of a projection
    /// as on another, and a test reading a single entry would see only part of it. The account holds one
    /// credential of each declared type here: the federated row, the set the session was opened over, and
    /// the passkey the ceremony registers.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_EntryCarriesTheIdTheTypeAndTheDateAndNothingElse()
    {
        // Arrange — a federated credential, a set of recovery codes and a passkey, so every shape of
        // entry the endpoint can emit is inspected.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> ownIds = await ListCredentialIdsAsync(admin, userId);

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as an empty member list nobody would recognise as a 401.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);

        // This used to read `entries.Count == 2`, and the number's only job was to say that every entry
        // the account can produce was put in front of the shape assertion below — a list of one would
        // otherwise leave the other branches of the projection uninspected. Said against the account's own
        // ids, that is the same claim without a number in it, and a stronger one: it also names an entry
        // that arrived under an id this account does not own.
        await Assert.That(ArrivedIds(entries)).IsEqualTo(JoinIds(ownIds));

        // Each entry's members ordered before joining, so a fourth member produces the same message
        // whichever order the serializer emitted it in — a red that reads differently between runs is a
        // red people stop trusting.
        string[] shapes =
        [
            .. entries.Select(entry => string.Join(
                ", ",
                AsObject(entry).Select(member => member.Key).Order(StringComparer.Ordinal))),
        ];

        // This used to compare against `"{EntryMembers} | {EntryMembers}"` — the member list written down
        // once per entry, which made it a count of entries wearing the shape assertion's clothes. Reduced
        // to the distinct shapes, it says the thing it always meant: every entry carries exactly these
        // members, however many entries there are. It cannot pass on an empty list — nothing joins to the
        // empty string but nothing — and a widened branch still prints its own member list beside the
        // right one, which is the failure message the old form was chosen for.
        string distinctShapes = string.Join(
            " | ",
            shapes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        await Assert.That(distinctShapes).IsEqualTo(EntryMembers);
    }

    /// <summary>
    /// That the federated credential's provider <c>sub</c> appears nowhere in the response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one value on a <c>credentials</c> row that the API has no reason ever to hand back. It is the
    /// key every authenticated request is resolved on, and a client holding it holds the name of the
    /// principal rather than a fact about the account. The entry pin above would catch it arriving as its
    /// own member; this catches it arriving anywhere at all — folded into an <c>id</c>, appended to a
    /// <c>type</c>, or carried by a member some future widening added and the pin above then had to be
    /// updated for.
    /// </para>
    /// <para>
    /// <b>Asserted against the raw response text, as it went over the wire.</b> Re-rendering a parsed
    /// document with <c>ToJsonString</c> would put the check at the mercy of whichever characters the
    /// serializer's encoder happens to escape, and a subject hidden behind an escape sequence is a leak a
    /// search over the re-rendered text would report as absent. The status is asserted first so an empty
    /// 404 body cannot pass as a payload that does not contain the subject.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_NeverCarryTheFederatedSubject()
    {
        // Arrange — the account registers under a subject chosen so that finding it in the payload can
        // only mean the endpoint put it there.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync(DistinctiveSubject);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        // The subject really is what the credential stores, or the search below is looking for a value
        // that was never at risk of leaking in the first place.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await ResolveUserIdAsync(admin, DistinctiveSubject)).IsNotEqualTo(Guid.Empty);

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert — the status first, then the payload exactly as it arrived.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain(DistinctiveSubject);
    }

    /// <summary>
    /// That <c>type</c> is spelled <c>passkey</c>, <c>federated</c> and <c>recovery_codes</c> — the
    /// schema's own vocabulary — and never <c>Passkey</c>, <c>Federated</c> or <c>recoveryCodes</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is not a style preference; it is the default the application is configured with.</b>
    /// <c>Api/Program.cs</c> registers <c>JsonStringEnumConverter</c> with <b>no</b> naming policy, so an
    /// enum handed straight to the serializer arrives as <c>"Passkey"</c> — camel-cased nowhere and
    /// disagreeing with the <c>type</c> column, the check constraint that bounds it, and the export
    /// document. <c>PasskeyEndpoints</c> already meets this by camel-casing the member at the endpoint,
    /// and this follows that rather than inventing a third spelling.
    /// </para>
    /// <para>
    /// All three spellings are asserted together, ordered and joined, rather than one being read out of
    /// the first entry: they arrive from different branches of the same projection and a fix applied to
    /// one of them is exactly the half-fix this pins. <c>recovery_codes</c> is the branch that makes the
    /// pin bite — see the remarks on <see cref="SchemaTypeSpellings" /> — and until this account was
    /// signed in over a set, no entry here carried it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_TypeIsSpelledTheWaySchemaSpellsIt()
    {
        // Arrange — one credential of each declared type, since a spelling can only be checked where it
        // appears. Opening the session over a set of recovery codes is what puts the third one on the
        // wire: the harness's default would open it with a passkey and leave that branch unreachable.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, _, _) = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);
        string spellings = string.Join(
            ", ",
            entries
                .Select(entry => AsObject(entry)["type"]!.GetValue<string>())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        await Assert.That(spellings).IsEqualTo(SchemaTypeSpellings);
    }

    /// <summary>
    /// That the list ascends by the instant each credential was registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>createdAtUtc</c> ascending is the contract, and <c>id</c> is deliberately not promised as a
    /// tiebreaker.</b> <c>IExportReadService</c> states that rule for every collection in the export and
    /// gives the reason: <c>uuid</c> collation is provider-defined, so two rows sharing an instant may
    /// order one way here and another way under a second implementation with neither being wrong. So this
    /// test asserts the arriving instants are the same instants in ascending order and asserts nothing
    /// whatever about how a tie would break.
    /// </para>
    /// <para>
    /// <b>The arrangement is what makes an unordered read visible, and without it this test could not
    /// fail.</b> Credentials are inserted in the order they are created — the federated row and the seeded
    /// set first, then each passkey — so physical order and chronological order agree, and a read with
    /// no <c>ORDER BY</c> returns the right answer by accident. The federated row is therefore moved to
    /// the end of the heap before the act, by a <b>no-op</b> update on the superuser connection: PostgreSQL
    /// does not detect that the value is unchanged, so it writes a new tuple version at the end of the
    /// page and a sequential scan returns that row last. Nothing in the database is different afterwards
    /// — no value is rewritten, and in particular no instant is fabricated — only where the row sits. An
    /// unsorted read then answers set, passkey, passkey, federated, and the ascent fails.
    /// </para>
    /// <para>
    /// <b>The seeded rows share an instant, and that is checked rather than assumed.</b> The sign-in
    /// harness stamps everything it seeds with one fixed instant, so the federated credential and the set
    /// the session opens over carry the same <c>created_at_utc</c> — only the two passkeys, each written
    /// by a real HTTP round trip, have instants of their own. That is enough for the ascent to be
    /// falsifiable, since the row moved to the end of the heap is one of the earliest, but "enough" is not
    /// something a reader should have to work out: the arrangement asserts outright that the account's
    /// instants span more than one value, so a seeding change that collapsed them all onto one instant
    /// fails here instead of quietly making the ascent below vacuous.
    /// </para>
    /// <para>
    /// That shuffle rests on the read being a sequential scan, which it is at this size — a handful of
    /// rows on one page, where an index scan costs more than reading the page. If a future index made the
    /// planner choose otherwise the test would weaken to what a natural arrangement gives (it would still
    /// catch a descending read, or one ordered by <c>type</c>) rather than become wrong.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_AreOrderedByRegistrationInstant()
    {
        // Arrange — the seeded account, then two passkeys separated by real HTTP round trips.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(
            Subject, opensWith: CredentialType.RecoveryCodes);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> ownIds = await ListCredentialIdsAsync(admin, userId);
        IReadOnlyList<DateTimeOffset> ownInstants = await ListCredentialInstantsAsync(admin, userId);

        // The arrangement really spans more than one instant, or the ascent below holds of any read at
        // all. Read from the database rather than from the response, so a broken read fails as a broken
        // read further down and never as "the arrangement is degenerate".
        await Assert.That(ownInstants.First()).IsNotEqualTo(ownInstants.Last());

        // The chronologically first row, moved to the end of the heap so that "the order the rows happen
        // to be stored in" and "the order they were created in" stop agreeing. See the remarks.
        await MoveToTheEndOfTheHeapAsync(admin, await ResolveFederatedCredentialIdAsync(admin, Subject));

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);

        // This used to read `entries.Count == 3`. The number said "the rows I arranged and no others",
        // which is what the ids say outright — and say better, because a dropped row, a duplicated row and
        // a stranger's row each arrive named in the failure message. It could not simply become 4: the
        // arrangement's length now follows from how the sign-in harness seeds, and pinning that here would
        // redden for a change in the seeding rather than in the ordering this test is about.
        await Assert.That(ArrivedIds(entries)).IsEqualTo(JoinIds(ownIds));

        List<DateTimeOffset> arrived =
            [.. entries.Select(entry => AsObject(entry)["createdAtUtc"]!.GetValue<DateTimeOffset>())];

        // Compared as joined text rather than element by element, so the failure prints both sequences
        // whole and a reader can see which pair is out of order instead of only that one index differs.
        await Assert.That(Join(arrived)).IsEqualTo(Join([.. arrived.Order()]));

        // This pair used to read `arrived.Distinct().Count() == 3` and
        // `(await ListCredentialIdsAsync(...)).Count == 3`. Between them they claimed that the same rows
        // arrived and that the ascent above was not passing on a sequence of repeats — both by counting,
        // and the first of the two also happened to be false the moment two seeded rows shared an instant,
        // which is why bumping it to 4 was never available. Comparing the arriving instants against the
        // account's own instants in ascending order says both at once and neither by number: the arriving
        // sequence is exactly the instants the account holds, in order. Instants rather than ids on this
        // side on purpose — the endpoint promises no tiebreak between rows sharing one, and two sequences
        // of instants compare equal however a tie was broken.
        await Assert.That(Join(arrived)).IsEqualTo(Join(ownInstants));
    }

    /// <summary>
    /// An authenticated subject with no account behind it is refused, and the refusal writes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The row counts are the half of this test that carries the weight.</b> A status assertion cannot
    /// see the difference between a route that refuses and a route that mints an account and
    /// <em>then</em> refuses — and a provider id token stays valid for up to an hour after the account it
    /// names is erased, so a <c>ProvisionsUser</c> marker arriving on this group would turn one retried
    /// <c>GET /api/me/credentials</c> into a resurrected, passkey-less account that the re-authentication
    /// gate in front of erasure can never remove again.
    /// </para>
    /// <para>
    /// <b>Counted unscoped, on the superuser connection.</b> The id an accidental marker would mint is one
    /// no assertion here could name, so a count filtered to this subject would pass over the very row it
    /// exists to catch — and <c>users</c>, <c>budgets</c> and <c>sessions</c> are policed by
    /// <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed connection reports zero rows for a row
    /// that is still there exactly as it does for one that was never written.
    /// </para>
    /// <para>
    /// The title is asserted before the counts, because it is what makes them meaningful: it says the
    /// request reached the provisioning middleware and was refused there. An unmapped path answers 404 and
    /// leaves the same three empty tables behind, so the counts on their own prove nothing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_ForAnAuthenticatedSubjectWithNoAccount_IsRefusedAndCreatesNothing()
    {
        // Arrange — an authenticated client that has deliberately never called EstablishAccountAsync.
        // Nothing under /api/me mints anything, so this subject has a valid token and no account behind
        // it, which is exactly the state a token outliving an erasure leaves behind.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-listing-unprovisioned");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response))
            .IsEqualTo(UserProvisioningMiddleware.NoAccountTitle);

        await Assert.That(await ScalarAsync(admin, "select count(*) from users")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from credentials")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from budgets")).IsEqualTo(0L);
    }

    /// <summary>
    /// A caller carrying no token at all is refused by the fallback policy, and the title says it was the
    /// policy rather than the middleware.
    /// </summary>
    /// <remarks>
    /// Two refusals answer 401 on this route and the status cannot tell them apart: the fallback
    /// authorization policy turning an anonymous caller away before the route is reached, and the
    /// provisioning middleware finding no account for an authenticated principal. Only the second carries
    /// <see cref="UserProvisioningMiddleware.NoAccountTitle" />; the anonymous one is titled
    /// <c>"Unauthorized"</c> from the status code alone, because <c>UseStatusCodePages</c> writes it with
    /// no title of its own. Without the inequality, a route that had lost its authorization entirely still
    /// passes here — an anonymous request would walk on to the provisioning middleware, find no account
    /// for a principal it cannot even name, and be answered that middleware's 401. This is
    /// <c>SignedInUserEndpointTests</c>'s shape, for the reason it gives.
    /// </remarks>
    [Test]
    public async Task Credentials_WithoutAuthentication_IsRefusedWithUnauthorized()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act — no subject header, so nothing authenticates and the fallback policy decides. GetAsync
        // rather than GetStreamAsync: the latter throws on any non-2xx, so a route that answered 200 to an
        // anonymous caller would fail as a transport error rather than as the status assertion it is.
        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync(CredentialsPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response))
            .IsNotEqualTo(UserProvisioningMiddleware.NoAccountTitle);
    }

    /// <summary>
    /// Asserts both directions of one caller's answer: that the entries are exactly the credentials that
    /// account owns, and that no identifier of the other account appears in the body it was sent.
    /// </summary>
    /// <remarks>
    /// The body is read once as text and parsed from that text, because the negative half needs the
    /// payload exactly as it went over the wire — an id hidden behind an escape sequence is a leak a
    /// search over a re-rendered document would report as absent.
    /// </remarks>
    private static async Task AssertListedExactlyAsync(
        HttpResponseMessage response,
        IReadOnlyList<Guid> ownIds,
        IReadOnlyList<Guid> otherIds)
    {
        // The status first, so a body that is missing because the request failed reads as the failure it
        // is rather than as a parse error several lines further down.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        JsonArray entries = JsonNode.Parse(payload) as JsonArray
            ?? throw new InvalidOperationException("The endpoint answered something other than a JSON array.");

        // Sorted on both sides and joined whole, so the failure names the id that arrived rather than
        // reporting that a count moved.
        await Assert.That(ArrivedIds(entries)).IsEqualTo(JoinIds(ownIds));

        foreach (Guid otherId in otherIds)
        {
            await Assert.That(payload).DoesNotContain(Format(otherId));
        }
    }

    /// <summary>
    /// The <c>id</c> of every entry in a response body, sorted and joined — the arriving half of every
    /// "exactly these credentials and no others" comparison in this file.
    /// </summary>
    /// <remarks>
    /// <b>Sorted, so it is a set comparison and not an order one.</b> The endpoint orders by registration
    /// instant and promises no tiebreak between rows sharing one, so a caller that compared arriving ids
    /// in arrival order would be pinning a tie-break the contract deliberately leaves open — and
    /// <see cref="Credentials_AreOrderedByRegistrationInstant" /> is where the ordering claim belongs
    /// anyway. Joined rather than counted, so a failure names the id that arrived or went missing.
    /// </remarks>
    private static string ArrivedIds(JsonArray entries) =>
        JoinIds(entries.Select(entry => AsObject(entry)["id"]!.GetValue<Guid>()));

    /// <summary>The expected half of the same comparison, rendered the same way.</summary>
    private static string JoinIds(IEnumerable<Guid> ids) =>
        string.Join(", ", ids.Select(Format).Order(StringComparer.Ordinal));

    private static string Format(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Join(IEnumerable<DateTimeOffset> instants) =>
        string.Join(", ", instants.Select(instant => instant.ToString("O", CultureInfo.InvariantCulture)));

    private static int CountOfType(JsonArray entries, string type) =>
        entries.Count(entry =>
            string.Equals(AsObject(entry)["type"]!.GetValue<string>(), type, StringComparison.Ordinal));

    /// <summary>
    /// One array element as an object, refusing anything that is not one.
    /// </summary>
    /// <remarks>
    /// Checked rather than forgiven: an element that is a bare string or <see langword="null" /> would
    /// give an empty member list through a lenient read, and an empty list is what the entry pin reads as
    /// "the members are wrong" — a red nobody could interpret. Failing here says the entry stopped being
    /// an object at all.
    /// </remarks>
    private static JsonObject AsObject(JsonNode? entry) =>
        entry as JsonObject
        ?? throw new InvalidOperationException("A list entry was something other than a JSON object.");

    /// <summary>
    /// The response body as a JSON <b>array</b>, refusing anything that is not one.
    /// </summary>
    /// <remarks>
    /// The contract is an array rather than an envelope carrying one, and a lenient read would let an
    /// object with an <c>items</c> member pass as "no entries arrived" — which reads as a projection bug
    /// rather than as the shape change it is.
    /// </remarks>
    private static async Task<JsonArray> ReadArrayAsync(HttpResponseMessage response) =>
        await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()) as JsonArray
        ?? throw new InvalidOperationException("The endpoint answered something other than a JSON array.");

    /// <summary>
    /// The <c>title</c> of a problem-details body, which is the only member that says which of this
    /// route's refusals answered.
    /// </summary>
    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!["title"]!.GetValue<string>();

    /// <summary>
    /// Runs both authenticated legs of a registration, so the account really holds a passkey a signature
    /// answers to rather than material seeded out of band.
    /// </summary>
    /// <remarks>
    /// The account already exists when this runs, and there is now one way it got there:
    /// <see cref="ApiFactory.CreateSignedInClientAsync" /> seeds the whole account behind the client it
    /// hands out. Neither passkey leg provisions and neither does <c>/api/me/*</c>, so a client arriving
    /// here with no account is refused with a 401 for a reason no test in this file is about. Written out
    /// here rather than shared, because it is private to <c>CredentialRevocationTests</c> and that file
    /// makes the same choice for the same reason.
    /// </remarks>
    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        HttpResponseMessage options = await client.PostAsync(RegistrationOptionsPath, content: null);
        options.EnsureSuccessStatusCode();
        JsonNode issued = (await JsonNode.ParseAsync(await options.Content.ReadAsStreamAsync()))!;
        byte[] challenge = Base64UrlText.Decode(issued["challenge"]!.GetValue<string>());

        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);

        WrappedKeyFixture keys = WrappedKeyFixture.Mint();
        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedContentKey = keys.WrappedContentKey,
            wrappedIndexKey = keys.WrappedIndexKey,
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Reads back the user provisioning minted for <paramref name="subject" />. Nothing the API returns
    /// names it, so the lookup goes through the credential the middleware resolved on.
    /// </summary>
    private static async Task<Guid> ResolveUserIdAsync(NpgsqlConnection admin, string subject)
    {
        await using NpgsqlCommand command = new(
            "select user_id from credentials where provider = 'google' and subject = @subject",
            admin);
        command.Parameters.AddWithValue("subject", subject);

        return await command.ExecuteScalarAsync() switch
        {
            Guid userId => userId,
            var unexpected => throw new InvalidOperationException(
                $"Provisioning wrote no account for subject '{subject}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The <c>credentials.id</c> of the federated Google credential provisioning minted for
    /// <paramref name="subject" /> — the chronologically first row of the account, and therefore the one
    /// the ordering test moves.
    /// </summary>
    private static async Task<Guid> ResolveFederatedCredentialIdAsync(NpgsqlConnection admin, string subject)
    {
        await using NpgsqlCommand command = new(
            "select id from credentials where provider = 'google' and subject = @subject",
            admin);
        command.Parameters.AddWithValue("subject", subject);

        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"Provisioning wrote no federated credential for subject '{subject}', "
                + $"got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Every credential of one account, ascending by the instant it was registered, read on the container
    /// superuser so that the predicate under test is not what produced the expectation.
    /// </summary>
    /// <remarks>
    /// Ordered by <c>created_at_utc</c> alone and never with an id tiebreaker, because the endpoint
    /// promises none — see <see cref="Credentials_AreOrderedByRegistrationInstant" />. Callers use the
    /// result as a set.
    /// </remarks>
    private static async Task<IReadOnlyList<Guid>> ListCredentialIdsAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select id from credentials where user_id = @userId order by created_at_utc",
            admin);
        command.Parameters.AddWithValue("userId", userId);

        List<Guid> ids = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>
    /// The <c>created_at_utc</c> of every credential of one account, ascending, read on the container
    /// superuser so that the endpoint's own ordering is not what produced the expectation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instants alone, without the ids they belong to, and that is deliberate rather than lazy. Two rows
    /// may share an instant — the sign-in harness stamps everything it seeds with one — and the endpoint
    /// promises nothing about how such a tie breaks, so a sequence of <c>(id, instant)</c> pairs would
    /// pin a tie-break that neither side owes the other. A sequence of instants compares equal whichever
    /// way both sides happened to order the tied rows, which is exactly the strength the contract has.
    /// </para>
    /// <para>
    /// Read as <see cref="DateTimeOffset" /> so that it renders through <see cref="Join" /> identically to
    /// the value the wire carries. <c>timestamptz</c> comes back at zero offset, and the endpoint's
    /// <c>createdAtUtc</c> is a UTC <see cref="DateTime" /> the serializer writes with a <c>Z</c>, so the
    /// two parse to the same offset and the comparison is between instants rather than between renderings.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<DateTimeOffset>> ListCredentialInstantsAsync(
        NpgsqlConnection admin,
        Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select created_at_utc from credentials where user_id = @userId order by created_at_utc",
            admin);
        command.Parameters.AddWithValue("userId", userId);

        List<DateTimeOffset> instants = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            instants.Add(reader.GetFieldValue<DateTimeOffset>(0));
        }

        return instants;
    }

    /// <summary>
    /// Rewrites one row to its own current value, which moves it to the end of the heap without changing
    /// anything it holds.
    /// </summary>
    /// <remarks>
    /// PostgreSQL does not detect a no-op update: it writes a new tuple version and leaves the old one
    /// dead, so a sequential scan returns this row <b>last</b> rather than in the position it was inserted
    /// at. That is the entire purpose — it separates "the order rows are stored in" from "the order they
    /// were created in", which credentials otherwise never do. On the superuser connection, because the
    /// application role deliberately holds no <c>UPDATE</c> grant on this table at all.
    /// </remarks>
    private static async Task MoveToTheEndOfTheHeapAsync(NpgsqlConnection admin, Guid credentialId)
    {
        await using NpgsqlCommand command = new(
            "update credentials set created_at_utc = created_at_utc where id = @id",
            admin);
        command.Parameters.AddWithValue("id", credentialId);

        // One row, or the shuffle silently did nothing and the ordering test measures the accidental
        // agreement it exists to break.
        await Assert.That(await command.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the query
        // changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{sql}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// A host on the provider-bearer path, which is now the two refusals and nothing else.
    /// </summary>
    /// <remarks>
    /// Its remaining callers are <see cref="Credentials_ForAnAuthenticatedSubjectWithNoAccount_IsRefusedAndCreatesNothing" />
    /// and <see cref="Credentials_WithoutAuthentication_IsRefusedWithUnauthorized" />, and they stay here
    /// because what each of them asserts <em>is</em> how a request proves who is asking — an authenticated
    /// principal with no account behind it, and no principal at all. They go with the bearer path in the
    /// commit that removes provisioning, not before.
    /// </remarks>
    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every test here
    /// that needs an account authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    /// <remarks>
    /// The counts that used to keep four tests off this helper are gone: each of them now compares the
    /// entries against the account's own rows instead of against a number, so a seeded credential can no
    /// longer move an expectation. What still cannot move is <c>federated == 1</c> beside
    /// <c>passkey == 2</c>, and what keeps those still is the <c>opensWith</c> every arrangement here
    /// passes — a set of recovery codes opens the same full session while writing no passkey. See the
    /// remarks on the class.
    /// </remarks>
    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
