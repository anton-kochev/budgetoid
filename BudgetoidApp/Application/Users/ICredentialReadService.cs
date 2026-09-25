using Application.Users.ListCredentials;

namespace Application.Users;

/// <summary>
/// Reads about the account's credentials that are for showing, not for deciding.
/// </summary>
/// <remarks>
/// <para>
/// A read service rather than a method on <c>IPasskeyRepository</c>, the same split
/// <see cref="IUserAccountReadService" /> states: the repository loads aggregates that rules are then
/// applied to, and this projects columns for a response. <b>The last-passkey rule keeps reading through
/// <c>IPasskeyRepository.CountPasskeysForUserAsync</c> and must never be moved onto this projection</b>
/// — a rule keyed on a value chosen for display is precisely what the split exists to prevent, and
/// <c>users-and-ownership.md</c> records counting through this list as the counterexample.
/// </para>
/// <para>
/// Every read here is scoped by its owner argument and by <b>nothing beneath it</b>: <c>credentials</c>
/// is exempt from row-level security, because it is the table a request is resolved <em>out of</em>.
/// See <see cref="ListForUserAsync" />.
/// </para>
/// </remarks>
public interface ICredentialReadService
{
    /// <summary>
    /// Every credential <paramref name="userId" /> holds — the one federated credential and every
    /// passkey beside it — ascending by the instant each was registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>CreatedAtUtc</c> ascending is the contract, and <c>Id</c> is <b>not</b> promised as a
    /// tiebreaker: <c>IExportReadService</c> states that rule for every collection in the export and
    /// gives the reason — <c>uuid</c> collation is provider-defined, so two rows sharing an instant may
    /// order one way here and another way under a second implementation with neither being wrong.
    /// </para>
    /// <para>
    /// <b>The owner argument is the only thing that scopes this read.</b> <c>credentials</c> is exempt
    /// from row-level security, so no policy narrows it, no query filter narrows it, and the grant is on
    /// the whole table — the exemption is what the identity of a request is resolved through, not a gap.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<CredentialSummary>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
