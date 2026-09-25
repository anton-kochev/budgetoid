using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntegrationTests;

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string SubjectHeader = "X-Test-Subject";
    public const string EmailHeader = "X-Test-Email";
    public const string OmitEmailHeader = "X-Test-Omit-Email";
    public const string EmailVerifiedHeader = "X-Test-Email-Verified";
    public const string OmitEmailVerifiedHeader = "X-Test-Omit-Email-Verified";

    /// <summary>
    /// One further claim on the principal, written as <c>type=value</c>, repeated once per claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists for one question no other header on this handler can ask: what happens to the claims
    /// the server did not want.</b> A real provider token carries <c>name</c>, <c>picture</c> and
    /// <c>locale</c> beside the three this product reads, and FR-003 is the claim that none of them is
    /// stored. A test driven by a principal that never carried them searches the database for values
    /// nothing sent, which is a search that passes against any implementation at all.
    /// </para>
    /// <para>
    /// Repeated rather than packed into one delimited value, so no test has to know an escaping rule for a
    /// claim value that legitimately contains a separator. A value with no <c>=</c> throws rather than
    /// being skipped: a silently dropped claim would put the test back in the state above, searching for
    /// something it only believed it had sent.
    /// </para>
    /// <para>
    /// Opt-in, and no test that does not name it sees any difference — which is what keeps it from
    /// changing the principal every other test in this suite was written against.
    /// </para>
    /// </remarks>
    public const string ExtraClaimHeader = "X-Test-Extra-Claim";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(SubjectHeader, out var subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? email = Request.Headers.ContainsKey(OmitEmailHeader)
            ? null
            : Request.Headers.TryGetValue(EmailHeader, out var emailHeader)
                ? emailHeader.ToString()
                : $"{subject}@example.com";

        // Defaults to "true" so every test that only asks to be authenticated keeps arriving with a
        // verified address; the raw string is passed through untouched so a test can send a value the
        // production code is expected to reject, such as "1" or "True".
        string? emailVerified = Request.Headers.ContainsKey(OmitEmailVerifiedHeader)
            ? null
            : Request.Headers.TryGetValue(EmailVerifiedHeader, out var emailVerifiedHeader)
                ? emailVerifiedHeader.ToString()
                : "true";

        List<Claim> claims = [new("sub", subject.ToString())];
        if (email is not null)
        {
            claims.Add(new Claim("email", email));
        }

        if (emailVerified is not null)
        {
            claims.Add(new Claim("email_verified", emailVerified));
        }

        // Added last and never merged with the three above, so a caller asking for a further claim
        // cannot silently redefine one this product reads.
        foreach (string? extra in Request.Headers[ExtraClaimHeader])
        {
            if (extra is null)
            {
                continue;
            }

            int separator = extra.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new InvalidOperationException(
                    $"'{ExtraClaimHeader}: {extra}' is not a claim: the header carries 'type=value'.");
            }

            claims.Add(new Claim(extra[..separator], extra[(separator + 1)..]));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
