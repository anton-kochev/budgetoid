using System.Globalization;
using System.Net;
using Api.Infrastructure;
using Application.Passkeys.BeginAssertion;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// That the four browser-facing security headers go on <b>every</b> response this application
/// produces, whichever part of the pipeline produced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every response is the whole requirement, and it is the part a header test usually misses.</b> A
/// header written by the route's own delegate, or by a filter that only runs on the way out of a
/// matched endpoint, decorates the successes and leaves three other kinds of response bare: the one an
/// <c>IExceptionHandler</c> writes, the one the authentication challenge writes, and the one a
/// middleware that never called <c>next</c> writes. Each of those is a response a browser renders, and
/// two of them are the ones an attacker is trying to provoke. So the file is organised by <em>which
/// part of the pipeline wrote the response</em> rather than by which header is being checked.
/// </para>
/// <para>
/// <b>The exception-handler case is first because it is the one a wrong <em>write technique</em> breaks
/// silently.</b> ASP.NET Core's exception-handler middleware calls <c>HttpResponse.Clear()</c> — headers
/// included — before it hands the exception to the registered handlers, so four headers written straight
/// onto the response before <c>await next(...)</c> are discarded and never reach the wire, wherever the
/// middleware writing them is registered. Writing them from a <c>Response.OnStarting</c> callback is what
/// survives, because the callback fires at flush, after any such clear. No comment in
/// <c>SecurityHeadersMiddleware</c> can hold that; only a request that actually throws can. What this case
/// does <em>not</em> pin is the registration's position — measured, all four headers ship on a 500 with the
/// middleware registered either side of <c>UseExceptionHandler()</c>. The position is pinned by
/// <see cref="ARefusedNonFirstPartyRequest_CarriesTheHeaders" />, against a middleware that answers
/// without calling <c>next</c> at all.
/// </para>
/// <para>
/// <b>There is deliberately no <c>X-Frame-Options</c> assertion.</b> Every browser that can reach this
/// API honours <c>frame-ancestors</c>, and a browser that honours both ignores <c>X-Frame-Options</c>,
/// so the older header would be a second spelling of one rule — able to disagree with the first and
/// unable to change any outcome. <see cref="Framing_IsForbidden" /> is what holds that rule instead.
/// </para>
/// <para>
/// <b>The header names a response is read by come from <see cref="SecurityHeadersMiddleware.Headers" />,
/// not from a copy.</b> The dictionary is the wire contract; a test that wrote its own copy of the names
/// would go on passing against a middleware that had stopped emitting one of them under a different name.
/// The <em>values</em> are written out, once, in <see cref="ExpectedHeaderLines" />, where they are the
/// <em>answer</em> rather than the input. Two names are written out as well — <see cref="HstsHeader" />
/// and <see cref="ContentSecurityPolicyHeader" /> — and each says in its own remarks why the test reading
/// it must not be able to agree with the dictionary about which header carries the rule.
/// </para>
/// <para>
/// Every host here runs in <c>Production</c> against a connection string nothing here ever opens, the way
/// <see cref="CorsTests" /> does: Production skips the Development-only startup migration, and none of
/// the six paths below reaches a handler that opens a connection. A header test that needed a database
/// would be a header test nobody runs.
/// </para>
/// </remarks>
public sealed class SecurityHeaderTests
{
    private const string HealthPath = "/health";
    private const string MePath = "/api/me";
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";

    /// <summary>
    /// The <c>Strict-Transport-Security</c> header name as a literal, deliberately not read off
    /// <see cref="SecurityHeadersMiddleware.Headers" />.
    /// </summary>
    /// <remarks>
    /// <see cref="HstsMaxAge_IsAtLeastOneYear" /> must not be able to agree with the dictionary about
    /// anything: not about the value, which it measures against the floor the requirement names rather
    /// than against what ships, and not about which header carries the directive. See its own remarks.
    /// <see cref="ContentSecurityPolicyHeader" /> is written out for the second half of that reason
    /// only — the policy's exact bytes are pinned against the dictionary elsewhere.
    /// </remarks>
    private const string HstsHeader = "Strict-Transport-Security";

    /// <summary>
    /// The <c>Content-Security-Policy</c> header name as a literal, deliberately not read off
    /// <see cref="SecurityHeadersMiddleware.Headers" />.
    /// </summary>
    /// <remarks>
    /// Same argument as <see cref="HstsHeader" />: <see cref="Framing_IsForbidden" /> is named after a
    /// requirement, and a rename in the dictionary must fail it rather than redirect it to whichever
    /// header the dictionary now calls the policy.
    /// </remarks>
    private const string ContentSecurityPolicyHeader = "Content-Security-Policy";

    /// <summary>
    /// One year in seconds — the floor the requirement names, not the value that ships.
    /// </summary>
    private const long OneYearInSeconds = 31_536_000;

    /// <summary>
    /// The four headers exactly as they go on the wire, projected into ordinal-sorted
    /// <c>name: value</c> lines. Compared as one string rather than header by header so a failure prints
    /// the whole set and names the item that drifted, instead of stopping at the first mismatch.
    /// </summary>
    private const string ExpectedHeaderLines =
        "Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none', "
        + "Referrer-Policy: no-referrer, "
        + "Strict-Transport-Security: max-age=63072000; includeSubDomains, "
        + "X-Content-Type-Options: nosniff";

    /// <summary>
    /// That a response written by an <see cref="Microsoft.AspNetCore.Diagnostics.IExceptionHandler" />
    /// still carries the headers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The most important test in the file, and what it discriminates is the write technique.</b>
    /// ASP.NET Core's exception-handler middleware calls <c>HttpResponse.Clear()</c> before invoking the
    /// handlers, which resets the status and every header already on the response. A middleware writing
    /// the four headers straight onto <c>Response.Headers</c> before <c>await next(...)</c> therefore
    /// writes four headers that are thrown away — on exactly the responses whose contents nobody chose —
    /// and no registration position repairs that. Writing them from a <c>Response.OnStarting</c> callback
    /// does, because the callback fires at flush, after the clear. Nothing else in this file can see the
    /// mistake: the other five paths write their response without ever clearing it.
    /// </para>
    /// <para>
    /// <b>The lever.</b> <c>BeginAssertionHandler</c>'s registration is replaced with one that throws on
    /// resolve, and the request is <c>POST /api/passkeys/assertion/options</c> — an anonymous route, so
    /// authorization decides nothing and no database is touched, and the
    /// throw happens while the minimal API delegate is resolving its arguments, which is inside the
    /// exception handler's reach. <c>GlobalExceptionHandler</c> is registered last and claims anything no
    /// earlier handler wants, which is what makes the status 500.
    /// </para>
    /// <para>
    /// <b>The status assertion is not decoration.</b> Without it, a 403 from the first-party control or a
    /// 404 from a renamed route would leave a green test that never reached an exception handler at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AResponseFromAnExceptionHandler_CarriesTheHeaders()
    {
        // Arrange — the handler the anonymous route resolves is replaced by one that cannot be
        // constructed. Registered through the factory's own seam, which runs last, so this wins over the
        // application's registration without either side knowing about the other.
        await using ApiFactory factory = CreateFactory(services =>
            services.AddScoped<BeginAssertionHandler>(_ =>
                throw new InvalidOperationException(
                    "Arranged failure: this handler exists only to reach the global exception handler.")));

        // Act
        HttpResponseMessage response = await factory.CreateClient()
            .SendAsync(new HttpRequestMessage(HttpMethod.Post, AssertionOptionsPath));

        // Assert — the status first, so a green bar cannot mean "some other refusal also carries them".
        // Smallest production change that turns this red: writing the four headers straight onto
        // Response.Headers before `await next(...)` instead of from the Response.OnStarting callback.
        // Moving the registration below app.UseExceptionHandler() does not — measured: Clear() resets the
        // status and the headers but not the OnStarting callback list, so all four still ship on the 500.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That(HeaderLines(response)).IsEqualTo(ExpectedHeaderLines);
    }

    /// <summary>
    /// That an ordinary successful response carries the set — the whole set, and nothing renamed or
    /// respelled.
    /// </summary>
    /// <remarks>
    /// <b>The count assertion catches one narrow mutation the string comparison cannot — and it is not an
    /// emptied dictionary on its own.</b> <see cref="ExpectedHeaderLines" /> is a non-empty <c>const</c>,
    /// so a <see cref="SecurityHeadersMiddleware.Headers" /> emptied to nothing projects to <c>""</c> and
    /// fails the equality first; the count never gets a say. What it catches is the commit that empties
    /// the dictionary <em>and</em> rewrites <see cref="ExpectedHeaderLines" /> to match it — the equality
    /// then compares <c>""</c> against <c>""</c> and goes green while the application emits no header at
    /// all. That is the same cannot-be-silenced-by-the-same-edit argument
    /// <see cref="HstsMaxAge_IsAtLeastOneYear" /> makes, in a weaker form: the third line of that same
    /// edit is <c>4</c> to <c>0</c> here, and then this guard is silenced too. It costs one line and
    /// raises the price of the mistake; it does not make the mistake impossible, and no assertion reading
    /// the same dictionary the middleware writes from could.
    /// </remarks>
    [Test]
    public async Task EveryResponse_CarriesTheHeaderSetExactly()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();

        // Act
        HttpResponseMessage response = await factory.CreateClient()
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, HealthPath));

        // Assert — smallest production change that turns this red: removing one entry from
        // SecurityHeadersMiddleware.Headers, or altering one byte of one value.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(HeaderLines(response)).IsEqualTo(ExpectedHeaderLines);
        await Assert.That(SecurityHeadersMiddleware.Headers.Count).IsEqualTo(4);
    }

    /// <summary>
    /// That the <c>max-age</c> a response actually carries is at least one year.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not redundant with <see cref="EveryResponse_CarriesTheHeaderSetExactly" />.</b>
    /// That test pins the exact bytes, which means it is silenced by the same commit that breaks the
    /// rule: shorten <c>max-age</c> to five minutes, update the literal to match — the diff looks like a
    /// value change and a test kept in step, and the suite is green. This test cannot be silenced that
    /// way, because it asserts the <em>floor the requirement names</em> rather than the value that
    /// ships, and no edit that lowers the value can also lower the floor without saying out loud that
    /// it is lowering the floor.
    /// </para>
    /// <para>
    /// <b>It parses rather than string-matches</b>, for the same reason. <c>Contains("63072000")</c>
    /// would pass against <c>max-age=630728</c> reformatted, against a value nested in another
    /// directive, and against the number appearing anywhere else in the header; and a shortened value
    /// would be a one-character edit to the test. Parsing the directive is the only form of this
    /// assertion that measures the quantity the requirement is about.
    /// </para>
    /// <para>
    /// The header name is this file's own literal rather than a key from the dictionary, so a rename
    /// there cannot make this test read a header nobody promised. An absent header fails on the parse.
    /// </para>
    /// </remarks>
    [Test]
    public async Task HstsMaxAge_IsAtLeastOneYear()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();

        // Act
        HttpResponseMessage response = await factory.CreateClient()
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, HealthPath));
        long? maxAge = ParseMaxAge(HeaderValue(response, HstsHeader));

        // Assert — smallest production change that turns this red: shortening the max-age directive
        // below one year, however it is spelled.
        await Assert.That(maxAge).IsNotNull();
        await Assert.That(maxAge!.Value).IsGreaterThanOrEqualTo(OneYearInSeconds);
    }

    /// <summary>
    /// That the headers survive the authentication challenge — the response nobody's route delegate
    /// wrote.
    /// </summary>
    /// <remarks>
    /// A plain client sends no <c>X-Test-Subject</c>, so <c>TestAuthHandler</c> returns
    /// <c>NoResult()</c>, authorization challenges, and the 401 is written on the way back out with no
    /// endpoint having run and no database touched. This is the "set on the 200s, dropped on the 401s"
    /// case, and it is the response an unauthenticated browser sees most often.
    /// </remarks>
    [Test]
    public async Task AnUnauthenticatedRequest_CarriesTheHeaders()
    {
        // Arrange — no subject header, so nothing authenticates.
        await using ApiFactory factory = CreateFactory();

        // Act
        HttpResponseMessage response = await factory.CreateClient()
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, MePath));

        // Assert — smallest production change that turns this red: gating the write on the outgoing
        // status, e.g. writing the headers only when Response.StatusCode is 200.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(HeaderLines(response)).IsEqualTo(ExpectedHeaderLines);
    }

    /// <summary>
    /// That a response written by a middleware which never calls <c>next</c> carries the headers too.
    /// </summary>
    /// <remarks>
    /// The third distinct writer, and the one that constrains <em>ordering</em>: the first-party control
    /// refuses without invoking the rest of the pipeline, so anything registered below it never runs on
    /// this request. The header removal mirrors <see cref="FirstPartyRequestTests" /> and is not
    /// defensive noise — <see cref="ApiFactory" /> adds the header to every client it hands out, so a
    /// test that did not remove it would quietly measure a 200 instead of the 403 it is named for.
    /// </remarks>
    [Test]
    public async Task ARefusedNonFirstPartyRequest_CarriesTheHeaders()
    {
        // Arrange — the one header the CSRF control requires, taken back off.
        await using ApiFactory factory = CreateFactory();
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove(FirstPartyRequestTests.ClientHeader);

        // Act
        HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, MePath));

        // Assert — smallest production change that turns this red: registering
        // app.UseMiddleware<SecurityHeadersMiddleware>() below app.UseMiddleware<FirstPartyRequestMiddleware>()
        // instead of above it.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(HeaderLines(response)).IsEqualTo(ExpectedHeaderLines);
    }

    /// <summary>
    /// That nothing may frame this API's responses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately separate from <see cref="EveryResponse_CarriesTheHeaderSetExactly" />, where the
    /// directive is one clause inside a literal that reads as a formatting detail. Clickjacking is a
    /// named requirement, and a future edit that "modernises" the policy — collapsing it to a
    /// <c>default-src</c>, or relaxing it so an embed can be demonstrated — should have to argue with a
    /// test named after the requirement rather than with a string somebody may assume is stale.
    /// Collapsing it is the concrete hazard: <c>frame-ancestors</c> has no <c>default-src</c> fallback,
    /// so a policy that folded it into one would permit framing while still reading as locked down.
    /// </para>
    /// <para>
    /// <b>It parses the directive out and compares the whole source list, rather than searching the
    /// policy for a substring.</b> <c>Contains("frame-ancestors 'none'")</c> passes unchanged against
    /// <c>frame-ancestors 'none' https://evil.example</c> — and widening is half of what this test is
    /// named for, so the substring form gave that half away. Comparing the source list is what fails on
    /// an added source; a directive that is absent altogether parses to <see langword="null" /> and
    /// fails the same assertion. The comparison is ordinal, so a respelled <c>'NONE'</c> fails too even
    /// though the grammar calls it the same policy: the value ships as a literal in one dictionary whose
    /// exact bytes <see cref="EveryResponse_CarriesTheHeaderSetExactly" /> already pins, so this makes
    /// nothing brittle that was not pinned already. The rest of the policy stays that test's job.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Framing_IsForbidden()
    {
        // Arrange
        await using ApiFactory factory = CreateFactory();

        // Act
        HttpResponseMessage response = await factory.CreateClient()
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, HealthPath));
        string? policy = HeaderValue(response, ContentSecurityPolicyHeader);
        string? frameAncestors = DirectiveSources(policy, "frame-ancestors");

        // Assert — smallest production change that turns this red: dropping the frame-ancestors
        // directive from the Content-Security-Policy value, or widening it past 'none' by adding a
        // source to it.
        await Assert.That(policy).IsNotNull();
        await Assert.That(frameAncestors).IsEqualTo("'none'");
    }

    /// <summary>
    /// The headers this response carries, for the names the middleware promises, as ordinal-sorted
    /// <c>name: value</c> lines.
    /// </summary>
    /// <remarks>
    /// A missing header renders as a name with an empty value rather than disappearing from the
    /// projection, so the comparison fails naming the header that went missing instead of silently
    /// comparing a shorter list.
    /// </remarks>
    private static string HeaderLines(HttpResponseMessage response) =>
        string.Join(
            ", ",
            SecurityHeadersMiddleware.Headers.Keys
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => $"{name}: {HeaderValue(response, name)}"));

    /// <summary>
    /// One header's value, wherever <see cref="HttpClient" /> filed it.
    /// </summary>
    /// <remarks>
    /// Both collections are searched because <see cref="HttpClient" /> splits a response's headers into
    /// message headers and content headers by a fixed internal list, and which side a given name lands
    /// on is a property of that list rather than of anything this application does. A helper that read
    /// only one of them would make a test's outcome depend on a classification nobody here controls.
    /// </remarks>
    private static string? HeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out IEnumerable<string>? values))
        {
            return string.Join(", ", values);
        }

        return response.Content.Headers.TryGetValues(name, out IEnumerable<string>? contentValues)
            ? string.Join(", ", contentValues)
            : null;
    }

    /// <summary>
    /// One Content-Security-Policy directive's source list, its tokens rejoined with single spaces, or
    /// <see langword="null" /> when the policy is absent or names no such directive.
    /// </summary>
    /// <remarks>
    /// The directive name is matched as a whole token rather than by prefix, so a future
    /// <c>frame-ancestors-something</c> cannot answer for <c>frame-ancestors</c>, and it is matched
    /// case-insensitively because a directive name is ASCII case-insensitive in the grammar — the
    /// <em>sources</em> are then the caller's to compare however strictly it means to. They are rejoined
    /// rather than returned as a list so a failure prints the whole list on one line and the reader sees
    /// what was added next to what was expected.
    /// </remarks>
    private static string? DirectiveSources(string? policy, string directiveName)
    {
        if (policy is null)
        {
            return null;
        }

        foreach (string directive in policy.Split(
                     ';',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] tokens = directive.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens is [var name, ..] && name.Equals(directiveName, StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(' ', tokens.Skip(1));
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>max-age</c> directive's value in seconds, or <see langword="null" /> if the header is
    /// absent or carries no parsable one.
    /// </summary>
    /// <remarks>
    /// <see cref="NumberStyles.None" /> and the invariant culture, so a sign, a thousands separator or
    /// surrounding whitespace is a parse failure rather than a number this test would accept on a
    /// header no browser would.
    /// </remarks>
    private static long? ParseMaxAge(string? headerValue)
    {
        if (headerValue is null)
        {
            return null;
        }

        foreach (string directive in headerValue.Split(';', StringSplitOptions.TrimEntries))
        {
            if (!directive.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return long.TryParse(
                directive["max-age=".Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long seconds)
                ? seconds
                : null;
        }

        return null;
    }

    /// <summary>
    /// A host that needs no database: Production skips the Development-only startup migration, and no
    /// path exercised in this file reaches a handler that opens a connection. Copied from
    /// <see cref="CorsTests" />, which needs the same thing for the same reason.
    /// </summary>
    private static ApiFactory CreateFactory(Action<IServiceCollection>? configureServices = null) =>
        new(
            "Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres",
            environment: "Production",
            configureServices: configureServices);
}
