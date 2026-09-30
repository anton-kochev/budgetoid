using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IEmailChangeRepository"/> that records every user id it is handed and applies a
/// change the way the real save would, unless told to answer with a refusal instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>A refusal writes nothing here</b>, which is what the real save does: the unique index or the
/// concurrency check refuses the whole <c>SaveChanges</c>, so no row moves. The handler's own writes
/// before the save — the session sweep — are not this fake's to undo; a test that cares whether they
/// committed asks the executor.
/// </para>
/// <para>
/// The shared call log, when one is handed in, is what lets a test order this fake's save against
/// another fake's write. Two call counters compared afterwards say both things happened, never which
/// came first.
/// </para>
/// </remarks>
/// <param name="callLog">An ordered log shared with other fakes, or <see langword="null"/>.</param>
public sealed class InMemoryEmailChangeRepository(List<string>? callLog = null) : IEmailChangeRepository
{
    /// <summary>The entry <see cref="ApplyAsync"/> writes to the shared call log.</summary>
    public const string ApplyEntry = "apply";

    private readonly List<User> _users = [];
    private readonly List<Credential> _credentials = [];
    private readonly List<Guid> _userIdsReceived = [];
    private readonly List<FederatedIdentityChange> _applyCalls = [];

    /// <summary>What <see cref="ApplyAsync"/> answers. Anything but applied writes nothing.</summary>
    public EmailChangeOutcome Outcome { get; set; } = EmailChangeOutcome.Applied;

    /// <summary>
    /// Runs the moment <see cref="ApplyAsync"/> is entered — a racing request committing between this
    /// request's reads and its save.
    /// </summary>
    public Action? OnApply { get; set; }

    /// <summary>Every user id any member was handed, in call order.</summary>
    public IReadOnlyList<Guid> UserIdsReceived => _userIdsReceived;

    /// <summary>Every change <see cref="ApplyAsync"/> was handed, in call order.</summary>
    public IReadOnlyList<FederatedIdentityChange> ApplyCalls => _applyCalls;

    /// <summary>The credentials this fake holds now.</summary>
    public IReadOnlyList<Credential> Credentials => _credentials;

    /// <summary>How many times any member was called.</summary>
    public int CallCount => _userIdsReceived.Count;

    public void Seed(User user, params Credential[] credentials)
    {
        _users.Add(user);
        _credentials.AddRange(credentials);
    }

    public Task<User?> FindUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _userIdsReceived.Add(userId);

        return Task.FromResult(_users.SingleOrDefault(user => user.Id == userId));
    }

    public Task<Credential?> FindFederatedCredentialAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        _userIdsReceived.Add(userId);

        // Owner and type, the two predicates the real read carries: credentials is exempt from
        // row-level security, so the owner is the only thing scoping it.
        Credential? credential = _credentials.SingleOrDefault(credential =>
            credential.UserId == userId && credential.Type == CredentialType.Federated);

        return Task.FromResult(credential);
    }

    public Task<EmailChangeOutcome> ApplyAsync(
        FederatedIdentityChange change,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        _userIdsReceived.Add(userId);
        _applyCalls.Add(change);
        callLog?.Add(ApplyEntry);

        OnApply?.Invoke();

        if (Outcome is not EmailChangeOutcome.Applied)
        {
            return Task.FromResult(Outcome);
        }

        if (change.Retired is not null)
        {
            _credentials.RemoveAll(credential => credential.Id == change.Retired.Id);
        }

        if (change.Filed is not null)
        {
            _credentials.Add(change.Filed);
        }

        _users.Single(user => user.Id == userId).ChangeEmail(change.Email.Value);

        return Task.FromResult(EmailChangeOutcome.Applied);
    }
}
