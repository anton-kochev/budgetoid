using System.Text.Json;
using Application;
using Infrastructure;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

IConfigurationRoot configuration = new ConfigurationBuilder()
    .SetBasePath(Environment.CurrentDirectory)
    .AddJsonFile("local.settings.json", true, true)
    .AddEnvironmentVariables()
    .Build();

IHost host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
            services
                .AddApplicationServices()
                .AddInfrastructureServices()
                .Configure<JsonSerializerOptions>(options =>
                {
                    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                })
                .AddSingleton(_ =>
                {
                    string connectionString =
                        Environment.GetEnvironmentVariable("CUSTOMCONNSTR_CosmosDb") ??
                        configuration.GetConnectionString("CosmosDb");
                    if (string.IsNullOrWhiteSpace(connectionString))
                    {
                        throw new InvalidOperationException(
                            "The CosmosDb connection string is not defined in local.settings.json");
                    }

                    return new CosmosClientBuilder(connectionString).Build();
                })
        // .AddSingleton<ICosmosDbInitializer, CosmosDbInitializer>()
        // .AddSingleton<ICosmosDbContainerFactory, CosmosDbContainerFactory>()
        // .AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        // .AddSingleton<IValidator<GetUserQuery>, GetUserQueryValidator>();
    ).Build();

await host.RunAsync();