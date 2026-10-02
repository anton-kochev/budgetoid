using Domain.Sessions;

namespace Application.Sessions.ReadSession;

/// <summary>
/// What a caller is told about the session it holds, and nothing else about the account.
/// </summary>
/// <param name="Kind">Whether the session reads budget content (<see cref="SessionKind.Full" />) or
/// offers only the release valve (<see cref="SessionKind.Locked" />).</param>
/// <param name="ExpiresAtUtc">The stored row's expiry, in UTC — never recomputed from
/// <see cref="SessionPolicy" />.</param>
/// <param name="ErasureTakesEffectAtUtc">When the account's scheduled erasure takes effect, in UTC, or
/// <see langword="null" /> when the account holds none.</param>
/// <remarks>
/// No session, account or budget id, no address and no credential: the route is reachable by a caller
/// who has proved nothing but a provider sign-in.
/// </remarks>
public sealed record SessionSummary(SessionKind Kind, DateTime ExpiresAtUtc, DateTime? ErasureTakesEffectAtUtc);
