using Application.Abstractions;
using Domain.Sessions;

namespace Application.Sessions.RevokeSessionsForCredential;

/// <summary>Revokes every live session a single credential established, and reports how many.</summary>
/// <remarks>
/// Thin on purpose, and not redundant with the repository call it wraps: the clock is read here so one
/// sweep is stamped with one instant, where a repository reading its own <c>now</c> per row would
/// spread a single decision to end access across several instants nobody could later tell apart from
/// sessions that genuinely ended at different times. This is the application half of "revoking a
/// credential ends its sessions", and it has exactly one caller: <c>RevokePasskeyHandler</c>, which
/// calls it before deleting the credential row. The <see langword="int" /> it returns is the only
/// observable evidence the explicit revocation ran at all, since the cascade from <c>credentials</c>
/// removes the same rows either way.
/// </remarks>
public sealed class RevokeSessionsForCredentialHandler(
    ISessionRepository repository,
    TimeProvider timeProvider) : ICommandHandler<RevokeSessionsForCredentialCommand, int>
{
    public Task<int> HandleAsync(
        RevokeSessionsForCredentialCommand command,
        CancellationToken cancellationToken = default)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        return repository.RevokeForCredentialAsync(command.CredentialId, now, cancellationToken);
    }
}
