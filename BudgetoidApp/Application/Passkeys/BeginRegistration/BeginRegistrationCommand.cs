namespace Application.Passkeys.BeginRegistration;

/// <summary>
/// Asks for the credential creation options that open a registration ceremony.
/// </summary>
/// <remarks>
/// Carries nothing. The account a passkey is registered to is the signed-in one, read from the
/// ambient user context — a parameter naming it would be a parameter a caller could set to somebody
/// else's account.
/// </remarks>
public sealed record BeginRegistrationCommand;
