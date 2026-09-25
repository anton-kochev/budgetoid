using Application.Users;

namespace UnitTests.Fakes;

/// <summary>
/// The stored email addresses of a handful of accounts, keyed by user id, for a unit-tested handler.
/// An id nothing was seeded for answers <see langword="null" />, which is the "no user row" arm of
/// <see cref="IUserAccountReadService.FindEmailAsync" />.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by id rather than answering one fixed address to every caller, and that is the whole reason
/// it is a dictionary: a handler that asked with something other than the identity it was given —
/// <see cref="Guid.Empty" />, a freshly minted id, the first row of anything — would be answered
/// <see langword="null" /> here and fall into the throw, instead of receiving the one address a
/// single-valued stub would hand to anybody who asked.
/// </para>
/// <para>
/// The isolation the real service relies on is deliberately not modelled. <c>users</c> is policed by
/// <c>user_isolation</c>, so in production a foreign id comes back empty because the database refuses
/// to show the row, not because a lookup written here missed — and a fake that re-implemented that
/// refusal in C# would be asserting the policy exists by restating it. The policy is covered by the
/// row-level-security tests, which read a real database.
/// </para>
/// </remarks>
public sealed class StubUserAccountReadService(params (Guid UserId, string Email)[] rows)
    : IUserAccountReadService
{
    private readonly Dictionary<Guid, string> _addresses =
        rows.ToDictionary(row => row.UserId, row => row.Email);

    public Task<string?> FindEmailAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(_addresses.GetValueOrDefault(userId));
}
