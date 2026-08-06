using Application.Passkeys.Verification;

namespace Application.Passkeys;

/// <summary>
/// An assertion the server would not accept, for any reason at all.
/// </summary>
/// <remarks>
/// <para>
/// One exception type for every way a sign-in can be refused — unknown credential, bad signature,
/// untrusted origin, spent or expired challenge, counter regression, user-handle mismatch — because
/// the response to all of them has to be byte-identical. A caller that can tell "no such credential"
/// from "wrong signature" can enumerate which handles are registered, and a caller that can tell
/// "counter regression" from "unknown credential" learns that a handle it stole is real.
/// </para>
/// <para>
/// <see cref="Reason"/> exists for the log and for nothing else. It is deliberately not a message a
/// person is meant to read: the operator needs to know which check refused, and the client must not.
/// </para>
/// </remarks>
public sealed class PasskeyVerificationException : Exception
{
    public PasskeyVerificationException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public PasskeyVerificationException(PasskeyVerificationFailure failure)
        : base(failure.ToString())
    {
        Reason = failure.ToString();
        Failure = failure;
    }

    /// <summary>Which check refused, in a form meant only for a log line.</summary>
    public string Reason { get; }

    /// <summary>
    /// The verifier's own refusal, where the refusal came from the verifier. Null for the refusals
    /// this layer reaches on its own — a handle nothing answers to, a challenge that was never live,
    /// a counter that failed to advance.
    /// </summary>
    public PasskeyVerificationFailure? Failure { get; }
}
