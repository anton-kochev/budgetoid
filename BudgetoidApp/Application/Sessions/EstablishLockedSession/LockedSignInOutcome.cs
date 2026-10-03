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
/// <b>Closed.</b> The constructor is private, so the two nested cases are the only values there are and
/// a switch over them needs no discard arm that could quietly admit a third.
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
    /// <remarks>Nothing was published and nothing was written.</remarks>
    public sealed record NoAccount : LockedSignInOutcome;
}
