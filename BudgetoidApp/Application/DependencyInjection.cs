using Application.Transactions.CreateTransaction;
using Application.Transactions.GetTransactions;
using Application.Users.EnsureUser;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<CreateTransactionHandler>();
        services.AddScoped<GetTransactionsHandler>();
        services.AddScoped<EnsureUserHandler>();

        return services;
    }
}
