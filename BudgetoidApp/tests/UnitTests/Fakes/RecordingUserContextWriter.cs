using Application.Users.EnsureUser;

namespace UnitTests.Fakes;

/// <summary>
/// An <see cref="IUserContextWriter"/> that keeps every id it is handed, in the order it was handed
/// them.
/// </summary>
/// <remarks>
/// Recording values rather than call counts is the point. The handler publishes more than once on
/// the racing paths, and every failure mode worth catching is a wrong <em>id</em> reaching the
/// session — a stale loser, or the id of an account the caller was never told about. A fake that
/// only reported "resolved was called" would be green for all of them, because the call is never the
/// part that goes wrong.
/// </remarks>
public sealed class RecordingUserContextWriter : IUserContextWriter
{
    private readonly List<Guid> _published = [];
    private readonly List<Guid> _publishedBudgets = [];

    /// <summary>Every id published, oldest first. The last entry is what the session ends up with.</summary>
    public IReadOnlyList<Guid> Published => _published;

    /// <summary>
    /// Every budget published, oldest first.
    /// </summary>
    /// <remarks>
    /// Nothing in this project reads it yet — no handler publishes a budget, the middleware does, and the
    /// middleware is not unit-testable here. Kept anyway rather than dropping the value on the floor: the
    /// type's whole claim is that it keeps what it was handed, and a fake that silently discards half of
    /// it is a fake that reads as green the first time a handler starts publishing budgets.
    /// </remarks>
    public IReadOnlyList<Guid> PublishedBudgets => _publishedBudgets;

    public void ResolveUser(Guid userId) => _published.Add(userId);

    public void ResolveBudget(Guid budgetId) => _publishedBudgets.Add(budgetId);
}
