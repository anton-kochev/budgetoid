namespace Application.Users.EraseAccount;

/// <summary>
/// Asks for the signed-in account, and everything owned beneath it, to be erased.
/// </summary>
/// <remarks>
/// Parameterless on purpose, and it must stay that way. The only identity the handler may act on is
/// <see cref="Application.Abstractions.IUserContext.UserId"/>; a user id declared here would be an
/// account a caller could name, and the endpoint would be one deserialized field away from erasing
/// somebody else's.
/// </remarks>
public sealed record EraseAccountCommand;
