using Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntegrationTests;

public sealed class ApiFactory(string connectionString, Guid? userId = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:budgetoid"] = connectionString,
            });
        });

        if (userId is { } id)
        {
            // ConfigureTestServices runs after the app's own ConfigureServices, so this override
            // wins. Identity is fixed at factory build, so use a separate factory per user to
            // exercise cross-user isolation.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserContext>();
                services.AddScoped<IUserContext>(_ => new TestUserContext(id));
            });
        }
    }
}
