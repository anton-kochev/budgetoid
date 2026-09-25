namespace Application.RecoveryCodes.CountRecoveryCodes;

/// <summary>
/// Asks how many recovery codes the current request's account has left.
/// </summary>
/// <remarks>
/// <b>No member, and none may be added.</b> The account counted is whichever one the request is
/// authenticated as, read from <see cref="Application.Abstractions.IUserContext.UserId" />. A user id
/// declared here would be a tenancy parameter with no ownership check to pair with it — the same rule
/// <c>ListCredentialsQuery</c>, <c>GetSignedInUserQuery</c> and <c>EraseAccountCommand</c> carry. It
/// bites at least as hard here as on the credential list: <c>recovery_code_hashes</c> is exempt from
/// row-level security, so a caller-supplied id would reach a read that nothing beneath the application
/// narrows.
/// </remarks>
public sealed record CountRecoveryCodesQuery;
