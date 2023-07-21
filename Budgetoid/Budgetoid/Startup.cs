using System;
using System.Reflection;
using Budgetoid;
using Budgetoid.Application.Transactions.Queries.GetTransaction;
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
        .AddJsonFile("local.settings.json", true, true)
        .AddEnvironmentVariables()
        .Build();

    // public override void ConfigureAppConfiguration(IFunctionsConfigurationBuilder builder)
    // {
    //     FunctionsHostBuilderContext context = builder.GetContext();
    //
    //     builder.ConfigurationBuilder
    //         .AddJsonFile(Path.Combine(context.ApplicationRootPath, "local.settings.json"), true, false)
    //         .AddJsonFile(Path.Combine(context.ApplicationRootPath, $"appsettings.{context.EnvironmentName}.json"), true, false)
    //         .AddEnvironmentVariables();
    // }

    public override void Configure(IFunctionsHostBuilder builder)
    {
        builder.Services.AddSingleton(_ =>
        {
            string connectionString = Environment.GetEnvironmentVariable("CUSTOMCONNSTR_CosmosDb") ??
                                      Configuration.GetConnectionString("CosmosDb");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    "The CosmosDb connection string is not defined in appsettings.json");

            return new CosmosClientBuilder(connectionString).Build();
        });

        // Call the InitializeCosmosDbStructure method here
        // CosmosDbInitializer.InitializeCosmosDbStructure(connectionString).Wait();

        builder.Services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(Assembly.GetAssembly(typeof(GetTransactionHandler))!));
        // builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        // builder.Services.AddSingleton<IValidator<GetUserQuery>, GetUserQueryValidator>();
    }
}
