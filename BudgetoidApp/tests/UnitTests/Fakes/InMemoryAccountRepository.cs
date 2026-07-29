using Application.Accounts;
using Application.Currencies;
using Domain.Accounts;

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
        IReadOnlyList<AccountDto> accounts = _accounts
            .OrderBy(account => account.Name)
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

    public async Task<Account> CreateAsync(string name = "Checking", AccountType type = AccountType.Checking, decimal openingBalance = 0m, string currencyCode = "USD")
    {
        // The minor unit comes from the same lookup the read projection uses, so a JPY account
        // seeded here is validated as a zero-decimal currency rather than silently as USD.
        Account account = Account.Create(
            budgetId,
            name,
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
