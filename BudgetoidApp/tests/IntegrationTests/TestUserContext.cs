using Application.Abstractions;

namespace IntegrationTests;

/// <summary>
/// Supplies an always-resolved fixed account to a hand-built <c>BudgetoidDbContext</c> and to the
/// handlers driven over it, standing in for the user session authentication publishes per request.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TestBudgetContext" />'s twin, and its remarks hold here unchanged: only
/// <see cref="IUserContext.ResolvedUserId" /> is implemented so that <c>UserId</c> keeps coming from
/// the interface's own derivation — a hand-written copy would silently override it, and a double whose
/// two accessors could name different accounts is the exact drift the derivation exists to prevent.
/// </para>
/// <para>
/// <b>It is what makes <c>SessionContextInterceptor</c> usable from a test</b>, which is why it arrives
/// with the first suite that drives a policed write through a context it built itself. That interceptor
/// takes both contexts and writes both settings on every connection open; a test holding only a budget
/// could not configure one, and an app-role statement on a session naming no user meets <c>22P02</c>
/// rather than reading the wrong rows.
/// </para>
/// </remarks>
public sealed class TestUserContext(Guid userId) : IUserContext
{
    public Guid? ResolvedUserId { get; } = userId;
}
