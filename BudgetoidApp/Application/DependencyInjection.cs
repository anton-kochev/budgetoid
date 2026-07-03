using Application.Accounts.CreateAccount;
using Application.Accounts.DeleteAccount;
using Application.Accounts.GetAccounts;
using Application.Accounts.UpdateAccount;
using Application.Currencies.GetCurrencies;
using Application.Groups.CreateGroup;
using Application.Groups.DeleteGroup;
using Application.Groups.GetGroups;
using Application.Groups.UpdateGroup;
using Application.Payees.GetPayees;
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
        services.AddScoped<CreateAccountHandler>();
        services.AddScoped<GetAccountsHandler>();
        services.AddScoped<UpdateAccountHandler>();
        services.AddScoped<DeleteAccountHandler>();
        services.AddScoped<CreateGroupHandler>();
        services.AddScoped<GetGroupsHandler>();
        services.AddScoped<UpdateGroupHandler>();
        services.AddScoped<DeleteGroupHandler>();
        services.AddScoped<GetCurrenciesHandler>();
        services.AddScoped<CreateTransactionHandler>();
        services.AddScoped<GetTransactionsHandler>();
        services.AddScoped<GetPayeesHandler>();
        services.AddScoped<EnsureUserHandler>();

        return services;
    }
}
