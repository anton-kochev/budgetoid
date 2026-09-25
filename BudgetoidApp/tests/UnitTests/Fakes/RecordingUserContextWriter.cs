using Application.Users;

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
    /// <b>A handler does publish the ambient budget now, and it is the one every request goes
    /// through.</b> This remark used to say the opposite — that no handler published one and the
    /// provisioning middleware did — and that stopped being true when the middleware was deleted and
    /// <c>AuthenticateSessionHandler</c> became the only thing that names a tenant. Two of its tests
    /// read this list, and one of them reads it for a value that must <em>not</em> be here: a handler
    /// that threw on an account holding no budget must not have published a stranger's on the way out,
    /// and an empty list is the whole assertion. That is why the list keeps refusals as faithfully as
    /// it keeps successes, and why nothing here filters.
    /// </remarks>
    public IReadOnlyList<Guid> PublishedBudgets => _publishedBudgets;

    public void ResolveUser(Guid userId) => _published.Add(userId);

    public void ResolveBudget(Guid budgetId) => _publishedBudgets.Add(budgetId);
}
