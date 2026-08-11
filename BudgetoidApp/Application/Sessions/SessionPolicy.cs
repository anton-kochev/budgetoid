using Domain.Sessions;

namespace Application.Sessions;

/// <summary>
/// The product's policy about the sessions it opens, in one place because the paths that open one have
/// to agree.
/// </summary>
public static class SessionPolicy
{
    /// <summary>
    /// How long a session opened by a credential its holder possesses lasts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than in Domain, and deliberately:</b> <see cref="Session.Establish"/> takes an
    /// expiry instead of computing one, because how long a session lasts is product policy and the
    /// domain holds invariants. ADR 0002 keeps policy above the invariants for exactly this reason —
    /// the bottom is the most expensive layer to change, and this number is one somebody will want to
    /// change. That argument is about the layer, not about the file, so it survives the move up here
    /// unchanged. It is not on <c>IPasskeyCeremonyPolicy</c> either: a session that expires after a
    /// different interval in one environment than another is a difference nobody meant.
    /// </para>
    /// <para>
    /// <b>One number, not one per path.</b> A passkey assertion, a recovery-code redemption and the
    /// re-establishment a regeneration performs all open a <see cref="SessionKind.Full"/> session, and
    /// each handler's remarks used to restate the interval and say that the three agreeing was a rule
    /// rather than a coincidence — a stated rule with nothing enforcing it. The equality is what the
    /// rule is <em>about</em>: both recovery paths exist for somebody who has just lost their
    /// authenticator, and a shorter session on either would quietly tell them that the way back in they
    /// were issued is worth less than the one they lost. Sharing the value is the only shape in which a
    /// single edit cannot separate them.
    /// </para>
    /// <para>
    /// Nothing about <em>when</em> a session is established is here. Each handler's call site sits
    /// where it does for a reason its own comments carry — after the signature verifies, after the code
    /// is spent, after the replacement set is saved — and those positions are security properties that
    /// do not travel with the number.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
}
