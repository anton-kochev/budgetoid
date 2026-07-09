using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntegrationTests;

public sealed class ApiFactory(
    string connectionString,
    string? defaultSubject = "test-subject",
    string environment = "Development",
    IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration(configuration =>
        {
            Dictionary<string, string?> values = new()
            {
                ["ConnectionStrings:budgetoid"] = connectionString,
                ["Authentication:Google:ClientId"] = "test-client-id",
            };

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
        });
    }

    public HttpClient CreateAuthenticatedClient(string? subject = null, string? email = null, string? name = null)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, subject ?? defaultSubject ?? "test-subject");
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email ?? $"{subject ?? defaultSubject ?? "test-subject"}@example.com");
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name ?? "Test User");
        return client;
    }

    public HttpClient CreateAuthenticatedClientWithoutEmail(string? subject = null, string? name = null)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, subject ?? defaultSubject ?? "test-subject");
        client.DefaultRequestHeaders.Add(TestAuthHandler.OmitEmailHeader, "true");
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name ?? "Test User");
        return client;
    }
}
