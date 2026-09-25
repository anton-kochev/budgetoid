using Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The <c>Set-Cookie</c> header this application writes, attribute by attribute, and the claim that the
/// header which takes the cookie away matches the one that put it there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every attribute here is enforced by the browser and by nothing in this codebase.</b> A cookie
/// named with the <c>__Host-</c> prefix is refused outright unless it is <c>Secure</c>, has
/// <c>Path=/</c> and carries no <c>Domain</c> — and that refusal happens in the user agent, silently,
/// with nothing in either log naming the cause. There is no server-side assertion that could catch it
/// and no integration test that would: the test client does not enforce the prefix, so a cookie this
/// application emits with a <c>Domain</c> would go on working perfectly in
/// <see cref="SessionCookieAuthenticationTests" /> while never being stored by a real browser at all.
/// This file is the only place that rule is checked.
/// </para>
/// <para>
/// <b><see cref="Clear_AndIssue_AgreeOnEveryAttributeABrowserMatchesOn" /> is the test this file exists
/// for.</b> A browser identifies a stored cookie by name, <c>Path</c> and <c>Domain</c>; a
/// <c>Set-Cookie</c> that clears while disagreeing on any of the three stores a <em>second</em> cookie
/// and keeps the first. The symptom is not an error anywhere — sign-out answers 204, the session row is
/// really gone, and the client goes on presenting a dead handle and showing a signed-in shell to
/// whoever is at the keyboard. That is invisible to every other test in this suite.
/// </para>
/// <para>
/// <b>The cookie name is written out as a literal here, not read off
/// <see cref="SessionCookie.Name" />.</b> It is wire contract: a browser sends the bytes, not the
/// symbol, so a test reading the constant agrees with whatever the constant says and stays green
/// through a rename that signs out every account at once. The production remarks ask for exactly this.
/// </para>
/// <para>
/// <b>Why these live in the integration project although they open no connection.</b> The subject is an
/// <c>Api</c> type, and <c>UnitTests</c> deliberately does not reference <c>Api</c> — the argument
/// <see cref="CurrentUserWriterTests" /> spells out and <c>ProjectReferenceGraphTests</c> enforces.
/// </para>
/// </remarks>
public sealed class SessionCookieTests
{
    /// <summary>
    /// The name the cookie is written under. See the remarks on the class for why this is a literal.
    /// </summary>
    private const string CookieName = "__Host-budgetoid-session";

    /// <summary>The expiry the issuing tests hand in — whole seconds, which is all the header carries.</summary>
    private static readonly DateTime Expiry = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    [Test]
    public async Task Issue_WritesTheCookieUnderTheHostPrefixedName()
    {
        // Arrange
        string value = Base64UrlText.Encode(TokenBytes(0x11));

        // Act
        SetCookieHeaderValue issued = SetCookieHeaderValue.Parse(IssuedHeader(value, Expiry));

        // Assert — the whole name, not a prefix check. "__Host-" is what the browser enforces the other
        // three attributes through, and the rest is what every client already holding a cookie sends.
        await Assert.That(issued.Name.ToString()).IsEqualTo(CookieName);
    }

    /// <summary>
    /// The four attributes the <c>__Host-</c> prefix makes mandatory, plus the one it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Secure</c>, <c>Path=/</c> and the absence of <c>Domain</c> are the prefix's own conditions:
    /// fail any one and the browser drops the cookie rather than storing a slightly weaker one.
    /// <c>HttpOnly</c> is not one of them and is asserted for its own reason — it is what keeps a
    /// script on the page from reading the handle.
    /// </para>
    /// <para>
    /// <c>SameSite=Lax</c> rather than <c>Strict</c>, and this test is not asserting a CSRF defence: Lax
    /// is what lets somebody following a link into the application arrive already signed in, and what
    /// refuses a cross-site request is <c>FirstPartyRequestMiddleware</c>, which
    /// <see cref="FirstPartyRequestTests" /> owns. Tightening this to <c>Strict</c> would not make the
    /// application safer; it would answer that first navigation signed out.
    /// </para>
    /// <para>
    /// <c>Domain</c> is asserted against the raw header rather than the parsed value, because absence is
    /// the claim: a parser reports an unset attribute and an attribute it failed to understand the same
    /// way, and the second of those would still reach the browser.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Issue_WritesTheAttributesTheHostPrefixRequires()
    {
        // Arrange
        string value = Base64UrlText.Encode(TokenBytes(0x11));

        // Act
        string raw = IssuedHeader(value, Expiry);
        SetCookieHeaderValue issued = SetCookieHeaderValue.Parse(raw);

        // Assert
        await Assert.That(issued.HttpOnly).IsTrue();
        await Assert.That(issued.Secure).IsTrue();
        // Qualified, because Microsoft.AspNetCore.Http declares a SameSiteMode of its own with a
        // different member order — the two are not interchangeable and an unqualified name here would
        // compare a parsed header attribute against a CookieOptions value.
        await Assert.That(issued.SameSite).IsEqualTo(Microsoft.Net.Http.Headers.SameSiteMode.Lax);
        await Assert.That(issued.Path.ToString()).IsEqualTo("/");
        await Assert.That(raw.Contains("domain", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    /// <summary>
    /// That the cookie dies with the session rather than on an interval of its own, and that nothing
    /// about it slides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different expiries through the same call, because a single one is satisfied by any
    /// implementation that happens to compute the same instant — "the session's expiry" and "now plus
    /// the configured lifetime" are the same value on the request that opens a session and diverge on
    /// every one after it.
    /// </para>
    /// <para>
    /// <b>The absent <c>Max-Age</c> is the sliding half.</b> Extending a session on each request would
    /// mean a <c>GRANT UPDATE (expires_at_utc)</c> on <c>sessions</c> — a column the grant matrix leaves
    /// out of its list precisely so a lifetime cannot be edited — and a row written on every read of
    /// every page. A cookie carrying a relative lifetime alongside the absolute one is where that would
    /// start, and <c>Max-Age</c> wins over <c>Expires</c> in every browser that understands it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Issue_ExpiresWithTheSessionAndNeverSlides()
    {
        // Arrange
        string value = Base64UrlText.Encode(TokenBytes(0x11));
        DateTime later = Expiry.AddDays(30);

        // Act
        string rawFirst = IssuedHeader(value, Expiry);
        string rawSecond = IssuedHeader(value, later);

        // Assert
        await Assert.That(SetCookieHeaderValue.Parse(rawFirst).Expires)
            .IsEqualTo(new DateTimeOffset(Expiry));
        await Assert.That(SetCookieHeaderValue.Parse(rawSecond).Expires)
            .IsEqualTo(new DateTimeOffset(later));
        await Assert.That(rawFirst.Contains("max-age", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    /// <summary>
    /// That the handle rides out on the wire exactly as it was handed in.
    /// </summary>
    /// <remarks>
    /// The two characters base64url uses and base64 does not — <c>-</c> and <c>_</c> — are the whole
    /// point of the arrangement. Both are unreserved in a URL and neither needs escaping, but a cookie
    /// writer that escaped anyway would emit <c>%2D</c>, and the value the browser sends back would
    /// decode to different bytes, hash to a different digest and find no session at all. The guard
    /// assertion is what stops that from being tested against a token that happened to contain neither.
    /// </remarks>
    [Test]
    public async Task Issue_WritesTheValueAsUnpaddedBase64UrlAndDoesNotEscapeIt()
    {
        // Arrange — bytes chosen so the encoding carries both of base64url's own characters.
        byte[] token = MixedAlphabetToken();
        string value = Base64UrlText.Encode(token);
        await Assert.That(value.Contains('-') && value.Contains('_')).IsTrue();

        // Act
        SetCookieHeaderValue issued = SetCookieHeaderValue.Parse(IssuedHeader(value, Expiry));

        // Assert — verbatim, unpadded, and round-tripping to the 32 bytes it started as.
        string written = issued.Value.ToString();
        await Assert.That(written).IsEqualTo(value);
        await Assert.That(written.Contains('=')).IsFalse();
        await Assert.That(Base64UrlText.Decode(written)).IsEquivalentTo(token);
    }

    [Test]
    public async Task Clear_TakesTheCookieAwayRatherThanLeavingItToLapse()
    {
        // Arrange, Act
        SetCookieHeaderValue cleared = SetCookieHeaderValue.Parse(ClearedHeader());

        // Assert — an empty value under the same name, with an expiry already in the past. A client
        // that ignored this header would still be refused on its next request; what this buys is the
        // other half, a browser that goes on believing it is signed in.
        await Assert.That(cleared.Name.ToString()).IsEqualTo(CookieName);
        await Assert.That(cleared.Value.ToString()).IsEqualTo(string.Empty);
        await Assert.That(cleared.Expires < DateTimeOffset.UtcNow).IsTrue();
    }

    /// <summary>
    /// That the header which clears the cookie and the header which sets it are the same cookie in every
    /// attribute a browser matches on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compared as raw text with the value and the expiry removed, rather than attribute by attribute
    /// through the parser. The parser models the attributes it knows; a <c>Domain</c>, a
    /// <c>Partitioned</c> or a <c>Priority</c> added to one call and not the other would reach the
    /// browser either way, and the browser is what decides whether these are one cookie or two.
    /// </para>
    /// <para>
    /// The value and the expiry are the two things that are <em>supposed</em> to differ — the clear
    /// carries nothing and a date in the past — so they are the two segments the comparison drops.
    /// <see cref="TheAttributeComparison_NoticesAMissingAttribute" /> is what says the remainder is
    /// still worth comparing.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Clear_AndIssue_AgreeOnEveryAttributeABrowserMatchesOn()
    {
        // Arrange
        string value = Base64UrlText.Encode(TokenBytes(0x11));

        // Act
        string issued = MatchingAttributes(IssuedHeader(value, Expiry));
        string cleared = MatchingAttributes(ClearedHeader());

        // Assert — and the name is compared as part of it, since it is the first thing a browser matches
        // on and the one attribute the two calls could not disagree about by accident.
        await Assert.That(cleared).IsEqualTo(issued);
        await Assert.That(issued).StartsWith($"{CookieName}=");
    }

    /// <summary>
    /// The provable-fail control for the comparison above.
    /// </summary>
    /// <remarks>
    /// Without it, <see cref="MatchingAttributes" /> could throw away every segment it was handed — or
    /// throw away one attribute too many — and the equality would hold between any two headers this
    /// application could possibly write. The dropped attribute here is <c>Path</c> on purpose: it is one
    /// of the three a browser identifies a stored cookie by, so an implementation that stopped writing
    /// it on one of the two paths is exactly the defect the comparison exists to catch.
    /// </remarks>
    [Test]
    public async Task TheAttributeComparison_NoticesAMissingAttribute()
    {
        // Arrange — the real header, and the same header with Path taken out of it.
        string real = IssuedHeader(Base64UrlText.Encode(TokenBytes(0x11)), Expiry);
        string withoutPath = string.Join(
            "; ",
            real.Split(';', StringSplitOptions.TrimEntries)
                .Where(segment => !segment.StartsWith("path=", StringComparison.OrdinalIgnoreCase)));

        // Act
        string comparable = MatchingAttributes(real);
        string mutated = MatchingAttributes(withoutPath);

        // Assert — the mutation really removed something, and the comparison sees it.
        await Assert.That(withoutPath).IsNotEqualTo(real);
        await Assert.That(mutated).IsNotEqualTo(comparable);
    }

    /// <summary>
    /// That an expiry which is not a UTC instant is refused rather than rendered.
    /// </summary>
    /// <remarks>
    /// An unspecified or local <see cref="DateTime" /> is rendered through the server's own zone, so a
    /// server west of UTC would hand out a cookie that expires hours early and one east of it a cookie
    /// that outlives its session row. Neither is an error anybody would see: the first signs people out
    /// early and the second leaves a handle the server refuses. The refusal is what keeps the cookie's
    /// expiry and the <c>sessions</c> row's the same instant.
    /// </remarks>
    [Test]
    public async Task Issue_WithAnExpiryThatIsNotUtc_IsRefused()
    {
        // Arrange
        string value = Base64UrlText.Encode(TokenBytes(0x11));
        DateTime unspecified = DateTime.SpecifyKind(Expiry, DateTimeKind.Unspecified);

        // Act, Assert
        await Assert.That(() => IssuedHeader(value, unspecified)).Throws<ArgumentException>();
        await Assert.That(() => IssuedHeader(value, Expiry.ToLocalTime())).Throws<ArgumentException>();
    }

    /// <summary>
    /// The one <c>Set-Cookie</c> header <see cref="SessionCookie.Issue" /> writes.
    /// </summary>
    /// <remarks>
    /// A bare <see cref="DefaultHttpContext" /> rather than a served request: what is under test is the
    /// header this method emits, and routing a request to it would only add ways for the assertion to be
    /// about something else. The count is checked so that a second cookie appearing beside this one
    /// fails here rather than being silently ignored by <c>Single</c>.
    /// </remarks>
    private static string IssuedHeader(string value, DateTime expiresAtUtc)
    {
        DefaultHttpContext context = new();
        SessionCookie.Issue(context.Response, value, expiresAtUtc);

        return SoleSetCookieHeader(context);
    }

    /// <summary>The one <c>Set-Cookie</c> header <see cref="SessionCookie.Clear" /> writes.</summary>
    private static string ClearedHeader()
    {
        DefaultHttpContext context = new();
        SessionCookie.Clear(context.Response);

        return SoleSetCookieHeader(context);
    }

    private static string SoleSetCookieHeader(HttpContext context)
    {
        string[] headers = [.. context.Response.Headers.SetCookie.Where(header => header is not null)!];

        return headers.Length == 1
            ? headers[0]
            : throw new InvalidOperationException(
                $"Expected exactly one Set-Cookie header, got {headers.Length}.");
    }

    /// <summary>
    /// The header reduced to the parts a browser decides "same cookie" by: everything except the value
    /// it carries and the instant it dies.
    /// </summary>
    /// <remarks>
    /// The name is kept — it is the first thing that must match — so the first segment is trimmed of its
    /// value rather than dropped. Ordinal-insensitive matching on the attribute keys, because the
    /// framework's casing of them is not a rule this file is pinning.
    /// </remarks>
    private static string MatchingAttributes(string header)
    {
        string[] segments = header.Split(';', StringSplitOptions.TrimEntries);
        string name = segments[0].Split('=', 2)[0];
        IEnumerable<string> attributes = segments
            .Skip(1)
            .Where(segment =>
                !segment.StartsWith("expires=", StringComparison.OrdinalIgnoreCase)
                && !segment.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))
            .Select(segment => segment.ToLowerInvariant());

        return string.Join("; ", [$"{name}=", .. attributes]);
    }

    /// <summary>
    /// A token of <see cref="Domain.Sessions.SessionToken.TokenLength" /> bytes, every one of them
    /// <paramref name="fill" />.
    /// </summary>
    private static byte[] TokenBytes(byte fill) => [.. Enumerable.Repeat(fill, 32)];

    /// <summary>
    /// A token whose base64url encoding carries both <c>-</c> and <c>_</c>.
    /// </summary>
    /// <remarks>
    /// <c>0xFF</c> triples encode to <c>____</c> and <c>0xFB 0xEF 0xBE</c> triples to <c>----</c>, which
    /// is the pair of characters standard base64 spells <c>/</c> and <c>+</c> — the two an escaping
    /// cookie writer would mangle.
    /// </remarks>
    private static byte[] MixedAlphabetToken() =>
    [
        .. Enumerable.Repeat<byte>(0xFF, 15),
        // The parentheses around the remainder are load-bearing: without them C# reads
        // "index % (3 switch { ... })", which is a modulus by 190 and hands back the index itself. The
        // guard assertion in the test is what caught that, and is why it is written as a guard rather
        // than as a comment claiming the fixture is interesting.
        .. Enumerable.Range(0, 17).Select(index => (byte)((index % 3) switch
        {
            0 => 0xFB,
            1 => 0xEF,
            _ => 0xBE,
        })),
    ];
}
