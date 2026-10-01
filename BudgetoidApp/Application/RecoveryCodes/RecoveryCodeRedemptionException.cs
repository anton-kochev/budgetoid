namespace Application.RecoveryCodes;

/// <summary>
/// A recovery code the server would not redeem, for any reason at all.
/// </summary>
/// <remarks>
/// <para>
/// One exception type for every way a redemption can be refused — a verifier that is not base64url, one
/// of the wrong width, one past the ceiling, one no row answers to, one already spent, and one spent by
/// a concurrent request while this one was asking — because the response to all of them has to be
/// byte-identical. A caller who can tell "no such code" from "that code was already used" has learned
/// that a value they presented was once real, which is exactly what somebody working through a
/// partially-observed recovery card wants to know; told apart from "that was not base64url" it says the
/// same about the encoding, and told apart from "too long" it hands out the width of a verifier for
/// free.
/// </para>
/// <para>
/// <b>Its own type rather than <c>PasskeyVerificationException</c>, and the argument is not tidiness.</b>
/// That exception's handler answers "The passkey could not be verified.", which on this route is a
/// <em>wrong</em> sentence rather than merely an uninformative one: it tells a person holding a card
/// that the thing they do not have is the thing that failed, and sends them to debug an authenticator
/// they have already lost. A wrong sentence is worse than a distinguishable one, and reusing that
/// exception is the shortest path to green for every refusal here — which is why it is closed with a
/// type of its own.
/// </para>
/// <para>
/// <see cref="Reason"/> exists for the log and for nothing else, exactly as its passkey counterpart's
/// does. It is deliberately not a message a person is meant to read: the operator needs to know which
/// check refused, and the client must not. Nothing here ever names a hash, a verifier, an account or a
/// collision — a refusal that mentioned a collision would say that two stored values met, which is a
/// fact about what is stored.
/// </para>
/// </remarks>
public sealed class RecoveryCodeRedemptionException : Exception
{
    public RecoveryCodeRedemptionException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    /// <summary>Which check refused, in a form meant only for a log line.</summary>
    public string Reason { get; }
}
