namespace Application.RecoveryCodes.RedeemRecoveryCode;

/// <summary>
/// One recovery code being presented, as the verifier the client derived from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One member, and there is nothing else a redemption may carry.</b> No account id, no email, no
/// credential id: each would be a value the server would have to either ignore or trust, and trusting
/// one would let an anonymous caller name the account a code is matched against. The account this
/// sign-in lands on is discovered from the code and from nothing the request said.
/// </para>
/// <para>
/// <b>The server never sees a code.</b> The browser mints one, derives a verifier <c>V = HKDF(code, …)</c>
/// from it and sends only <c>V</c>; <c>recovery_code_hashes</c> stores <c>SHA-256(V)</c>. The account's
/// key-encryption key is derived from the same code on an independent HKDF branch, so a code on the
/// wire would hand the operator that key. See <see cref="Domain.Users.RecoveryCodeHash"/>.
/// </para>
/// <para>
/// <b>Nullable, and deliberately so</b>, for the reason the generation route's request record states: a
/// <c>required</c> member would buy a framework 400 that tells an anonymous caller the server has an
/// opinion about the member's shape before it has refused them, and it would be a second answer this
/// route can give. A body of <c>{}</c> binds this to <see langword="null"/> and lands on the same
/// refusal every wrong verifier lands on.
/// </para>
/// </remarks>
/// <param name="Verifier">The base64url verifier, as the browser derived and sent it.</param>
public sealed record RedeemRecoveryCodeCommand(string? Verifier);
