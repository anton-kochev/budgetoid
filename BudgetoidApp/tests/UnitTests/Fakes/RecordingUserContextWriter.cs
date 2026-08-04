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

    /// <summary>Every id published, oldest first. The last entry is what the session ends up with.</summary>
    public IReadOnlyList<Guid> Published => _published;

    public void ResolveUser(Guid userId) => _published.Add(userId);
}
