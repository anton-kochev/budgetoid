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

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
