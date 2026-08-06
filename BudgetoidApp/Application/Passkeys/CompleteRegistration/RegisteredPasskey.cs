namespace Application.Passkeys.CompleteRegistration;

/// <summary>
/// What a completed registration reports back.
/// </summary>
/// <param name="PrfEnabled">
/// Whether the client said the <c>prf</c> extension was enabled for the new credential, or null when
/// it reported nothing about the extension at all.
/// </param>
/// <remarks>
/// <para>
/// <b>Nothing about <c>prf</c> is stored.</b> The claim arrives in the client extension results: it
/// is asserted by the client, it is covered by no signature, the server cannot reproduce it, and the
/// PRF output itself never leaves the client. A column holding it would be a column recording an
/// unverifiable assertion, which is data nothing can act on.
/// </para>
/// <para>
/// It is reported instead, which is what lets the ceremony demonstrate the server observed the claim
/// without the server having believed it. A later change deciding what this may gate has to start
/// from that: this value proves nothing about the authenticator, and any rule keyed on it is a rule a
/// client can satisfy by saying so. If key custody ever has to depend on PRF, what establishes it is
/// a value derived through PRF that the server can check — not this flag.
/// </para>
/// </remarks>
public sealed record RegisteredPasskey(bool? PrfEnabled);
