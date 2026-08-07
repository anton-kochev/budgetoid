namespace Application.Users.EnsureUser;

/// <summary>
/// Asks which account, if any, a provider subject already resolves to.
/// </summary>
/// <remarks>
/// <b>No email member, and its absence is the design rather than an omission.</b> This path never
/// writes a <c>users</c> row, so an address here would be a value carried the length of a request for
/// nothing to read — and the moment it exists, the next change to the handler can quietly start
/// writing it. That is a real data-minimisation property and not only tidiness: the email claim is read
/// on every authenticated request by the middleware's claim gate, and it reaches a handler only on the
/// route groups that may create an account.
/// </remarks>
public sealed record ResolveUserCommand(string GoogleSubject);
