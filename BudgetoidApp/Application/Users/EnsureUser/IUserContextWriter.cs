namespace Application.Users.EnsureUser;

/// <summary>
/// Publishes the identity a request was resolved to, and the budget it runs against, so the next
/// connection the request opens can carry both into the <c>user_isolation</c> and
/// <c>budget_isolation</c> row-level security policies.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <c>IUserContext</c> and <c>IBudgetContext</c>, and deliberately declared
/// beside its consumers rather than next to them. Merged, every collaborator that only needed to read
/// the identity or the ambient budget would also hold the capability to reassign it — the exact
/// capability those policies exist to constrain. Split, that capability is visible in the constructors
/// that ask for it.
/// </para>
/// <para>
/// Both members live here rather than one here and a budget writer elsewhere, because
/// <see cref="ResolveUser" /> clears the budget: an interface that could only clear it leaves a caller
/// needing to name one no way to do so but to reach past this type and assign the request-scoped state
/// itself — and a second writer is what makes the clearing rule apply only to the publications that
/// happen to go through the first.
/// </para>
/// </remarks>
public interface IUserContextWriter
{
    /// <summary>Names <paramref name="userId"/> as the identity for the rest of this request.</summary>
    /// <remarks>
    /// Clears the ambient budget as well — see the implementation for why a republished identity must
    /// not keep the previous one's tenant standing beside it.
    /// </remarks>
    void ResolveUser(Guid userId);

    /// <summary>
    /// Names <paramref name="budgetId"/> as the ambient budget for the rest of this request, leaving
    /// the published identity as it stands.
    /// </summary>
    /// <remarks>
    /// <b>Call this after <see cref="ResolveUser" />, never before.</b> That call clears the budget, so
    /// a budget named first is a budget the rest of the request does not have, and every budget-scoped
    /// statement below it meets an unresolved budget. <b>Nothing enforces the order</b> — not a type,
    /// not the container, not the database; it holds only because each caller writes it that way, and
    /// <c>UserProvisioningWriterTests</c> is the one place it is pinned. A caller that reverses it gets
    /// a loud failure rather than a silent mis-scope, which is why the contract is allowed to live in a
    /// doc comment at all.
    /// </remarks>
    void ResolveBudget(Guid budgetId);
}
