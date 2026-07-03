using Application.Accounts;
using Application.Currencies;
using Application.Groups;
using Application.Payees;
using Application.Transactions;
using Domain.Accounts;
using Domain.Groups;
using Domain.Payees;
using Domain.Transactions;
using Domain.Users;
using Infrastructure.ReadServices;
using Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        services.AddScoped<IPayeeRepository, PayeeRepository>();
        services.AddScoped<IGroupRepository, GroupRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ITransactionReadService, TransactionReadService>();
        services.AddScoped<IAccountReadService, AccountReadService>();
        services.AddScoped<ICurrencyReadService, CurrencyReadService>();
        services.AddScoped<IPayeeReadService, PayeeReadService>();
        services.AddScoped<IGroupReadService, GroupReadService>();
        return services;
    }
}
