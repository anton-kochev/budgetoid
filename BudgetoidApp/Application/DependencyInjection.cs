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
using Application.Passkeys.BeginAssertion;
using Application.Passkeys.BeginRegistration;
using Application.Passkeys.CompleteAssertion;
using Application.Passkeys.CompleteRegistration;
using Application.Passkeys.Reauthentication;
using Application.Passkeys.RevokePasskey;
using Application.Payees.GetPayees;
using Application.Payees.RenamePayee;
using Application.RecoveryCodes.CountRecoveryCodes;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Application.RecoveryCodes.RedeemRecoveryCode;
using Application.Registration;
using Application.Sessions.AuthenticateSession;
using Application.Sessions.RevokeSession;
using Application.Sessions.RevokeSessionsForCredential;
using Application.Transactions.CreateTransaction;
using Application.Transactions.DeleteTransaction;
using Application.Transactions.GetTransaction;
using Application.Transactions.GetTransactions;
using Application.Transactions.UpdateTransaction;
using Application.Users.EnsureUser;
using Application.Users.EraseAccount;
using Application.Users.ExportData;
using Application.Users.GetSignedInUser;
using Application.Users.ListCredentials;
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
        services.AddScoped<UpdateTransactionHandler>();
        services.AddScoped<DeleteTransactionHandler>();
        services.AddScoped<GetPayeesHandler>();
        services.AddScoped<RenamePayeeHandler>();
        services.AddScoped<ResolveUserHandler>();
        services.AddScoped<EnsureUserHandler>();
        services.AddScoped<EraseAccountHandler>();
        services.AddScoped<ExportDataHandler>();
        services.AddScoped<GetSignedInUserHandler>();
        services.AddScoped<ListCredentialsHandler>();
        services.AddScoped<RevokeSessionsForCredentialHandler>();
        services.AddScoped<AuthenticateSessionHandler>();
        services.AddScoped<RevokeSessionHandler>();
        services.AddScoped<BeginRegistrationHandler>();
        services.AddScoped<CompleteRegistrationHandler>();
        services.AddScoped<BeginAssertionHandler>();
        services.AddScoped<CompleteAssertionHandler>();
        services.AddScoped<BeginReauthenticationHandler>();
        services.AddScoped<RevokePasskeyHandler>();
        services.AddScoped<GenerateRecoveryCodesHandler>();
        services.AddScoped<CountRecoveryCodesHandler>();
        services.AddScoped<RedeemRecoveryCodeHandler>();
        services.AddScoped<BeginAccountRegistrationHandler>();
        services.AddScoped<RegisterAccountHandler>();

        // Registered as the concrete type, because it has no interface and must not grow one: a
        // stubbable gate would let a test prove erasure works with the proof faked out.
        services.AddScoped<PasskeyReauthentication>();

        return services;
    }
}
