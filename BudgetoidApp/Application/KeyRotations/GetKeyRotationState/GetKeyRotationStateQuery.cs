namespace Application.KeyRotations.GetKeyRotationState;

/// <summary>
/// Asks what content-key rotation the authenticated account has in flight, so a client that lost the
/// generation it was rewriting under can pick the run back up.
/// </summary>
/// <remarks>
/// <para>
/// <b>It declares no member, the shape every neighbouring query keeps.</b> <c>GetAccountKeysQuery</c>,
/// <c>ListCredentialsQuery</c>, <c>CountRecoveryCodesQuery</c> and <c>ExportDataQuery</c> each declare
/// none for one reason: what they are scoped to is the account, which is read from
/// <see cref="Application.Abstractions.IUserContext.UserId" />, and a member here would be a tenancy
/// parameter with no ownership check to pair with it.
/// </para>
/// <para>
/// <b>No member may name a rotation either, and that is the one a reader will reach for.</b> A
/// <c>rotationId</c> would read as "tell me about this run", and there is only ever one —
/// <c>key_rotations.user_id</c> is the primary key — so the identifier could only ever be checked
/// against the account's own staged row and would add a way to be told <em>nothing</em> for a run that
/// is genuinely in flight. A client resuming after a reload holds no identifier at all: losing it is
/// most of what it means to have been interrupted.
/// </para>
/// </remarks>
public sealed record GetKeyRotationStateQuery;
