using Application.Abstractions;
using Application.AccountKeys;
using Application.Accounts;
using Application.Categories;
using Application.CategoryGroups;
using Application.Currencies;
using Application.Payees;
using Application.RecoveryCodes;
using Application.Transactions;
using Application.Users;
using Application.Users.ExportData;
using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Sessions;
using Domain.Transactions;
using Domain.Users;
using Infrastructure.Persistence;
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
        services.AddScoped<ICategoryGroupRepository, CategoryGroupRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IBudgetRepository, BudgetRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<ISessionTokenRepository, SessionTokenRepository>();
        services.AddScoped<IPasskeyRepository, PasskeyRepository>();
        services.AddScoped<IRecoveryCodeRepository, RecoveryCodeRepository>();
        services.AddScoped<IRegistrationRepository, RegistrationRepository>();

        // Scoped like every other writer over the DbContext, and for the same reason: it holds the
        // scoped BudgetoidDbContext, so a longer lifetime would keep one request's context alive
        // across requests.
        services.AddScoped<IWebAuthnChallengeStore, DbWebAuthnChallengeStore>();
        services.AddScoped<ITransactionReadService, TransactionReadService>();
        services.AddScoped<IAccountReadService, AccountReadService>();
        services.AddScoped<ICurrencyReadService, CurrencyReadService>();
        services.AddScoped<IPayeeReadService, PayeeReadService>();
        services.AddScoped<ICategoryGroupReadService, CategoryGroupReadService>();
        services.AddScoped<ICategoryReadService, CategoryReadService>();
        services.AddScoped<IUserAccountReadService, UserAccountReadService>();
        services.AddScoped<ICredentialReadService, CredentialReadService>();
        services.AddScoped<IAccountKeyReadService, AccountKeyReadService>();
        services.AddScoped<IRecoveryCodeReadService, RecoveryCodeReadService>();
        services.AddScoped<IExportReadService, ExportReadService>();
        services.AddScoped<ITransactionalExecutor, DbContextTransactionalExecutor>();

        // Scoped for the reason every writer over the context is: it holds the scoped
        // BudgetoidDbContext, and discarding the tracked entities of a request other than the current
        // one is the one thing this must never be able to do.
        services.AddScoped<IPersistenceState, DbContextPersistenceState>();

        // Scoped, because it reads the scoped IBudgetContext and IUserContext. Api/Program.cs
        // attaches it through the (serviceProvider, options) overload of AddDbContext, whose
        // optionsLifetime is Scoped, so the instance resolves from the request scope rather than the
        // root one.
        services.AddScoped<SessionContextInterceptor>();
        return services;
    }
}
