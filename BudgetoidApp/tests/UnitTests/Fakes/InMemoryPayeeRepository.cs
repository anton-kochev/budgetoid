using Application.Payees;
using Domain.Payees;
using Domain.Security;
using TestSupport;

namespace UnitTests.Fakes;

/// <summary>
/// The payee port and the payee read side, over a list.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no member here that takes a name, and the absence is the fake's whole fidelity.</b>
/// <c>GetOrCreateAsync</c> used to live here and folded case with
/// <see cref="StringComparison.OrdinalIgnoreCase" /> — a fake standing in for a case-insensitive
/// collation on a text column. Both are gone: the column is an envelope, the collation left with it, and
/// the server holds no index key with which to compute the digest a name is stored under. A fake that
/// kept the member would let a handler under test resolve a name the real one cannot, which is the one
/// way an in-memory double can make a whole slice of tests describe a system that does not exist.
/// </para>
/// </remarks>
public sealed class InMemoryPayeeRepository(Guid budgetId, TimeProvider timeProvider)
    : IPayeeRepository, IPayeeReadService
{
    private readonly List<Payee> _payees = [];

    public int AddCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }

    public Task AddAsync(Payee payee, CancellationToken cancellationToken = default)
    {
        AddCallCount++;
        _payees.Add(payee);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PayeeDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // The creation instant, then the id — PayeeReadService's order and no longer the name's.
        // `OrderBy(payee => payee.Name)` was here and could not stay: NarrativeField implements no
        // comparison, so it would have thrown at run time rather than failed to build. And there is
        // nothing to replace it with — ordering a bytea name orders by the first differing byte, which
        // after the version is the nonce, redrawn on every seal.
        IReadOnlyList<PayeeDto> payees = _payees
            .OrderBy(payee => payee.CreatedAtUtc)
            .ThenBy(payee => payee.Id)
            .Select(PayeeDto.FromPayee)
            .ToList();

        return Task.FromResult(payees);
    }

    // No budget filter: every payee this fake holds was stamped with the one budget id it was
    // constructed with, so it can only match on the payee id. Budget isolation is exercised against
    // the real EF query filters instead.
    public Task<Payee?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Payee? payee = _payees.SingleOrDefault(payee => payee.Id == id);
        return Task.FromResult(payee);
    }

    // Explicit implementation because the repository already exposes a public GetByIdAsync that returns
    // the entity; the read-service member shares that name but returns a DTO, so the two cannot both be
    // ordinary public methods on this class. InMemoryAccountRepository resolves the same collision the
    // same way.
    Task<PayeeDto?> IPayeeReadService.GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        Payee? payee = _payees.SingleOrDefault(payee => payee.Id == id);
        return Task.FromResult(payee is null ? null : PayeeDto.FromPayee(payee));
    }

    // Nothing to store: callers rename the instance handed back by GetByIdAsync, which is the very
    // instance held in the list, so the new name is already visible here. The real repository has the
    // same shape — it saves changes to an entity the context is already tracking. The counter is
    // what a test can assert on to prove the save was asked for at all.
    public Task UpdateAsync(Payee payee, CancellationToken cancellationToken = default)
    {
        UpdateCallCount++;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Seeds a payee whose name is sealed and indexed from <paramref name="label" />.
    /// </summary>
    /// <param name="label">
    /// What distinguishes this row from the next one. It is NOT the payee's name and is never read back
    /// as one — the column holds an envelope this side has no key for. It survives as the seed both
    /// halves of the name are derived from, so a caller that wants two rows to hold "the same name"
    /// passes one label twice.
    /// </param>
    /// <param name="createdAtUtc">
    /// The creation instant, when the caller needs one that disagrees with the order the rows were
    /// seeded in. It exists for exactly one case: a v7 identifier sorts by the instant it was minted,
    /// so rows seeded in sequence make "order by the id" and "order by the creation instant, then the
    /// id" emit byte-identical lists, and an id-only read side passes an ordering assertion it should
    /// fail. Inverting the two is the only seed that tells them apart. Defaults to the clock.
    /// </param>
    /// <remarks>
    /// The id is minted HERE and threaded in, because <see cref="Payee.Create" /> no longer mints one:
    /// it is the associated data the name was sealed against, so the factory takes it and never invents
    /// it.
    /// </remarks>
    public async Task<Payee> CreateAsync(string label = "Starbucks", DateTime? createdAtUtc = null)
    {
        Payee payee = Payee.Create(
            Guid.CreateVersion7(),
            budgetId,
            SealedNarrative.Indexed(label),
            createdAtUtc ?? timeProvider.GetUtcNow().UtcDateTime);
        await AddAsync(payee);
        return payee;
    }
}
