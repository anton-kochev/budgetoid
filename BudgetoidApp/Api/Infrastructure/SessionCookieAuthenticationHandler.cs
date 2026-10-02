using System.Security.Claims;
using System.Text.Encodings.Web;
using Application.Passkeys;
using Application.Sessions.AuthenticateSession;
using Domain.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Api.Infrastructure;

/// <summary>
/// Authenticates a request from the first-party session cookie: decodes the presented handle, and hands
/// the digest to the use case that turns it into an account, a budget and a session.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class decodes and decides; it looks nothing up.</b> The two reads that establish an identity
/// — the exempt <c>session_tokens</c> lookup and the policed <c>sessions</c> row after it — and the
/// order between them are <see cref="AuthenticateSessionHandler" />'s, in Application, where the
/// row-level-security ordering is argued. <c>CompositionBoundaryTests</c> holds the API to composing
/// Infrastructure rather than consuming it, and a handler here reaching for a repository would be the
/// same shortcut on a path where the ordering is the security property.
/// </para>
/// <para>
/// <b>Every refusal is <see cref="AuthenticateResult.NoResult" />, never an exception.</b> The value
/// being decoded is one a caller typed, and this scheme runs on the anonymous routes too. A decoder
/// that threw would turn a hand-edited cookie into a logged server fault, and — worse — into an oracle
/// answering differently for differently-malformed input on the one surface where a stranger is
/// allowed to experiment. <see cref="AuthenticateResult.Fail" /> is not used either: nothing here is a
/// failure worth surfacing, and the caller learns the same thing from the challenge that follows.
/// </para>
/// <para>
/// <b>The width is refused from both sides and never truncated.</b> A short value is a prefix of a real
/// handle and a long one is a real handle with something appended; a decoder that padded would find the
/// row for the first and one that truncated would find it for the second. The ceiling handed to the
/// decoder bounds the work an anonymous caller can name, and the equality below is what bounds the
/// value — the two are not the same check, because a 33-byte token encodes to text the ceiling admits.
/// </para>
/// <para>
/// <b>The claims are the answer, not the evidence.</b> <c>sub</c> carries this installation's own
/// account id and not a provider subject.
/// </para>
/// </remarks>
public sealed class SessionCookieAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AuthenticateSessionHandler authenticateSession)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The name this scheme is registered and forwarded to under.</summary>
    public const string SchemeName = "Budgetoid.Session";

    /// <summary>
    /// The account the request acts as. Spelled <c>sub</c> because that is what a subject claim is
    /// called, and carrying this installation's own account id rather than a provider subject.
    /// </summary>
    public const string SubjectClaimType = "sub";

    /// <summary>
    /// The sign-in the presented handle names, which is what lets a request end its own session without
    /// naming one. Read by <c>SessionEndpoints</c> and by nothing else.
    /// </summary>
    public const string SessionIdClaimType = "session_id";

    /// <summary>
    /// How much of the account the credential that opened this session reaches, as
    /// <see cref="SessionKind" /> spells it.
    /// </summary>
    /// <remarks>
    /// Read, through <see cref="TryReadSessionKind" /> and nowhere else, by the handlers of the two
    /// requirements on the application's fallback policy and therefore on every route that declares
    /// nothing: <see cref="FullSessionRequirement" /> answers 403 to a session whose kind does not read
    /// budget content unless the route carries <see cref="AllowsLockedSessionAttribute" />, and
    /// <see cref="LockedSessionOnlyRequirement" /> answers 403 to anything but a locked session on a route
    /// carrying <see cref="RequiresLockedSessionAttribute" />. Carried as a claim rather than re-read per
    /// request so that the answer a request acts on is the one its own authentication reached. An
    /// identity that authenticated on this scheme and carries no such claim reaches nothing — both
    /// requirements refuse what they cannot find, because a missing claim is a session nobody proved
    /// anything about rather than a session with nothing to prove.
    /// </remarks>
    public const string SessionKindClaimType = "session_kind";

    /// <summary>
    /// Reads the kind claim off <paramref name="principal" />, answering <see langword="true" /> only when
    /// it is present and spelled exactly as this handler writes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The round trip is the rule, and a bare <c>Enum.TryParse</c> is not a substitute for it</b> — the
    /// same argument <c>CanonicalIdentifier</c> makes about a format that is not a spelling. Two families of
    /// value get in without it. The obvious call is the case-insensitive overload, which admits
    /// <c>"full"</c>; and <em>every</em> overload accepts a numeric string, so <c>"1"</c> parses to
    /// <see cref="SessionKind.Full" /> under the case-sensitive one too. The claim is written by
    /// <c>SessionKind.ToString()</c>, which produces exactly one spelling per member, so comparing the
    /// presented text back against what the parsed member renders as is the only formulation that cannot
    /// drift from the value this handler actually published. Anything else is an account opened by a value
    /// nothing in this product ever wrote.
    /// </para>
    /// <para>
    /// <b>One definition beside the write, because two readers depend on it.</b> Restated in each
    /// requirement handler, the first one to be "simplified" would admit a spelling the other refuses. A
    /// single-value read on purpose: this scheme writes the claim once, and a principal carrying two is
    /// one it did not build — <c>FindFirst</c> judges the first, which is no worse than judging none.
    /// </para>
    /// </remarks>
    internal static bool TryReadSessionKind(ClaimsPrincipal principal, out SessionKind kind)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? claimValue = principal.FindFirstValue(SessionKindClaimType);

        return Enum.TryParse(claimValue, out kind)
               && string.Equals(claimValue, kind.ToString(), StringComparison.Ordinal);
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No cookie is not a refusal: it is a request that said nothing about a session. This is also
        // the arm that makes "an authenticated request can never name an account that does not exist"
        // structural — NoResult leaves the principal unauthenticated and the fallback policy challenges,
        // so nothing downstream ever runs holding an identity nobody could resolve.
        if (!Request.Cookies.TryGetValue(SessionCookie.Name, out string? presented))
        {
            return AuthenticateResult.NoResult();
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
            return AuthenticateResult.NoResult();
        }

        // Hashed here, at the boundary that decoded it, so the live token stops at this method and no
        // command, port or log statement below has a member it could travel through.
        AuthenticatedSession? session = await authenticateSession.HandleAsync(
            new AuthenticateSessionCommand(SessionToken.HashOf(token)),
            Context.RequestAborted);

        if (session is null)
        {
            return AuthenticateResult.NoResult();
        }

        // A session that has ended reaches exactly one route — the one that ends sessions — so that
        // signing out twice is 204 rather than a 401 leaving a dead cookie on the client forever. See
        // AcceptsEndedSessionAttribute for why that permission is a marker on the route rather than an
        // authorization policy, and for why it grants far less than it looks like it does.
        if (!session.IsLive
            && Context.GetEndpoint()?.Metadata.GetMetadata<AcceptsEndedSessionAttribute>() is null)
        {
            return AuthenticateResult.NoResult();
        }

        // The scheme name as the identity's authentication type, which is what makes the principal
        // IsAuthenticated at all — and is also how a reader of HttpContext.User can tell a request that
        // came in on a cookie from one that came in on a provider token.
        ClaimsIdentity identity = new(
            [
                new Claim(SubjectClaimType, session.UserId.ToString()),
                new Claim(SessionIdClaimType, session.SessionId.ToString()),
                new Claim(SessionKindClaimType, session.Kind.ToString()),
            ],
            SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
