using Application.Abstractions;
using Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        return services
            // .AddSingleton(new CosmosClient(Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING")!))
            .AddScoped<IAccountsRepository, AccountsRepository>()
            .AddScoped<IPayeesRepository, PayeesRepository>()
            .AddScoped<ITransactionsRepository, TransactionsRepository>();
    }
}
