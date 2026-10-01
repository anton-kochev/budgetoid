using Application.Accounts;
using Application.Currencies;
using Domain.Accounts;
using TestSupport;

namespace UnitTests.Fakes;

public sealed class InMemoryAccountRepository(Guid budgetId, TimeProvider timeProvider) : IAccountRepository, IAccountReadService
{
    private readonly List<Account> _accounts = [];
    private readonly HashSet<Guid> _referencedAccountIds = [];

    public int AddCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }

    public Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        AddCallCount++;
        _accounts.Add(account);
        return Task.CompletedTask;
    }

    public Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Account? account = _accounts.SingleOrDefault(account => account.Id == id && account.BudgetId == budgetId);
        return Task.FromResult(account);
    }

    public Task UpdateAsync(Account account, CancellationToken cancellationToken = default)
    {
        UpdateCallCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Account account, CancellationToken cancellationToken = default)
    {
        DeleteCallCount++;
        _accounts.Remove(account);
        return Task.CompletedTask;
    }

    public Task<bool> HasTransactionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_referencedAccountIds.Contains(accountId));
    }

    public void MarkReferenced(Guid accountId) => _referencedAccountIds.Add(accountId);

    public Task<IReadOnlyList<AccountDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // The creation instant, then the id — AccountReadService's order and no longer the name's.
        // `OrderBy(account => account.Name)` was here and could not stay: NarrativeField implements no
        // comparison, so it would have thrown at run time rather than failed to build. And there is
        // nothing to replace it with — ordering a bytea name orders by the first differing byte, which
        // after the version is the nonce, redrawn on every seal.
        IReadOnlyList<AccountDto> accounts = _accounts
            .OrderBy(account => account.CreatedAtUtc)
            .ThenBy(account => account.Id)
            .Select(account => AccountDto.FromAccount(account, CurrencyFor(account.CurrencyCode)))
            .ToList();

        return Task.FromResult(accounts);
    }

    // Explicit implementation because the repository already exposes a public GetByIdAsync that
    // returns the entity; the read-service member shares that name but returns a DTO, so the two
    // cannot both be ordinary public methods on this class.
    Task<AccountDto?> IAccountReadService.GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        Account? account = _accounts.SingleOrDefault(account => account.Id == id && account.BudgetId == budgetId);
        AccountDto? dto = account is null
            ? null
            : AccountDto.FromAccount(account, CurrencyFor(account.CurrencyCode));

        return Task.FromResult(dto);
    }

    /// <summary>
    /// Seeds an account whose name is sealed and indexed from <paramref name="label" />.
    /// </summary>
    /// <param name="label">
    /// What distinguishes this row from the next one. It is NOT the account's name and is never read
    /// back as one — the column holds an envelope this side has no key for. It survives as the seed
    /// both halves of the name are derived from, so a caller that wants two rows to hold "the same
    /// name" passes one label twice.
    /// </param>
    public async Task<Account> CreateAsync(string label = "Checking", AccountType type = AccountType.Checking, decimal openingBalance = 0m, string currencyCode = "USD")
    {
        // The minor unit comes from the same lookup the read projection uses, so a JPY account
        // seeded here is validated as a zero-decimal currency rather than silently as USD.
        //
        // The id is minted HERE and threaded in, because Account.Create no longer mints one: it is the
        // associated data the name was sealed against, so the factory takes it and never invents it.
        Account account = Account.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed(label),
            type,
            openingBalance,
            currencyCode,
            CurrencyFor(currencyCode).MinorUnit,
            timeProvider.GetUtcNow().UtcDateTime);
        await AddAsync(account);
        return account;
    }

    private static CurrencyDto CurrencyFor(string currencyCode) => currencyCode switch
    {
        "JPY" => new CurrencyDto("JPY", "Yen", "¥", 0),
        _ => new CurrencyDto("USD", "US Dollar", "$", 2),
    };
}
