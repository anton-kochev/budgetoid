using Domain.Sessions;

namespace Application.Passkeys.CompleteAssertion;

/// <summary>
/// The session a verified assertion opened.
/// </summary>
/// <remarks>
/// The session's id is deliberately absent. What the caller needs to know is how much of the account
/// this sign-in reaches and until when; the row's identifier answers neither, and handing it over
/// would give the client a stable handle to a session that nothing authenticates the holder of.
/// </remarks>
public sealed record EstablishedSession(SessionKind Kind, DateTime ExpiresAtUtc);
