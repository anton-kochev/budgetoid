using Application.Abstractions;
using Application.Transactions.CreateTransaction;
using Application.Transactions.GetTransactions;
using Microsoft.Extensions.DependencyInjection;

namespace Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IUserContext, FakeUserContext>();
        services.AddScoped<CreateTransactionHandler>();
        services.AddScoped<GetTransactionsHandler>();

        return services;
    }
}
