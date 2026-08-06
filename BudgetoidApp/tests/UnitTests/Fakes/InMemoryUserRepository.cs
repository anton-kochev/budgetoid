using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IUserRepository"/> that reproduces the one database refusal an erasure has to
/// order around: a <c>users</c> delete is <b>refused</b> while the account still holds a transaction.
/// </summary>
/// <remarks>
/// <para>
/// Deleting a user cascades into <c>budgets</c>, and <c>transactions → budgets</c> is
/// <c>ON DELETE RESTRICT</c> rather than <c>CASCADE</c>, so PostgreSQL answers <c>23503</c>. That
/// refusal was reproduced against the real database before this rule was written here, which is the
/// only reason it is here. Giving this fake the same rule is what lets a unit test assert the
/// erasure's <em>outcome</em> instead of the order it called its collaborators in — a call-order
/// assertion would pass for a handler that got the order right by accident and would have to be
/// rewritten by anyone who legitimately reshaped the handler.
/// </para>
/// <para>
/// <c>categories → category_groups</c> is <b>not</b> modelled, and modelling it would be a lie: both
/// tables cascade from <c>budgets</c>, PostgreSQL queues that edge's check as an after-row trigger
/// when the <c>category_groups</c> row is deleted — strictly after the cascade into <c>categories</c>
/// was queued — and the after-trigger queue is FIFO, so the database does not refuse this and a fake
/// that did would pin a constraint nobody has. It was checked on postgres:17 with the two constraints
/// created in either order; both delete cleanly.
/// </para>
/// <para>
/// Where this fake is stricter than the database, and deliberately: it holds one tenant's rows and is
/// given no budget id, so it refuses on <em>any</em> surviving transaction where PostgreSQL refuses
/// only on ones the erased account owns. Nothing here can tell the two apart, and budget scoping is
/// proved against the real query filters in <c>TransactionRepositoryTests</c> instead. It also throws
/// a plain <see cref="InvalidOperationException"/> rather than the provider's own exception, because
/// no unit test reads the type — only that the delete was refused.
/// </para>
/// <para>
/// The surviving count is read off the transactions fake rather than tracked here, so a handler that
/// emptied that table through the real interface is exactly the handler this fake lets through.
/// Nothing in this class needs to be told the delete happened.
/// </para>
/// </remarks>
public sealed class InMemoryUserRepository(InMemoryTransactionRepository transactions) : IUserRepository
{
    private readonly List<User> _users = [];
    private readonly List<Credential> _credentials = [];

    public int DeleteCallCount { get; private set; }

    /// <summary>The rows still here, so an erasure's post-condition can be asserted directly.</summary>
    public IReadOnlyList<User> Users => _users;

    /// <summary>Stores a user and its credential directly, as if an earlier request had provisioned them.</summary>
    public void Seed(User user, Credential credential)
    {
        _users.Add(user);
        _credentials.Add(credential);
    }

    public Task<Guid?> FindUserIdByFederatedCredentialAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken = default)
    {
        // Ordinal on both halves, matching the case-sensitive column comparison the real lookup makes.
        Credential? credential = _credentials.SingleOrDefault(credential =>
            string.Equals(credential.Provider, provider, StringComparison.Ordinal)
            && string.Equals(credential.Subject, subject, StringComparison.Ordinal));
        return Task.FromResult(credential?.UserId);
    }

    public Task<bool> TryAddAsync(
        User user,
        Credential credential,
        CancellationToken cancellationToken = default)
    {
        // Both unique rules the real insert can lose to, modelled together because the real one
        // reports a single false for either. The email index is case-insensitive by collation.
        bool loses = _credentials.Any(existing =>
                         string.Equals(existing.Provider, credential.Provider, StringComparison.Ordinal)
                         && string.Equals(existing.Subject, credential.Subject, StringComparison.Ordinal))
                     || _users.Any(existing =>
                         string.Equals(existing.Email.Value, user.Email.Value, StringComparison.OrdinalIgnoreCase));

        if (loses)
        {
            return Task.FromResult(false);
        }

        Seed(user, credential);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Removes the user, or refuses when the RESTRICT edge under the cascade would still block it. An
    /// absent row is not an error, exactly as the real repository documents: erasure states a
    /// post-condition rather than acting on a row.
    /// </summary>
    public Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DeleteCallCount++;

        if (transactions.Transactions.Count > 0)
        {
            throw new InvalidOperationException(
                "23503: transactions → budgets is ON DELETE RESTRICT, so the transactions must be "
                + "deleted before the user.");
        }

        _users.RemoveAll(user => user.Id == userId);
        return Task.CompletedTask;
    }
}
