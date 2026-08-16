using Domain.Sessions;

namespace UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="ISessionTokenRepository"/>, holding the one behaviour that matters about the
/// discovery read: a handle is found by the digest of itself and by nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>No owner filter, and here that is not a limitation of the fake — it is the contract.</b>
/// <c>session_tokens</c> is <b>exempt</b> from row-level security precisely so this statement can run
/// on a connection that has published nobody, which is the whole of ADR 0019. A fake that narrowed on
/// an owner would model a table the production schema deliberately does not have, and would then be
/// green for a handler that published an identity before this read — the one arrangement the real
/// database answers with <c>22P02</c>.
/// </para>
/// <para>
/// <see cref="IdentityWhenFindByTokenHashWasEntered"/> is what makes that checkable from a unit test,
/// and it is the mirror of <see cref="InMemoryBudgetRepository.IdentityWhenFindFirstWasEntered"/> on
/// the other side of the publication.
/// </para>
/// </remarks>
public sealed class InMemorySessionTokenRepository : ISessionTokenRepository
{
    private readonly List<SessionToken> _tokens = [];
    private readonly List<Guid> _identityWhenFindByTokenHashWasEntered = [];
    private RecordingUserContextWriter? _observedWriter;

    /// <summary>How many times the discovery read was asked to run.</summary>
    /// <remarks>
    /// Recorded because "the handle was looked up once" and "the handle was looked up" are different
    /// sentences on a path that runs on every authenticated request in the product.
    /// </remarks>
    public int FindByTokenHashCallCount { get; private set; }

    /// <summary>
    /// The identity the session carried at the instant each <see cref="FindByTokenHashAsync"/> call was
    /// entered — the last id published, or <see cref="Guid.Empty"/> when nothing had been published
    /// yet. Empty list unless <see cref="ObservePublicationsDuring"/> armed it.
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.Empty"/> is the <em>expected</em> value here and the failure is a real id, which
    /// is the opposite of what the same member means on the two policed reads. An unset
    /// <c>app.current_user_id</c> reaches a policy as <c>''::uuid</c>; this table has no policy for it
    /// to reach, so an identity published above this call is not an error the database would report —
    /// it is an ordering that happens to work here and fails <c>22P02</c> nowhere near the line that
    /// caused it.
    /// </remarks>
    public IReadOnlyList<Guid> IdentityWhenFindByTokenHashWasEntered =>
        _identityWhenFindByTokenHashWasEntered;

    /// <summary>Stores a handle, as the establishing path wrote it beside its session.</summary>
    public void Seed(SessionToken token) => _tokens.Add(token);

    /// <summary>
    /// Arms the recording of <see cref="IdentityWhenFindByTokenHashWasEntered"/> against
    /// <paramref name="writer"/>.
    /// </summary>
    /// <remarks>
    /// Off unless a test asks for it, for the reason
    /// <see cref="InMemoryBudgetRepository.ObservePublicationsDuring"/> gives: a test that reads the
    /// ordering has said so in its own Arrange block.
    /// </remarks>
    public void ObservePublicationsDuring(RecordingUserContextWriter writer) => _observedWriter = writer;

    public Task<SessionToken?> FindByTokenHashAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);

        FindByTokenHashCallCount++;

        // Before the read, not after, for InMemoryBudgetRepository's reason: this stands in for the
        // identity the statement would have run under, and a value sampled once the call had returned
        // would include a publication the statement never saw.
        if (_observedWriter is not null)
        {
            _identityWhenFindByTokenHashWasEntered.Add(
                _observedWriter.Published.Count > 0 ? _observedWriter.Published[^1] : Guid.Empty);
        }

        // Compared by value rather than by reference, because the caller hands over a digest it
        // computed from the presented token and never the array that was stored. SingleOrDefault
        // matches the real table: token_hash is its primary key.
        SessionToken? token = _tokens.SingleOrDefault(
            stored => stored.TokenHash.Span.SequenceEqual(tokenHash));

        return Task.FromResult(token);
    }
}
