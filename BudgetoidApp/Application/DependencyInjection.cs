using Application.Accounts.CreateAccount;
using Application.Accounts.DeleteAccount;
using Application.Accounts.GetAccount;
using Application.Accounts.GetAccounts;
using Application.Accounts.UpdateAccount;
using Application.Categories.CreateCategory;
using Application.Categories.DeleteCategory;
using Application.Categories.GetCategories;
using Application.Categories.GetCategory;
using Application.Categories.PlaceCategory;
using Application.Categories.UpdateCategory;
using Application.CategoryGroups.CreateCategoryGroup;
using Application.CategoryGroups.DeleteCategoryGroup;
using Application.CategoryGroups.GetCategoryGroup;
using Application.CategoryGroups.GetCategoryGroups;
using Application.CategoryGroups.MoveCategoryGroup;
using Application.CategoryGroups.UpdateCategoryGroup;
using Application.Currencies.GetCurrencies;
using Application.Payees.GetPayees;
using Application.Transactions.CreateTransaction;
using Application.Transactions.DeleteTransaction;
using Application.Transactions.GetTransaction;
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
        services.AddScoped<GetAccountHandler>();
        services.AddScoped<UpdateAccountHandler>();
        services.AddScoped<DeleteAccountHandler>();
        services.AddScoped<CreateCategoryGroupHandler>();
        services.AddScoped<GetCategoryGroupsHandler>();
        services.AddScoped<GetCategoryGroupHandler>();
        services.AddScoped<UpdateCategoryGroupHandler>();
        services.AddScoped<MoveCategoryGroupHandler>();
        services.AddScoped<DeleteCategoryGroupHandler>();
        services.AddScoped<CreateCategoryHandler>();
        services.AddScoped<GetCategoriesHandler>();
        services.AddScoped<GetCategoryHandler>();
        services.AddScoped<UpdateCategoryHandler>();
        services.AddScoped<PlaceCategoryHandler>();
        services.AddScoped<DeleteCategoryHandler>();
        services.AddScoped<GetCurrenciesHandler>();
        services.AddScoped<CreateTransactionHandler>();
        services.AddScoped<GetTransactionsHandler>();
        services.AddScoped<GetTransactionHandler>();
        services.AddScoped<DeleteTransactionHandler>();
        services.AddScoped<GetPayeesHandler>();
        services.AddScoped<EnsureUserHandler>();

        return services;
    }
}
