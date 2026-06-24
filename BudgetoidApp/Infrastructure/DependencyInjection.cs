using Application.Payees;
using Application.Transactions;
using Application.Users;
using Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        services.AddScoped<IPayeeRepository, PayeeRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        return services;
    }
}
