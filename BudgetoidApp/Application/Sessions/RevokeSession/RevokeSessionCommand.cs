namespace Application.Sessions.RevokeSession;

/// <summary>Ends the single session named by <paramref name="SessionId"/>.</summary>
/// <remarks>
/// <para>
/// <b>One session, and never the account's others.</b> Signing out on a laptop must leave the phone in
/// somebody's pocket signed in; keyed on the user this would be the same code with the rule inverted,
/// and every test in a single-session account would still pass under it. That is the same trap
/// <see cref="Application.Sessions.RevokeSessionsForCredential.RevokeSessionsForCredentialCommand"/>
/// names one level up, where the key is a credential rather than a session.
/// </para>
/// <para>
/// <b>The id is not the caller's to choose.</b> It is read off the claim the authentication of this
/// very request produced, never off the request body or the route — so there is no id here for a
/// caller to substitute. What would refuse a substituted one anyway is <c>user_isolation</c> on
/// <c>sessions</c>, one layer below: a stranger's session is not found and cannot be written.
/// </para>
/// </remarks>
public sealed record RevokeSessionCommand(Guid SessionId);
