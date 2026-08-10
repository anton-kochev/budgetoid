using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
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
/// No test here names a production type. They address the route over HTTP and read the wire body, so
/// while the endpoint is unmapped they fail on the status assertion against a real 404 rather than
/// failing to compile — which is the difference between a red test that is telling us something and one
/// that is telling us nothing.
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
    /// The two spellings of <c>type</c>, as the schema itself spells them, joined ordinally.
    /// </summary>
    private const string SchemaTypeSpellings = "federated, passkey";

    /// <summary>
    /// The happy path: one federated credential and <b>two</b> passkeys, and all three arrive.
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
    /// Both types are asserted by count rather than the total alone: a read that returned three
    /// credentials of one type — three passkeys, or the federated row three times through a join — has
    /// the right length and the wrong content, and the length on its own could not see it.
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
        // Arrange — registering a passkey establishes the account first, which is what mints the
        // federated credential this list must also carry. Nothing under /api/me provisions anything.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert — the media type only, never the whole Content-Type header. The charset the framework
        // appends is a framework detail this feature makes no claim about.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");

        JsonArray entries = await ReadArrayAsync(response);
        await Assert.That(entries.Count).IsEqualTo(3);

        // Counted per type, so three rows of the wrong kind cannot pass as the right three.
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
    /// The two accounts hold different numbers of credentials on purpose — A has a passkey beside its
    /// federated row and B has only its federated row — so a read returning the whole table is visible in
    /// the count as well as in the ids. Each response is checked against its account's ids as a set and
    /// against the other's as raw text, the shape <c>SignedInUserEndpointTests</c> uses: an extra member
    /// carrying a stranger's identifier is a leak whether or not the member this test reads is correct.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_ForASecondAccount_ListThatAccountsCredentialsAndNotTheFirsts()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        HttpClient first = host.Factory.CreateAuthenticatedClient(Subject);
        await RegisterPasskeyAsync(first, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        HttpClient second = host.Factory.CreateAuthenticatedClient(OtherSubject);
        await ApiFactory.EstablishAccountAsync(second);

        // The container superuser, never the application role — the expected ids have to be read
        // without the very predicate under test standing between the query and the rows.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyList<Guid> firstIds = await ListCredentialIdsAsync(admin, await ResolveUserIdAsync(admin, Subject));
        IReadOnlyList<Guid> secondIds =
            await ListCredentialIdsAsync(admin, await ResolveUserIdAsync(admin, OtherSubject));

        // The arrangement itself, or every assertion below is a claim about rows nothing wrote.
        await Assert.That(firstIds.Count).IsEqualTo(2);
        await Assert.That(secondIds.Count).IsEqualTo(1);

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
    /// Both entry types are present, because a widening is as likely to land on the passkey branch of a
    /// projection as on the federated one, and a test reading one entry would see only half of it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_EntryCarriesTheIdTheTypeAndTheDateAndNothingElse()
    {
        // Arrange — one federated credential and one passkey, so both shapes of entry are inspected.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as an empty member list nobody would recognise as a 401.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);
        await Assert.That(entries.Count).IsEqualTo(2);

        // Each entry's members ordered before joining, so a fourth member produces the same message
        // whichever order the serializer emitted it in — a red that reads differently between runs is a
        // red people stop trusting.
        string shapes = string.Join(
            " | ",
            entries.Select(entry => string.Join(
                ", ",
                AsObject(entry).Select(member => member.Key).Order(StringComparer.Ordinal))));

        await Assert.That(shapes).IsEqualTo($"{EntryMembers} | {EntryMembers}");
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
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(DistinctiveSubject);
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
    /// That <c>type</c> is spelled <c>passkey</c> and <c>federated</c> — the schema's own vocabulary —
    /// and never <c>Passkey</c> or <c>Federated</c>.
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
    /// Both spellings are asserted together, ordered and joined, rather than one being read out of the
    /// first entry: the two arrive from different branches of the same projection and a fix applied to
    /// one of them is exactly the half-fix this pins.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_TypeIsSpelledTheWaySchemaSpellsIt()
    {
        // Arrange — one credential of each type, since a spelling can only be checked where it appears.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
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
    /// fail.</b> Credentials are inserted in the order they are created — the federated row at
    /// provisioning, then each passkey — so physical order and chronological order agree, and a read with
    /// no <c>ORDER BY</c> returns the right answer by accident. The federated row is therefore moved to
    /// the end of the heap before the act, by a <b>no-op</b> update on the superuser connection: PostgreSQL
    /// does not detect that the value is unchanged, so it writes a new tuple version at the end of the
    /// page and a sequential scan returns that row last. Nothing in the database is different afterwards
    /// — no value is rewritten, and in particular no instant is fabricated — only where the row sits. An
    /// unsorted read then answers passkey, passkey, federated, and the ascent fails.
    /// </para>
    /// <para>
    /// That shuffle rests on the read being a sequential scan, which it is at this size — four rows on one
    /// page, where an index scan costs more than reading the page. If a future index made the planner
    /// choose otherwise the test would weaken to what a natural arrangement gives (it would still catch a
    /// descending read, or one ordered by <c>type</c>) rather than become wrong.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_AreOrderedByRegistrationInstant()
    {
        // Arrange — three credentials with three distinct instants: the federated row first, then two
        // passkeys, each separated by a real HTTP round trip.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await ResolveUserIdAsync(admin, Subject);

        // The chronologically first row, moved to the end of the heap so that "the order the rows happen
        // to be stored in" and "the order they were created in" stop agreeing. See the remarks.
        await MoveToTheEndOfTheHeapAsync(admin, await ResolveFederatedCredentialIdAsync(admin, Subject));

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);
        await Assert.That(entries.Count).IsEqualTo(3);

        List<DateTimeOffset> arrived =
            [.. entries.Select(entry => AsObject(entry)["createdAtUtc"]!.GetValue<DateTimeOffset>())];

        // Compared as joined text rather than element by element, so the failure prints both sequences
        // whole and a reader can see which pair is out of order instead of only that one index differs.
        await Assert.That(Join(arrived)).IsEqualTo(Join([.. arrived.Order()]));

        // And the same three rows arrived, so a read that "ordered" by dropping one — or by returning a
        // row twice — cannot pass the ascent above.
        await Assert.That(arrived.Distinct().Count()).IsEqualTo(3);
        await Assert.That((await ListCredentialIdsAsync(admin, userId)).Count).IsEqualTo(3);
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
        string arrived = string.Join(
            ", ",
            entries.Select(entry => AsObject(entry)["id"]!.GetValue<Guid>()).Select(Format).Order(StringComparer.Ordinal));
        string expected = string.Join(", ", ownIds.Select(Format).Order(StringComparer.Ordinal));
        await Assert.That(arrived).IsEqualTo(expected);

        foreach (Guid otherId in otherIds)
        {
            await Assert.That(payload).DoesNotContain(Format(otherId));
        }
    }

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
    /// The account is established first, on the one route group allowed to mint one. Neither passkey leg
    /// provisions and neither does <c>/api/me/*</c>, so without that line the very first request is
    /// refused with a 401 and every test here would be red for a reason it is not about. Written out here
    /// rather than shared, because it is private to <c>CredentialRevocationTests</c> and that file makes
    /// the same choice for the same reason.
    /// </remarks>
    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        await ApiFactory.EstablishAccountAsync(client);

        HttpResponseMessage options = await client.PostAsync(RegistrationOptionsPath, content: null);
        options.EnsureSuccessStatusCode();
        JsonNode issued = (await JsonNode.ParseAsync(await options.Content.ReadAsStreamAsync()))!;
        byte[] challenge = Base64UrlText.Decode(issued["challenge"]!.GetValue<string>());

        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);

        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
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

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
