using Application.Sessions.ReadSession;

namespace Application.Sessions.EstablishLockedSession;

/// <summary>
/// What a locked sign-in came to: a session established, or no account under the provider identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>An unknown subject is an answer, not a fault</b>, so it is a case of this type rather than an
/// exception: the route turns it into a 404 naming <c>no_account</c>, which a client acts on by offering
/// registration. An exception would route it through a handler built for refusals that leave something
/// to correct.
/// </para>
/// <para>
/// <b>Two cases, and not sealed shut.</b> The constructor is private, but a record also synthesizes a
/// protected copy constructor, so a record in another assembly can still derive a third case from an
/// existing value. That is why the endpoint's switch keeps a default arm, and why that arm throws rather
/// than answering anything.
/// </para>
/// </remarks>
public abstract record LockedSignInOutcome
{
    private LockedSignInOutcome()
    {
    }

    /// <summary>A locked session was written, and this is what the caller is told and handed.</summary>
    /// <param name="Session">The session's kind and expiry as stored, and the account's scheduled erasure
    /// instant or <see langword="null"/> — the same three facts <c>GET /api/me/session</c> answers.</param>
    /// <param name="Handoff">The handle the session is presented by. It travels beside
    /// <paramref name="Session"/> and never inside it, for the reason <see cref="Issued{TResult}"/>
    /// gives: the endpoint writes it into the cookie and maps the rest onto the wire.</param>
    public sealed record Established(SessionSummary Session, SessionHandoff Handoff) : LockedSignInOutcome;

    /// <summary>No account holds a federated credential under the presented provider identity.</summary>
    /// <remarks>The handler published nothing and wrote nothing. A request that arrived carrying a live
    /// locked cookie still holds the account the cookie scheme published for it.</remarks>
    public sealed record NoAccount : LockedSignInOutcome;
}
