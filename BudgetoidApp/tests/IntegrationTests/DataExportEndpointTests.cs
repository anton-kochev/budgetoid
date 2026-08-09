using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// That a signed-in account can ask for a copy of its own data and be answered, and that the two
/// callers who are not that account are turned away without anything being written for them.
/// </summary>
/// <remarks>
/// <para>
/// Driven through the real HTTP pipeline rather than the handler, because what is measured here is
/// not the document: it is which requests reach one. The route carries no authorization metadata of
/// its own — the application's fallback policy is what authenticates it — and no
/// <c>ProvisionsUserAttribute</c>, so both refusals below are properties of the pipeline the route
/// was mapped into rather than of anything written inside the endpoint.
/// </para>
/// <para>
/// <see cref="Export_ForAnAuthenticatedOwner_RespondsWithApplicationJson" /> is the control for both
/// refusals, and without it they are worth nothing: a route that refused every caller — one that was
/// never mapped at all, answering 404 — satisfies neither status assertion, but a route mapped behind
/// a policy nobody can clear satisfies both while the feature does not exist. The happy path is what
/// says the door opens for somebody.
/// </para>
/// <para>
/// The two 401s are asserted to be <em>distinguishable</em> rather than merely both 401. Three
/// refusals reach this route — nothing authenticated, an authenticated token naming an account that
/// does not exist, and the claim gates above it — and only the second carries
/// <see cref="UserProvisioningMiddleware.NoAccountTitle" />. The anonymous one is titled
/// <c>"Unauthorized"</c> by <c>ProblemDetailsDefaults</c>, from the status code alone, because
/// <c>UseStatusCodePages</c> writes it with no title of its own. Without the title asserted, a route
/// that had lost its authorization entirely would still pass the unprovisioned case on the 401 the
/// middleware answers, and a route that had lost the middleware would still pass the anonymous case.
/// </para>
/// <para>
/// <see cref="Export_CarriesSchemaVersionOne" /> sits with them rather than with the completeness
/// tests, because it is a claim about the envelope and not about anything inside it: it needs no
/// furnished budget, and it is the one member of the document a caller reads before deciding whether
/// it can read the rest.
/// </para>
/// <para>
/// The row count in
/// <see cref="Export_ForAnAuthenticatedSubjectWithNoAccount_IsRefusedAndCreatesNothing" /> is the
/// half that carries the weight. A status assertion cannot see the difference between a route that
/// refuses and a route that mints an account and <em>then</em> refuses — and a provider id token
/// stays valid for up to an hour after the account it names has been erased, so a marker added to
/// this group would turn one retried export into a resurrected, passkey-less account. Read on the
/// container superuser, never on the application role: <c>user_isolation</c> is <c>FOR ALL</c>, so a
/// policed connection reports zero rows for a row that is still there exactly as it does for one that
/// is gone, and the assertion could not fail.
/// </para>
/// </remarks>
public sealed class DataExportEndpointTests
{
    private const string ExportPath = "/api/me/export";

    [Test]
    public async Task Export_ForAnAuthenticatedOwner_RespondsWithApplicationJson()
    {
        // Arrange — an established account and nothing furnished inside it. The bare case is the one
        // that fails if the route is simply missing, which is the whole job of a control.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await ApiFactory.EstablishAccountAsync(client);

        // Act
        HttpResponseMessage response = await client.GetAsync(ExportPath);

        // Assert — the media type only, never the whole Content-Type header. The charset the framework
        // appends is a framework detail this feature makes no claim about, and pinning the full string
        // would go red on a framework change that altered nothing anyone can observe.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
    }

    [Test]
    public async Task Export_WithoutAuthentication_IsRefusedWithUnauthorized()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act — no subject header, so nothing authenticates and the fallback policy decides. GetAsync
        // rather than GetStreamAsync: the latter throws on any non-2xx, so a route that answered 200 to
        // an anonymous caller would fail this test as a transport error rather than as the status
        // assertion it is.
        HttpResponseMessage response = await host.Factory.CreateClient().GetAsync(ExportPath);

        // Assert — and that this refusal is not the middleware's. The endpoint declares no
        // authorization metadata of its own, so this is the test that would notice an AllowAnonymous
        // added to the group.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response))
            .IsNotEqualTo(UserProvisioningMiddleware.NoAccountTitle);
    }

    [Test]
    public async Task Export_ForAnAuthenticatedSubjectWithNoAccount_IsRefusedAndCreatesNothing()
    {
        // Arrange — an authenticated client that has deliberately never called EstablishAccountAsync.
        // /api/me/* mints nothing, so this subject has a valid token and no account behind it, which is
        // exactly the state a token outliving an erasure leaves behind.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient("google-unprovisioned");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        HttpResponseMessage response = await client.GetAsync(ExportPath);

        // Assert — the title first, because it is what makes the count below meaningful: it says the
        // request reached the provisioning middleware and was refused there, rather than never having
        // matched a route at all. An unmapped path answers 404 and leaves an empty users table too.
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
    /// That the document says which shape it is written in, and that the answer is <c>1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The literal, never <c>ExportDocument.CurrentSchemaVersion</c>. A test that reads the constant
    /// agrees with whatever the production code currently says, so it stays green through a version
    /// bump — which is the single change it exists to notice. Asserting the number by hand is what
    /// makes raising the version a decision somebody takes deliberately, with this line as the place
    /// the new value has to be written down.
    /// </para>
    /// <para>
    /// It is green the day it is written, and that is the point: a pin rather than a driver. A saved
    /// file outlives the deployment that wrote it, and the version is the only thing a reader has to
    /// tell which shape they are holding — so the value has to be fixed by something other than the
    /// code that emits it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Export_CarriesSchemaVersionOne()
    {
        // Arrange — an established account and nothing furnished inside it. The version is a property
        // of the document rather than of its contents, so the bare case is the honest one.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient();
        await ApiFactory.EstablishAccountAsync(client);

        // Act
        HttpResponseMessage response = await client.GetAsync(ExportPath);

        // Assert — read as an integer rather than as text, because the wire type is part of the claim:
        // a version rendered as "1" would satisfy a string comparison while breaking every reader that
        // expects a number.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonNode document = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))
            ?? throw new InvalidOperationException("The export answered an empty body.");
        await Assert.That(document["schemaVersion"]!.GetValue<int>()).IsEqualTo(1);
    }

    /// <summary>
    /// That the response tells the browser to save the document, and names the file after the instant
    /// the request was served, in UTC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asserted as the whole header, character for character, rather than through
    /// <c>Contains</c>.</b> Two halves of this string break silently and neither shows up in a
    /// substring check for the filename. Drop <c>attachment</c> and the browser renders the JSON in a
    /// tab instead of saving it, which is a feature that looks like it works right up until somebody
    /// tries to keep the file. Drop the quotes and the header is still parsed today, but the first
    /// filename to carry a character the grammar reserves — a space, a semicolon — truncates at it,
    /// and nothing about the day that happens points back here. An exact comparison is the only one
    /// that holds both.
    /// </para>
    /// <para>
    /// The instant comes from a <see cref="FakeTimeProvider" /> substituted through
    /// <see cref="ApiFactory" />'s <c>configureServices</c> hook, which is applied last and therefore
    /// replaces the <c>TimeProvider.System</c> singleton the application registers. A test that let the
    /// clock run would have to assert a window or re-format the current time on its own, and both of
    /// those pass against a filename built from a value nobody chose.
    /// </para>
    /// <para>
    /// <see cref="Export_UnderANonGregorianCultureIsStillNamedInTheGregorianCalendar" /> carries a
    /// different instant on purpose. Between the two, the filename is shown to track the clock rather
    /// than being a constant somebody wrote into the endpoint — a single instant cannot tell those
    /// apart.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Export_NamesTheFileWithTheRequestInstantInUtc()
    {
        // Arrange — the clock fixed before the host is built, so provisioning and the export read the
        // same instant and the filename can be written down here in full.
        await using PostgresTestHost host = await StartHostAsync();
        await using ApiFactory factory = host.CreateFactory(
            configureServices: services => services.Replace(
                ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider(RequestInstant))));
        HttpClient client = factory.CreateAuthenticatedClient();
        await ApiFactory.EstablishAccountAsync(client);

        // Act
        HttpResponseMessage response = await client.GetAsync(ExportPath);

        // Assert — the success first, so a header that is absent because the request failed reads as
        // the failure it is rather than as a missing header.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ContentDispositionOf(response))
            .IsEqualTo("attachment; filename=\"budgetoid-export-20260808T131415Z.json\"");
    }

    /// <summary>
    /// That the instant in the filename is rendered in the Gregorian calendar whatever calendar the
    /// serving thread's culture happens to use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This control can fail, and it was checked that it can.</b> A <c>DateTime</c> formatted with
    /// <c>yyyyMMdd'T'HHmmss'Z'</c> under <c>th-TH</c> renders the year in the Buddhist calendar — the
    /// instant below comes back as <c>25691231T235959Z</c> rather than <c>20261231T235959Z</c>, a
    /// filename 543 years wrong that every other assertion in this file passes over. So the format
    /// call has to name <see cref="CultureInfo.InvariantCulture" />, and this is the test that says so.
    /// A server whose culture is set by its host — a container image with a locale, a machine
    /// configured for the person running it — is not an exotic deployment.
    /// </para>
    /// <para>
    /// <b><c>PreserveExecutionContext</c> is what makes it a control rather than a decoration, and it
    /// has to be set before the client is built.</b> <c>CultureInfo.CurrentCulture</c> is an
    /// <c>AsyncLocal</c>, and <c>TestServer</c> suppresses execution-context flow by default: measured
    /// against a probe middleware, a culture set in the test body reached the request pipeline as
    /// <c>en-US</c> while the test thread held <c>th-TH</c>. Without the line below this test would set
    /// a culture the endpoint never sees and pass no matter how the filename is formatted. The flag is
    /// read when the handler is created, so setting it after <c>CreateAuthenticatedClient</c> is too
    /// late — that ordering was measured too.
    /// </para>
    /// <para>
    /// Nothing process-wide is touched: <c>CultureInfo.DefaultThreadCurrentCulture</c> would have done
    /// the job in one line and would have reached every other test running beside this one, in a suite
    /// with no parallelism cap. An <c>AsyncLocal</c> assignment is confined to this test's own flow.
    /// </para>
    /// <para>
    /// <b>The fake clock's local zone is moved off UTC, and that is what makes the second half of the
    /// filename claim measurable at all.</b> <see cref="FakeTimeProvider" /> reports UTC as its local
    /// zone unless told otherwise, and <c>GetUtcNow()</c> always carries offset zero — so with the
    /// default zone <c>UtcDateTime</c> and <c>DateTime</c> are the same value, and an endpoint reading
    /// the local clock, or formatting without projecting to UTC, would leave both filename tests green.
    /// <c>Pacific/Kiritimati</c> is UTC+14, the largest offset in the database, and against the instant
    /// below it lands in the next day <em>and</em> the next year: an endpoint that read local time and
    /// formatted it without the UTC projection would name the file <c>20270101T135959Z</c> — a
    /// timestamp that looks entirely plausible and is a year wrong.
    /// </para>
    /// <para>
    /// <b>The conjunction in that sentence is exact, and it was measured.</b> Swapping the endpoint's
    /// <c>GetUtcNow()</c> for <c>GetLocalNow()</c> on its own changes no filename anywhere, in this zone
    /// or any other: the two name the same instant, and the formatter projects with
    /// <c>DateTimeOffset.UtcDateTime</c> before rendering, which undoes the offset. What this test
    /// catches is the pair — a local read <em>and</em> a formatter that renders the offset it was handed
    /// — and under the default UTC zone that pair is invisible too, which is the whole reason the zone
    /// is set. Both halves were checked by modelling each mutation in the value the endpoint reads.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Export_UnderANonGregorianCultureIsStillNamedInTheGregorianCalendar()
    {
        // Arrange — the same substitution, a different instant, and the execution context allowed to
        // flow so the culture set below reaches the endpoint at all. The clock's local zone is UTC+14,
        // which puts local time a day and a year ahead of the instant it reports: without that, local
        // and UTC are the same value on a FakeTimeProvider and the "in UTC" half of the claim is
        // unmeasurable.
        await using PostgresTestHost host = await StartHostAsync();
        FakeTimeProvider clock = new(NewYearsEveInstant);
        clock.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById(FurthestAheadTimeZoneId));

        await using ApiFactory factory = host.CreateFactory(
            configureServices: services => services.Replace(
                ServiceDescriptor.Singleton<TimeProvider>(clock)));
        factory.Server.PreserveExecutionContext = true;
        HttpClient client = factory.CreateAuthenticatedClient();
        await ApiFactory.EstablishAccountAsync(client);

        // A calendar whose year is 543 ahead of the Gregorian one, so a format call that reads the
        // ambient culture produces a filename no reader could date.
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");

        // Act
        HttpResponseMessage response = await client.GetAsync(ExportPath);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ContentDispositionOf(response))
            .IsEqualTo("attachment; filename=\"budgetoid-export-20261231T235959Z.json\"");
    }

    /// <summary>
    /// The instant <see cref="Export_NamesTheFileWithTheRequestInstantInUtc" /> serves its request at —
    /// 2026-08-08 13:14:15 UTC.
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeKind.Utc" /> is load-bearing rather than tidy: the same provider is what
    /// provisioning stamps its rows with, and PostgreSQL's <c>timestamptz</c> rejects a
    /// <see cref="DateTime" /> of any other kind outright.
    /// </remarks>
    private static readonly DateTimeOffset RequestInstant =
        new(new DateTime(2026, 8, 8, 13, 14, 15, DateTimeKind.Utc));

    /// <summary>
    /// A second instant, deliberately the last second of a year: the value the Buddhist calendar
    /// renders furthest from the Gregorian one, and — paired with
    /// <see cref="FurthestAheadTimeZoneId" /> as the clock's local zone — the one that puts local time
    /// in a different day and a different year from the instant itself.
    /// </summary>
    private static readonly DateTimeOffset NewYearsEveInstant =
        new(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc));

    /// <summary>
    /// UTC+14, the largest standard offset in the time-zone database, used as the fake clock's local
    /// zone so that "local" and "UTC" cannot be the same value.
    /// </summary>
    /// <remarks>
    /// An IANA id rather than a Windows one: the suite runs on Linux and macOS, and .NET resolves IANA
    /// ids on Windows too. Kiritimati keeps no daylight saving, so the offset is the same whatever the
    /// date — a zone that shifted would make the expected filename depend on which half of the year the
    /// instant fell in.
    /// </remarks>
    private const string FurthestAheadTimeZoneId = "Pacific/Kiritimati";

    /// <summary>
    /// The one <c>Content-Disposition</c> the response carries, refusing zero and refusing more than
    /// one.
    /// </summary>
    /// <remarks>
    /// Read off the content headers rather than the response headers, which is where the framework
    /// puts it, and returned as the raw string so the assertion compares what a client receives rather
    /// than a value re-rendered from a parsed <c>ContentDispositionHeaderValue</c>. Refusing a second
    /// value matters as much as refusing none: two dispositions on one response is a header written
    /// twice, and a client reading the first would save a file this test never looked at.
    /// </remarks>
    private static string ContentDispositionOf(HttpResponseMessage response)
    {
        if (!response.Content.Headers.TryGetValues("Content-Disposition", out IEnumerable<string>? values))
        {
            throw new InvalidOperationException("The export carried no Content-Disposition header.");
        }

        return values.SingleOrDefault()
            ?? throw new InvalidOperationException(
                "The export carried more than one Content-Disposition header.");
    }

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
