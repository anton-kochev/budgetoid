using Application.Abstractions;
using Domain.Accounts;
using Domain.Budgets;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Currencies;
using Domain.Payees;
using Domain.Sessions;
using Domain.Transactions;
using Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public sealed class BudgetoidDbContext(
    DbContextOptions<BudgetoidDbContext> options,
    IBudgetContext? budgetContext = null) : DbContext(options)
{
    // Budgets, Users, Credentials and Sessions deliberately carry no BudgetIsolation query filter:
    // they are what provisioning reads and writes before an ambient budget exists, and a credential is
    // keyed on the user it lets in rather than on a budget at all. A session's reason is its own: it
    // belongs to a person and names no budget, so there is no budget to filter it by — one person's
    // session is established before any budget is ambient and outlives whichever budget was. It is
    // isolated on user_id by the user_isolation policy in the database instead. Every query over these
    // sets must therefore scope by owner explicitly.
    //
    // The three passkey sets are unfiltered too, and their reasons differ from each other. A public key
    // and a signature counter belong to a credential, and through it to a person; neither names a
    // budget, and both are read while authenticating — before any budget could be ambient. They are
    // isolated on user_id by user_isolation, exactly as sessions are. A challenge is the odd one: it
    // names nobody at all, because the authentication ceremony issues it before anybody has said who
    // they are, so there is no owner for a filter or a policy to key on. See
    // WebAuthnChallengeConfiguration for what stands in for isolation there.
    //
    // Recovery code hashes are unfiltered for a reason of their own, and it is the strictest of the
    // lot. The row is found by the SHA-256 of the verifier on an ANONYMOUS redemption request, before
    // anybody has said who they are, so there is no budget to filter by and no identity a policy could
    // be keyed on either — which is why the table is written down in
    // RowLevelSecurityCoverage.Exemptions rather than policed. Nothing beneath this set scopes it: any
    // read or write of it other than the discovery lookup itself must carry its own user_id filter.
    //
    // Session tokens are unfiltered for the strictest reason of all, and it is recovery_code_hashes'
    // one arriving on the path every authenticated request takes. The row is found by the SHA-256 of
    // the token a cookie presented, before anybody has said who they are — that lookup is what
    // establishes the identity — so there is no budget to filter by and no identity a policy could be
    // keyed on either, which is why the table is written down in
    // RowLevelSecurityCoverage.Exemptions rather than policed. Nothing beneath this set scopes it, and
    // the discovery lookup is the only read it has; a second one would owe its own user_id filter.
    //
    // Wrapped account keys are unfiltered for the reason the passkey material is, and the reason is not
    // that a budget filter would be inconvenient: these envelopes belong to a recovery factor, and
    // through it to a person. They name no budget and could not — the content key they wrap is the
    // account's, so a copy keyed on one budget would be a claim that some of an account's data is
    // encrypted under a different key than the rest. They are also read while authenticating, before any
    // budget could be ambient. Isolation on user_id comes from the user_isolation policy, which this
    // table is subject to rather than exempt from: nothing about it is read before the request has an
    // identity, so every read must both carry its own user_id filter and stay policed.
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Payee> Payees => Set<Payee>();
    public DbSet<CategoryGroup> CategoryGroups => Set<CategoryGroup>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionToken> SessionTokens => Set<SessionToken>();
    public DbSet<PasskeyPublicKey> PasskeyPublicKeys => Set<PasskeyPublicKey>();
    public DbSet<PasskeySignatureCounter> PasskeySignatureCounters => Set<PasskeySignatureCounter>();
    public DbSet<RecoveryCodeHash> RecoveryCodeHashes => Set<RecoveryCodeHash>();
    public DbSet<WrappedAccountKeys> WrappedAccountKeys => Set<WrappedAccountKeys>();

    // Internal rather than public, following its row type: nothing outside this assembly has a reason
    // to read a protocol nonce, and a public set would be the first step towards one.
    internal DbSet<WebAuthnChallengeRow> WebAuthnChallenges => Set<WebAuthnChallengeRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BudgetoidDbContext).Assembly);
        modelBuilder.HasCollation(
            "case_insensitive",
            locale: "und-u-ks-level2",
            provider: "icu",
            deterministic: false);

        // Every filter reads the primary-constructor parameter on purpose: Roslyn lowers it to an
        // instance field, so the lambda closes over `this` and EF re-roots the closure to the context
        // instance running the query. A captured local, a static, or a service-locator call would bake
        // the first request's budget into the cached model and leak rows across tenants.
        modelBuilder.Entity<Transaction>()
            .HasQueryFilter("BudgetIsolation", transaction => transaction.BudgetId == budgetContext!.BudgetId);
        modelBuilder.Entity<Account>()
            .HasQueryFilter("BudgetIsolation", account => account.BudgetId == budgetContext!.BudgetId);
        modelBuilder.Entity<Payee>()
            .HasQueryFilter("BudgetIsolation", payee => payee.BudgetId == budgetContext!.BudgetId);
        modelBuilder.Entity<CategoryGroup>()
            .HasQueryFilter("BudgetIsolation", categoryGroup => categoryGroup.BudgetId == budgetContext!.BudgetId);
        modelBuilder.Entity<Category>()
            .HasQueryFilter("BudgetIsolation", category => category.BudgetId == budgetContext!.BudgetId);
    }
}
