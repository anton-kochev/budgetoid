using Domain.Users;

namespace UnitTests.Fakes;

/// <summary>
/// An <see cref="IRegistrationRepository" /> that writes nothing and keeps two things about every call:
/// what it was handed, and what the request's identity looked like at the moment it was handed it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The second member is the reason this fake exists rather than a bare recorder.</b>
/// <c>RegisterAccountHandler</c> publishes the derived account identifier at rung 13 and calls this port
/// at rung 14, and that order is a correctness rule with a silent failure: an identity published
/// afterwards is an identity the <c>users</c> INSERT ran without, so every policed row of the save meets
/// <c>''::uuid</c> and the request dies with <c>22P02</c>. Nothing about the handler's return value
/// changes when the two are swapped, so a fake that only counted calls would be green either way.
/// A snapshot taken <em>inside</em> the call is the only place in a unit test where the ordering is
/// observable.
/// </para>
/// <para>
/// The snapshot arrives as a <see cref="Func{TResult}" /> rather than as the writer itself, so the two
/// fakes can be constructed in either order and so this one carries no opinion about which writer a
/// caller uses. It also keeps this type from reaching into a collaborator it is not implementing.
/// </para>
/// <para>
/// It <b>writes nothing</b>, deliberately: the rows one registration produces are asserted end to end in
/// <c>IntegrationTests.AccountRegistrationTests</c>, against the real least-privilege connection where a
/// missing grant is a <c>42501</c> and a mis-ordered transaction is a <c>22P02</c>. Reproducing thirty
/// rows in memory here would be a second, weaker copy of that claim, able to disagree with the database
/// about what a save does.
/// </para>
/// </remarks>
/// <param name="outcome">What the save comes to. Defaults to the one that lands every row.</param>
/// <param name="publishedSoFar">
/// Reads the identities published so far, evaluated at the instant of each call.
/// </param>
public sealed class RecordingRegistrationRepository(
    RegistrationOutcome outcome = RegistrationOutcome.Registered,
    Func<IReadOnlyList<Guid>>? publishedSoFar = null) : IRegistrationRepository
{
    private readonly List<Registration> _calls = [];
    private readonly List<IReadOnlyList<Guid>> _identitiesAtCall = [];

    /// <summary>Every registration handed to this port, oldest first.</summary>
    public IReadOnlyList<Registration> Calls => _calls;

    /// <summary>
    /// The identities the request had published when each call arrived, one entry per call.
    /// </summary>
    /// <remarks>
    /// An empty entry means the save was attempted before anybody was named, which is the ordering
    /// failure this fake was built to see.
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<Guid>> IdentitiesPublishedAtEachCall => _identitiesAtCall;

    public Task<RegistrationOutcome> RegisterAsync(
        Registration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        _calls.Add(registration);

        // Copied rather than kept as a live reference: the writer's list goes on growing, and an entry
        // that changed after the call would report the state at assertion time instead of at call time —
        // which is exactly the distinction this member exists to make.
        _identitiesAtCall.Add([.. publishedSoFar?.Invoke() ?? []]);

        return Task.FromResult(outcome);
    }
}
