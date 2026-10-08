using Domain.Common;

namespace Domain.Users;

/// <summary>
/// The public key an authenticator handed over when a passkey was registered, together with the
/// WebAuthn credential id the authenticator answers to and the algorithm its signatures are verified
/// with.
/// </summary>
/// <remarks>
/// Its own entity referencing its credential and its user by id, for the reason
/// <see cref="Domain.Sessions.Session"/> gives: hanging it off <see cref="User"/> would grow a root
/// that is loaded on every authenticated request.
/// </remarks>
public sealed class PasskeyPublicKey
{
    /// <summary>
    /// Below this an identifier could not have come from a conforming authenticator, and accepting
    /// it would let a guessable value be filed as a credential handle.
    /// </summary>
    public const int MinWebAuthnCredentialIdLength = 16;

    /// <summary>The ceiling WebAuthn itself puts on a credential id.</summary>
    public const int MaxWebAuthnCredentialIdLength = 1023;

    public const int MaxCoseKeyLength = 1024;

    private PasskeyPublicKey()
    {
    }

    public Guid CredentialId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>
    /// The type of the credential this key was registered against, carried on the row so the
    /// database can check that it is a passkey instead of trusting that every INSERT went through
    /// <see cref="Register"/>.
    /// </summary>
    public CredentialType CredentialType { get; private set; }

    /// <summary>The handle the authenticator answers to, as the client returned it.</summary>
    public ReadOnlyMemory<byte> WebAuthnCredentialId { get; private set; }

    public ReadOnlyMemory<byte> CoseKey { get; private set; }

    public CoseAlgorithm Algorithm { get; private set; }

    /// <summary>
    /// Records the key material an authenticator produced for <paramref name="credential"/>.
    /// </summary>
    public static PasskeyPublicKey Register(
        Credential credential,
        ReadOnlyMemory<byte> webAuthnCredentialId,
        ReadOnlyMemory<byte> coseKey,
        CoseAlgorithm algorithm)
    {
        // The owner and the type are both read off the credential, so there is nothing to register
        // without one. No user typed this; a caller handed over nothing.
        ArgumentNullException.ThrowIfNull(credential);

        Dictionary<string, string[]> errors = new();

        // A key filed under a credential the identity provider owns would let a provider sign-in be
        // verified as if an authenticator had signed it.
        if (credential.Type != CredentialType.Passkey)
        {
            errors[nameof(CredentialType)] = ["A public key may only be registered against a passkey credential."];
        }

        if (webAuthnCredentialId.Length is < MinWebAuthnCredentialIdLength or > MaxWebAuthnCredentialIdLength)
        {
            errors[nameof(WebAuthnCredentialId)] =
            [
                $"WebAuthn credential id must be between {MinWebAuthnCredentialIdLength} and "
                + $"{MaxWebAuthnCredentialIdLength} bytes.",
            ];
        }

        if (coseKey.Length is 0 or > MaxCoseKeyLength)
        {
            errors[nameof(CoseKey)] = [$"COSE key must be between 1 and {MaxCoseKeyLength} bytes."];
        }

        // An enum holds any value of its underlying type, so a COSE header naming an algorithm the
        // product cannot verify arrives here as a perfectly valid CoseAlgorithm. Storing it would
        // move the failure from registration to sign-in, which is the expensive end to find out.
        if (!Enum.IsDefined(algorithm))
        {
            errors[nameof(Algorithm)] = ["Algorithm must be one the product verifies."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new PasskeyPublicKey
        {
            CredentialId = credential.Id,
            UserId = credential.UserId,
            CredentialType = credential.Type,

            // Copied, not aliased. A ReadOnlyMemory<byte> is a view over an array the caller still
            // owns; a caller reusing or returning that buffer would otherwise rewrite a stored
            // public key from a distance, and the credential would stop matching the authenticator
            // that produced it with nothing in the code path to point at.
            WebAuthnCredentialId = webAuthnCredentialId.ToArray(),
            CoseKey = coseKey.ToArray(),
            Algorithm = algorithm,
        };
    }
}
