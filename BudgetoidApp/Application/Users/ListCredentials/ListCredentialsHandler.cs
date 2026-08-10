using Application.Abstractions;

namespace Application.Users.ListCredentials;

/// <summary>
/// Answers the signed-in account's own credentials.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes no <c>ILogger</c>, and must never take one</b>, for the reason
/// <c>GetSignedInUserHandler</c> gives about the address it holds: the identifiers on this path name an
/// account's authenticators, and a log line copies them into a sink with a different retention policy
/// and a different audience from the table they came from. No gate anywhere reads a log line from here,
/// so there is nothing to trade against.
/// </para>
/// <para>
/// An empty list is not a state the product can be left in <em>at rest</em> — an account and its first
/// federated credential go in one save — so an empty answer means the account was erased between the
/// middleware resolving it and this read. It is returned rather than turned into a refusal: unlike the
/// email <c>GetSignedInUserHandler</c> cannot invent, an empty collection is the honest shape of "no
/// credentials came back", and a handler that threw on it would be a rule keyed on a read taken for
/// display.
/// </para>
/// </remarks>
public sealed class ListCredentialsHandler(
    IUserContext userContext,
    ICredentialReadService readService)
    : IQueryHandler<ListCredentialsQuery, IReadOnlyList<CredentialSummary>>
{
    public async Task<IReadOnlyList<CredentialSummary>> HandleAsync(
        ListCredentialsQuery query,
        CancellationToken cancellationToken = default)
    {
        Guid userId = userContext.UserId;

        return await readService.ListForUserAsync(userId, cancellationToken);
    }
}
