using System.Diagnostics.CodeAnalysis;
using Application.Passkeys;
using Domain.Sessions;

namespace Api.Infrastructure;

/// <summary>
/// The one first-party cookie this application sets: its name, the attributes it is written with, the
/// only two writes that may ever be made to it, and its one reader.
/// </summary>
/// <remarks>
/// <para>
/// <b>One type because a cookie is cleared by re-setting it, and the browser compares attributes.</b> A
/// <c>Set-Cookie</c> that clears has to name the same cookie by <c>name</c>, <c>Path</c> and
/// <c>Domain</c> as the one that set it, or the browser stores a second cookie and keeps the first —
/// leaving a signed-out person's dead handle on every subsequent request. Two call sites building their
/// own <see cref="CookieOptions"/> are one edit away from that, and the symptom is not an error
/// anywhere: sign-out returns 204 and the cookie is still there.
/// </para>
/// <para>
/// <b><c>__Host-</c> is a rule the browser enforces, not a naming convention.</b> A cookie with that
/// prefix is refused outright unless it is <c>Secure</c>, has <c>Path=/</c> and carries <b>no</b>
/// <c>Domain</c> — which is exactly what makes it unwritable by a sibling subdomain. So the three
/// attributes below are not defaults to be tuned: setting <c>Domain</c> here, or relaxing
/// <c>Secure</c>, makes the browser drop the cookie and the sign-in silently stop working. Do not
/// "fix" that by renaming the cookie.
/// </para>
/// <para>
/// <b><c>SameSite=Lax</c> rather than <c>Strict</c>, and it is not what defends this API.</b> Lax is
/// what lets somebody following a link into the application arrive already signed in; Strict would
/// answer that first navigation signed out. What refuses a cross-site request is
/// <see cref="FirstPartyRequestMiddleware"/> — a header no cross-site form can add — and that control
/// covers the anonymous routes too, which is where a cookie is set in the first place.
/// </para>
/// <para>
/// <b>The expiry is absolute and never slides.</b> Extending it on each request would mean a
/// <c>GRANT UPDATE (expires_at_utc)</c> on <c>sessions</c> — a column the grant matrix leaves out of its
/// list precisely so a session's lifetime cannot be edited — and a row written on every read of every
/// page. A session lasts from the sign-in that opened it until <c>SessionPolicy.Lifetime</c> later, and
/// the cookie says so.
/// </para>
/// </remarks>
public static class SessionCookie
{
    /// <summary>
    /// The name a request presents its session handle under.
    /// </summary>
    /// <remarks>
    /// <b>Wire contract.</b> Every browser already holding one sends these bytes, so renaming it signs
    /// out every account at once, silently, with nothing in either log naming the cause. The tests that
    /// drive this cookie write the name out as a literal of their own rather than reading this constant,
    /// so that a rename goes red instead of agreeing with itself.
    /// </remarks>
    public const string Name = "__Host-budgetoid-session";

    /// <summary>
    /// Writes the handle onto the client, to be presented until <paramref name="expiresAtUtc"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One caller, <see cref="SessionCookieWriter"/>, and never an endpoint.</b> Five paths establish a
    /// session — a completed registration, a verified passkey assertion, a redeemed recovery code, a
    /// regeneration that ended a live session of the replaced set, and a locked sign-in — and each
    /// endpoint hands its handoff to that writer <em>after</em> its handler returned, because every
    /// refusal on the first four leaves by exception: a cookie written before the handler ran is a cookie
    /// a refusal leaves behind on the client of whoever was guessing. The locked sign-in's one refusal
    /// past its gates is a returned outcome rather than an exception, so there the writer runs on the
    /// established arm only, and the 404 arm writes nothing. The writer stands between the endpoints and
    /// this method because overwriting a cookie does not, by itself, end the session it named; the writer
    /// displaces that session first. <c>SessionCookieIssueCensusTests</c> holds the one caller. Measured:
    /// a direct call here from an endpoint reddens it.
    /// </para>
    /// <para>
    /// <b>Neither argument may be built here.</b> <paramref name="value"/> is the handle the handler
    /// minted and filed the digest of, and <paramref name="expiresAtUtc"/> is the session row's own
    /// instant; both arrive together on a <c>SessionHandoff</c>, which is what keeps a cookie from
    /// carrying bytes no row was written for or an expiry computed from an interval. A caller
    /// assembling either from something to hand produces a cookie that authenticates nothing, or one
    /// that outlives the session it names — and neither is visible from a response or from a row.
    /// </para>
    /// </remarks>
    /// <param name="response">The response the handle rides out on.</param>
    /// <param name="value">The token, encoded as the unpadded base64url a request presents it in.</param>
    /// <param name="expiresAtUtc">The session's own expiry — never an interval computed here.</param>
    public static void Issue(HttpResponse response, string value, DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(value);

        // Kind is load-bearing rather than pedantic: an unspecified DateTime is rendered as the local
        // zone's instant, so a server west of UTC would hand out a cookie that expires hours early and
        // one east of it a cookie that outlives its session row.
        if (expiresAtUtc.Kind is not DateTimeKind.Utc)
        {
            throw new ArgumentException("The expiry must be a UTC instant.", nameof(expiresAtUtc));
        }

        response.Cookies.Append(Name, value, Attributes(new DateTimeOffset(expiresAtUtc)));
    }

    /// <summary>
    /// Reads the handle the request presents and answers its digest, or <see langword="false"/> when it
    /// presents none or one that is not a handle this application could have issued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One reader, because two readers are how two dialects start.</b> The session scheme authenticates
    /// a request from this, and <see cref="SessionCookieWriter"/> displaces the session it names. If the
    /// two decoded differently, there would be a string one of them accepts and the other refuses: a
    /// cookie that signs a browser in and that an establishment then fails to displace, or the reverse.
    /// </para>
    /// <para>
    /// <b>Every refusal is <see langword="false"/>, never an exception.</b> The value is one a caller
    /// typed, read on anonymous routes too, so a decoder that threw would turn a hand-edited cookie into a
    /// logged server fault.
    /// </para>
    /// <para>
    /// <b>The width is refused from both sides and never truncated.</b> A short value is a prefix of a
    /// real handle and a long one is a real handle with something appended; a decoder that padded would
    /// find the row for the first and one that truncated would find it for the second. The ceiling handed
    /// to the decoder bounds the work an anonymous caller can name, and the equality after it is what
    /// bounds the value — the two are not the same check, because a 33-byte token encodes to text the
    /// ceiling admits.
    /// </para>
    /// </remarks>
    /// <param name="request">The request whose cookie is read.</param>
    /// <param name="tokenHash">SHA-256 of the presented handle, when this answers <see langword="true"/>.</param>
    public static bool TryReadTokenHash(HttpRequest request, [NotNullWhen(true)] out byte[]? tokenHash)
    {
        ArgumentNullException.ThrowIfNull(request);

        tokenHash = null;

        if (!request.Cookies.TryGetValue(Name, out string? presented))
        {
            return false;
        }

        // PasskeyEncoding despite the name, and borrowing it beats a second decoder. It is this
        // codebase's one base64url reader, it judges the length before validating and decoding — the
        // ordering an anonymous surface needs, argued in full over there — and a decoder written again
        // here is how the two dialects start disagreeing about which strings are handles. What the
        // ceiling cannot say is that the width is exact: it refuses 33 bytes, but a handle of 31 or of
        // one is under every bound it owns, so the equality below is a separate check and not a
        // restatement.
        if (!PasskeyEncoding.TryDecode(presented, SessionToken.TokenLength, out byte[]? token)
            || token.Length != SessionToken.TokenLength)
        {
            return false;
        }

        // Hashed here, at the boundary that decoded it, so the live token stops at this method and no
        // command, port or log statement below has a member it could travel through.
        tokenHash = SessionToken.HashOf(token);

        return true;
    }

    /// <summary>Takes the cookie off the client, whatever it was holding.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="IResponseCookies.Delete(string, CookieOptions)"/> rather than an empty
    /// <see cref="IResponseCookies.Append(string, string, CookieOptions)"/>, because it is the framework's
    /// own spelling of the same header — an empty value plus an expiry in the past — and it takes the
    /// attributes from <see cref="Attributes"/>, which is what makes this pair byte-identical to
    /// <see cref="Issue"/> in every attribute a browser matches on.
    /// </para>
    /// <para>
    /// <b>Clearing is not what ends access.</b> The session row is, and a client that ignored this header
    /// entirely would still be refused on its next request. What this buys is the other half: a browser
    /// that goes on believing it is signed in, sending a dead handle and showing a signed-in shell to
    /// whoever is at the keyboard.
    /// </para>
    /// </remarks>
    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Delete(Name, Attributes(expiresAt: null));
    }

    /// <summary>
    /// The attributes both members write, which is the whole reason they are one method.
    /// </summary>
    /// <remarks>
    /// <see cref="CookieOptions.Domain"/> is left unset on purpose and must stay that way — see the
    /// <c>__Host-</c> paragraph on the type. <see cref="CookieOptions.IsEssential"/> says this cookie is
    /// not subject to a consent policy, which is true of it and of nothing else here: it carries no
    /// analytics and the application cannot be used without it.
    /// </remarks>
    private static CookieOptions Attributes(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
        Expires = expiresAt,
    };
}
