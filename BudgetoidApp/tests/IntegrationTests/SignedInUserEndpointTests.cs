using System.Net;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// That a signed-in account can ask who it is and be told its own email address, and that the answer
/// is the caller's address rather than some other account's.
/// </summary>
/// <remarks>
/// <para>
/// Driven through the real HTTP pipeline rather than the handler, because half of what is measured
/// here is which account the request arrives as. The address is not a parameter the caller passes; it
/// is resolved from the authenticated subject by the middleware above the route, and a handler tested
/// in isolation would be handed the identity these tests exist to check the pipeline produces.
/// </para>
/// <para>
/// <see cref="Me_ForASecondAccount_RespondsWithThatAccountsAddressAndNotTheFirsts" /> is the control
/// for <see cref="Me_ForAnAuthenticatedOwner_RespondsWithTheAccountsEmailAddress" />, and without it
/// the happy path is worth very little: a handler that returns a hardcoded string — or reads the
/// first row of <c>users</c> — satisfies the single-account test perfectly, because with one account
/// established every wrong answer and the right one are the same value. It takes a second account to
/// tell a resolved address apart from a constant.
/// </para>
/// <para>
/// The happy path is in turn the control for the refusals a later trip adds — anonymous 401,
/// authenticated-with-no-account 401. A route that was never mapped at all refuses every caller and
/// would satisfy neither refusal, but a route mapped behind a policy nobody can clear satisfies both
/// while the feature does not exist. This test is what says the door opens for somebody.
/// </para>
/// <para>
/// Neither test names a production type. They address the route over HTTP and read the wire body, so
/// while the endpoint is missing they fail on the status assertion against a real 404 rather than
/// failing to compile — which is the difference between a red test that is telling us something and
/// one that is telling us nothing.
/// </para>
/// <para>
/// The two refusals arrived next, and they are asserted to be <em>distinguishable</em> rather than
/// merely both 401. Three refusals reach this route — nothing authenticated, an authenticated token
/// naming an account that does not exist, and the claim gates above it — and only the second carries
/// <see cref="UserProvisioningMiddleware.NoAccountTitle" />. The anonymous one is titled
/// <c>"Unauthorized"</c> by <c>ProblemDetailsDefaults</c>, from the status code alone, because
/// <c>UseStatusCodePages</c> writes it with no title of its own. Without the title asserted, a route
/// that had lost its authorization entirely would still pass the unprovisioned case on the 401 the
/// middleware answers, and a route that had lost the middleware would still pass the anonymous case.
/// This is <c>DataExportEndpointTests</c>'s shape, for the reason it gives.
/// </para>
/// <para>
/// The row count in
/// <see cref="Me_ForAnAuthenticatedSubjectWithNoAccount_IsRefusedAndCreatesNothing" /> is the half of
/// that test which carries the weight. A status assertion cannot see the difference between a route
/// that refuses and a route that mints an account and <em>then</em> refuses — and a provider id token
/// stays valid for up to an hour after the account it names has been erased, so a
/// <c>ProvisionsUserAttribute</c> added to this group would turn one retried <c>GET /api/me</c> into a
/// resurrected, passkey-less account.
/// </para>
/// <para>
/// <see cref="Me_ResponseCarriesTheEmailAndNothingElse" /> is a <b>pin</b> and is green the day it is
/// written, which is the point rather than an apology. Nothing else in either suite goes red when an
/// <c>id</c> or a <c>createdAtUtc</c> starts arriving in this response: the happy-path test reads the
/// <c>email</c> member and would keep passing beside a second one, and the two-account test only
/// refuses a member carrying the <em>other</em> account's address. The defect it exists to catch is one
/// a later reader adds — a handler widened to return the whole row because the shape was to hand — and
/// a test that only went red once would have to be written after the id had already shipped to a
/// client.
/// </para>
/// </remarks>
public sealed class SignedInUserEndpointTests
{
    private const string MePath = "/api/me";

    [Test]
    public async Task Me_ForAnAuthenticatedOwner_RespondsWithTheAccountsEmailAddress()
    {
        // Arrange — the address the client authenticates with is written down here rather than left to
        // the factory's default, so the assertion below compares against a value this test chose. A
        // test that re-derived the expected address the same way the client did would agree with
        // itself no matter which account answered.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-owner", OwnerAddress);

        // Required, not ceremony: nothing under /api/me provisions an account, so a client that
        // skipped this has a valid token and no account row behind it — which is a refusal case, not
        // this one.
        await ApiFactory.EstablishAccountAsync(client);

        // Act
        HttpResponseMessage response = await client.GetAsync(MePath);

        // Assert — the media type only, never the whole Content-Type header. The charset the framework
        // appends is a framework detail this feature makes no claim about, and pinning the full string
        // would go red on a framework change that altered nothing anyone can observe.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");

        JsonNode document = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))
            ?? throw new InvalidOperationException("The endpoint answered an empty body.");
        await Assert.That(document["email"]!.GetValue<string>()).IsEqualTo(OwnerAddress);
    }

    /// <summary>
    /// That two established accounts each receive their own address, and that neither is shown the
    /// other's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The negative half is the half that carries the weight. "Mine is present" passes against a
    /// handler that reads the first row of <c>users</c> whenever the caller happens to be that row,
    /// and it passes against a document that answers with everyone's address at once — a query that
    /// forgot its owner filter and serialized the lot. Asserting that the other account's address does
    /// not appear <em>anywhere in the payload</em>, rather than merely that the <c>email</c> member
    /// holds the right value, is what refuses the second of those: an extra member carrying a stranger's
    /// address is a leak whether or not the member this test reads is correct.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Me_ForASecondAccount_RespondsWithThatAccountsAddressAndNotTheFirsts()
    {
        // Arrange — account A is established first and account B second, and that ordering is
        // load-bearing rather than incidental. An unfiltered read, or one that takes First(), returns
        // whichever row was written first: with A ahead of B, B asking for itself is answered with A's
        // address and the test goes red. Seed B first and the same broken handler answers B correctly,
        // and the test passes for a reason that has nothing to do with the feature.
        await using PostgresTestHost host = await StartHostAsync();

        HttpClient first = host.Factory.CreateAuthenticatedClient("google-first", FirstAddress);
        await ApiFactory.EstablishAccountAsync(first);

        HttpClient second = host.Factory.CreateAuthenticatedClient("google-second", SecondAddress);
        await ApiFactory.EstablishAccountAsync(second);

        // Act — both callers ask, because one of them alone cannot distinguish a resolved address from
        // a constant. Asking as A as well as B is also what would catch a handler that had simply been
        // made to return the *last* row instead of the first.
        HttpResponseMessage secondResponse = await second.GetAsync(MePath);
        HttpResponseMessage firstResponse = await first.GetAsync(MePath);

        // Assert — B first, since it is the caller the ordering above was arranged to trap.
        await AssertAnsweredWithAsync(secondResponse, SecondAddress, FirstAddress);
        await AssertAnsweredWithAsync(firstResponse, FirstAddress, SecondAddress);
    }

    [Test]
    public async Task Me_WithoutAuthentication_IsRefusedWithUnauthorized()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act — no subject header, so nothing authenticates and the fallback policy decides. GetAsync
        // rather than GetStreamAsync: the latter throws on any non-2xx, so a route that answered 200 to
        // an anonymous caller would fail this test as a transport error rather than as the status
        // assertion it is.
        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync(MePath);

        // Assert — and that this refusal is not the middleware's. Without the title inequality, a route
        // that had lost its authorization entirely still passes here: an anonymous request would reach
        // the provisioning middleware, find no account for a principal it cannot even name, and be
        // answered the middleware's own 401. The status alone cannot tell that apart from the fallback
        // policy turning the caller away before the route was ever reached, and only one of those two
        // means the door is shut.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response))
            .IsNotEqualTo(UserProvisioningMiddleware.NoAccountTitle);
    }

    [Test]
    public async Task Me_ForAnAuthenticatedSubjectWithNoAccount_IsRefusedAndCreatesNothing()
    {
        // Arrange — an authenticated client that has deliberately never called EstablishAccountAsync.
        // Nothing under /api/me mints anything, so this subject has a valid token and no account behind
        // it, which is exactly the state a token outliving an erasure leaves behind.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-unprovisioned");

        // The container superuser, never the application role. users is policed by user_isolation,
        // which is FOR ALL, so a policed connection reports zero rows for a row that is still there
        // exactly as it does for one that is gone — every count below would pass whatever the endpoint
        // had written, and the assertions could not fail.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        HttpResponseMessage response = await client.GetAsync(MePath);

        // Assert — the title first, because it is what makes the counts below meaningful: it says the
        // request reached the provisioning middleware and was refused there, rather than never having
        // matched a route at all. An unmapped path answers 404 and leaves an empty users table too, so
        // the counts on their own prove nothing.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response))
            .IsEqualTo(UserProvisioningMiddleware.NoAccountTitle);

        // Unscoped, not "no row for this subject". The id an accidental ProvisionsUserAttribute would
        // mint is one no assertion here could name, so a scoped count would pass over the very row it
        // exists to catch.
        await Assert.That(await ScalarAsync(admin, "select count(*) from users")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from credentials")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from budgets")).IsEqualTo(0L);
    }

    /// <summary>
    /// That the response object carries exactly one member, <c>email</c>, and no second one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never <c>Contains("email")</c>, and that is the whole point of the shape.</b> A containment
    /// check over the member names is a test that can never fail: every widening of the response — an
    /// internal <c>id</c>, a <c>createdAtUtc</c>, whatever the next row projection brings — leaves
    /// <c>email</c> present and the assertion green. The members are joined and compared whole, the
    /// shape <c>DataMinimizationSchemaTests.Schema_PinsTheColumnsOfTheUserRow</c> uses on the table
    /// this response is read out of.
    /// </para>
    /// <para>
    /// Joined rather than counted for the same reason it is there: a count goes red on the second
    /// member too, but it says "1 != 2" and leaves the reader to work out which member arrived. The
    /// joined string names it in the failure message.
    /// </para>
    /// <para>
    /// Why it matters that an id stays out is argued on <c>SignedInUser</c> itself: every tenancy value
    /// in this API is resolved server-side from the authenticated subject and none is ever addressed by
    /// the client, and publishing an id that nothing displays is the first half of a client-supplied
    /// tenancy parameter.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Me_ResponseCarriesTheEmailAndNothingElse()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await ApiFactory.EstablishAccountAsync(client);

        // Act
        HttpResponseMessage response = await client.GetAsync(MePath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as an empty member list, which is a shape this test would otherwise
        // report as "no members arrived" and nobody would read as a 401.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonObject document = await ReadObjectAsync(response);

        // Ordered before joining, so a second member produces the same message whichever order the
        // serializer emitted it in — a red that reads differently between runs is a red people stop
        // trusting.
        string members = string.Join(
            ", ",
            document.Select(member => member.Key).Order(StringComparer.Ordinal));
        await Assert.That(members).IsEqualTo("email");
    }

    /// <summary>
    /// The address <see cref="Me_ForAnAuthenticatedOwner_RespondsWithTheAccountsEmailAddress" />
    /// authenticates with. Deliberately not the <c>{subject}@example.com</c> shape the factory falls
    /// back to, so an endpoint that rebuilt the address from the subject instead of reading the stored
    /// account would be visible here rather than agreeing by construction.
    /// </summary>
    private const string OwnerAddress = "owner@budgetoid.test";

    /// <summary>The address of the account established first, and the one a <c>First()</c> returns.</summary>
    private const string FirstAddress = "first-account@budgetoid.test";

    /// <summary>The address of the account established second, which no unfiltered read reaches.</summary>
    private const string SecondAddress = "second-account@budgetoid.test";

    /// <summary>
    /// Asserts both directions of one caller's answer: that its own address is the value of
    /// <c>email</c>, and that the other account's address appears nowhere in the body it was sent.
    /// </summary>
    /// <remarks>
    /// The body is read once, as text, and parsed from that text rather than from the response stream —
    /// the negative half needs the payload exactly as it went over the wire. Re-rendering a parsed
    /// document with <c>ToJsonString</c> would put the check at the mercy of whichever characters the
    /// serializer's encoder happens to escape, and an address hidden behind an escape sequence is a leak
    /// a <c>Contains</c> over the re-rendered text would report as absent.
    /// </remarks>
    private static async Task AssertAnsweredWithAsync(
        HttpResponseMessage response,
        string ownAddress,
        string otherAddress)
    {
        // The status first, so a body that is missing because the request failed reads as the failure it
        // is rather than as a parse error several lines further down.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string payload = await response.Content.ReadAsStringAsync();
        JsonNode document = JsonNode.Parse(payload)
            ?? throw new InvalidOperationException("The endpoint answered an empty body.");

        await Assert.That(document["email"]!.GetValue<string>()).IsEqualTo(ownAddress);
        await Assert.That(payload).DoesNotContain(otherAddress);
    }

    /// <summary>
    /// The response body as a JSON object, refusing anything that is not one.
    /// </summary>
    /// <remarks>
    /// The cast is checked rather than forgiven: a response that answered an array, a bare string or
    /// <c>null</c> would give an empty member list through a lenient read, and an empty list is what the
    /// pin above reads as "one member, and it is not email" — a red nobody could interpret. Failing here
    /// says the response stopped being an object at all.
    /// </remarks>
    private static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response) =>
        await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()) as JsonObject
        ?? throw new InvalidOperationException("The endpoint answered something other than a JSON object.");

    /// <summary>
    /// The <c>title</c> of a problem-details body, which is the only member that says which of this
    /// route's refusals answered.
    /// </summary>
    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!["title"]!.GetValue<string>();

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
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
