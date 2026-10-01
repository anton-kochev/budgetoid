using Domain.Common;

namespace Domain.Users;

/// <summary>
/// The highest signature counter an authenticator has reported for one passkey — the record a cloned
/// authenticator gives itself away against.
/// </summary>
/// <remarks>
/// Kept beside <see cref="PasskeyPublicKey"/> rather than on it: the key is written once at
/// registration and the counter is the one value a sign-in may move, and separating them is what
/// lets the write grant name a single column.
/// </remarks>
public sealed class PasskeySignatureCounter
{
    private PasskeySignatureCounter()
    {
    }

    public Guid CredentialId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>
    /// The type of the credential this counter belongs to, carried on the row for the reason
    /// <see cref="PasskeyPublicKey.CredentialType"/> gives.
    /// </summary>
    public CredentialType CredentialType { get; private set; }

    public uint Value { get; private set; }

    /// <summary>
    /// Opens the counter for <paramref name="credential"/> at the value its registration reported.
    /// </summary>
    public static PasskeySignatureCounter Start(Credential credential, uint value)
    {
        ArgumentNullException.ThrowIfNull(credential);

        Dictionary<string, string[]> errors = new();

        // A counter filed against a federated credential would be a clone check on a credential no
        // authenticator ever signs with, which is a check that can only ever pass.
        if (credential.Type != CredentialType.Passkey)
        {
            errors[nameof(CredentialType)] = ["A signature counter may only be kept for a passkey credential."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new PasskeySignatureCounter
        {
            CredentialId = credential.Id,
            UserId = credential.UserId,
            CredentialType = credential.Type,
            Value = value,
        };
    }

    /// <summary>
    /// Takes the counter an assertion reported and returns whether the stored value moved, so the
    /// caller writes only when something changed.
    /// </summary>
    /// <exception cref="ValidationException">
    /// The reported counter did not advance, which is what a replayed or cloned authenticator
    /// produces.
    /// </exception>
    public bool Accept(uint reported)
    {
        // Not a loophole in the monotonic rule: an authenticator backing a synced passkey has no
        // per-device counter to increment and reports zero every time, so refusing a repeated zero
        // would refuse the majority of real passkeys. Once either side is non-zero the authenticator
        // has shown it counts, and a counter that then fails to advance is the signature of a clone.
        if (Value == 0 && reported == 0)
        {
            return false;
        }

        if (reported <= Value)
        {
            // The stored value is left where it was: writing the lower number would hand a clone a
            // counter it can now advance past.
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(Value)] = ["The reported signature counter did not advance."],
            });
        }

        Value = reported;

        return true;
    }
}
