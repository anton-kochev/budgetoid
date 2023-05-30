using System;
using System.Configuration;
using Budgetoid;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(Startup))]

namespace Budgetoid;

public class Startup : FunctionsStartup
{
    private static readonly IConfigurationRoot Configuration = new ConfigurationBuilder()
        .SetBasePath(Environment.CurrentDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .Build();
    
    public override void Configure(IFunctionsHostBuilder builder)
    {
        builder.Services.AddSingleton(s =>
        {
            string connectionString = Configuration.GetConnectionString("CosmosDb");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "The CosmosDb connection string is not defined in appsettings.json");
            }

            return new CosmosClientBuilder(connectionString).Build();
        });
    }
}
