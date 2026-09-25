using Application.Abstractions;

namespace UnitTests.Fakes;

/// <summary>
/// An always-resolved signed-in user for a unit-tested handler. Handlers only ever read the id, so a
/// fixed value is enough; the request-time resolution of that id is covered by the provisioning
/// tests.
/// </summary>
/// <remarks>
/// The unresolved state — <see cref="IUserContext.ResolvedUserId" /> coming back
/// <see langword="null" /> — is deliberately not modelled here. It belongs to the request-scoped
/// implementation, which is where a request can genuinely reach a handler before an identity exists.
/// Only <see cref="IUserContext.ResolvedUserId" /> is implemented so that <c>UserId</c> keeps coming
/// from the interface's own derivation: a hand-written copy here would silently override it, and a
/// stub whose two accessors could name different users than production's do is the exact drift the
/// derivation exists to prevent.
/// </remarks>
public sealed class StubUserContext(Guid userId) : IUserContext
{
    public Guid? ResolvedUserId { get; } = userId;
}
