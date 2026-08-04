namespace Application.Users.EnsureUser;

/// <summary>
/// Publishes the identity a request was resolved to, so the next connection the request opens can
/// carry it into the <c>user_isolation</c> row-level security policies.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>IUserContext</c>, and deliberately declared beside its single
/// consumer rather than next to it. Merged, every collaborator that only needed to read the identity
/// would also hold the capability to reassign it — the exact capability those policies exist to
/// constrain. Split, that capability is visible in one constructor.
/// </remarks>
public interface IUserContextWriter
{
    /// <summary>Names <paramref name="userId"/> as the identity for the rest of this request.</summary>
    void ResolveUser(Guid userId);
}
