using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

/// <summary>
/// Hosts the API over a test database under <b>two</b> identities, mirroring how the deployed
/// application is configured: <c>ConnectionStrings:budgetoid</c> is the least-privilege
/// application role that serves every request, and <c>ConnectionStrings:budgetoid-admin</c> is
/// the elevated account used only by the Development-startup block to migrate and to provision
/// the role's grants. Splitting them is what lets the API suite prove the grant matrix is
/// sufficient rather than merely correct.
/// </summary>
/// <param name="appConnectionString">
/// Connection string the application serves requests on — the least-privilege role.
/// </param>
/// <param name="adminConnectionString">
/// Elevated connection string for startup migration and provisioning. Declared last, and
/// optional, so existing positional call sites keep compiling and so a <c>string?</c> subject
/// can never slide into it by accident. When omitted it falls back to
/// <paramref name="appConnectionString" />, which suits the tests that run in Production
/// (no startup migration, so no database is touched at all).
/// </param>
public sealed class ApiFactory(
    string appConnectionString,
    string? defaultSubject = "test-subject",
    string environment = "Development",
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null,
    string? adminConnectionString = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration(configuration =>
        {
            Dictionary<string, string?> values = new()
            {
                ["ConnectionStrings:budgetoid"] = appConnectionString,
                ["ConnectionStrings:budgetoid-admin"] = adminConnectionString ?? appConnectionString,
                ["Authentication:Google:ClientId"] = "test-client-id",
            };

            // Applied after the defaults so a caller can still override either connection string
            // key — including pointing the application back at the admin account to isolate a
            // failure to privileges rather than to the change under test.
            if (settings is not null)
            {
                foreach ((string key, string? value) in settings)
                {
                    values[key] = value;
                }
            }

            configuration.AddInMemoryCollection(values);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Runs last so a caller can replace anything the application registered, including the
            // test authentication above. Tests that need to fail a specific collaborator swap it here
            // rather than constructing a handler by hand, which would couple them to its constructor.
            configureServices?.Invoke(services);
        });
    }

    /// <param name="emailVerified">
    /// Raw value for the <c>email_verified</c> claim. Declared last, and optional, so existing
    /// positional call sites keep compiling. When omitted the handler emits its own default.
    /// </param>
    public HttpClient CreateAuthenticatedClient(string? subject = null, string? email = null, string? emailVerified = null)
    {
        HttpClient client = CreateSubjectClient(subject, out string resolvedSubject);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email ?? $"{resolvedSubject}@example.com");
        if (emailVerified is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.EmailVerifiedHeader, emailVerified);
        }

        return client;
    }

    public HttpClient CreateAuthenticatedClientWithoutEmail(string? subject = null)
    {
        HttpClient client = CreateSubjectClient(subject, out _);
        client.DefaultRequestHeaders.Add(TestAuthHandler.OmitEmailHeader, "true");
        return client;
    }

    public HttpClient CreateAuthenticatedClientWithUnverifiedEmail(string? subject = null) =>
        CreateAuthenticatedClient(subject, emailVerified: "false");

    public HttpClient CreateAuthenticatedClientWithoutEmailVerifiedClaim(string? subject = null)
    {
        HttpClient client = CreateSubjectClient(subject, out string resolvedSubject);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, $"{resolvedSubject}@example.com");
        client.DefaultRequestHeaders.Add(TestAuthHandler.OmitEmailVerifiedHeader, "true");
        return client;
    }

    private HttpClient CreateSubjectClient(string? subject, out string resolvedSubject)
    {
        resolvedSubject = subject ?? defaultSubject ?? "test-subject";
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, resolvedSubject);
        return client;
    }
}
