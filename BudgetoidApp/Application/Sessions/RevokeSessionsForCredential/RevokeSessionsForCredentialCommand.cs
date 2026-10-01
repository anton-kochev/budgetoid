namespace Application.Sessions.RevokeSessionsForCredential;

/// <summary>Ends the sessions the credential named by <paramref name="CredentialId"/> established.</summary>
/// <remarks>
/// Keyed on a credential, not on the user who owns it. Withdrawing one way into an account must leave
/// the others signed in; keyed on the user this would be the same code with the rule inverted, and a
/// routine credential cleanup would sign the holder out of the device in their hand.
/// </remarks>
public sealed record RevokeSessionsForCredentialCommand(Guid CredentialId);
