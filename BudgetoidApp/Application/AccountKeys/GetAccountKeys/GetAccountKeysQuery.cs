namespace Application.AccountKeys.GetAccountKeys;

/// <summary>
/// Asks for the wrapped account keys of every factor the authenticated account holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>It declares no member, which is now the same shape every neighbouring query keeps.</b>
/// <c>ListCredentialsQuery</c>, <c>GetSignedInUserQuery</c>, <c>CountRecoveryCodesQuery</c> and
/// <c>ExportDataQuery</c> each declare none for one reason: the thing they are scoped to is the account,
/// which is read from <see cref="Application.Abstractions.IUserContext.UserId" />, and a member here
/// would be a tenancy parameter with no ownership check to pair with it. This query is scoped to exactly
/// the same thing, so it says exactly the same nothing.
/// </para>
/// <para>
/// <b>It used to carry a session id, and dropping it is the point rather than a tidy-up.</b> The answer
/// was narrowed to the credential that opened the session, and the session id was the only way to learn
/// which credential that was. That narrowing is wrong — <c>GetAccountKeysHandler</c> carries the reason
/// — and once the answer is keyed on the account there is nothing left for the session to supply. What
/// is gained is not a shorter type: a query with no member cannot be handed the wrong id, cannot be
/// built from a claim that was never read, and gives a future reader nothing to key a second scoping on.
/// </para>
/// <para>
/// <b>No member may be added naming a user, a session, a credential or a factor.</b> Each would be a
/// caller-chosen identifier on the one read that hands back an account's key custody, and the read
/// service it reaches takes its owner argument from the resolved context precisely so that no request can
/// name one.
/// </para>
/// </remarks>
public sealed record GetAccountKeysQuery;
