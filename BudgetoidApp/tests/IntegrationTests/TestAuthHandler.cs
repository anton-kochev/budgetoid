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
    public const string NameHeader = "X-Test-Name";

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
        string name = Request.Headers.TryGetValue(NameHeader, out var nameHeader)
            ? nameHeader.ToString()
            : "Test User";

        List<Claim> claims = [new("sub", subject.ToString())];
        if (email is not null)
        {
            claims.Add(new Claim("email", email));
        }

        claims.Add(new Claim("name", name));
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
