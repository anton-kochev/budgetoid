using Domain.Common;

namespace Domain.Users;

public sealed class Credential
{
    /// <summary>Google's documented maximum length for the <c>sub</c> claim.</summary>
    public const int MaxSubjectLength = 255;

    public const int MaxProviderLength = 50;

    public const string GoogleProvider = "google";

    private Credential()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public CredentialType Type { get; private set; }

    /// <summary>
    /// The identity provider that issued <see cref="Subject"/>, or <see langword="null"/> for a
    /// credential that has no issuer — a passkey is held by the authenticator, not granted by anyone.
    /// </summary>
    public string? Provider { get; private set; }

    /// <summary>
    /// The provider's stable identifier for the user, or <see langword="null"/> where there is no
    /// provider. See <see cref="Provider"/>.
    /// </summary>
    public string? Subject { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public static Credential CreateFederated(Guid userId, string provider, string subject, DateTime createdAtUtc)
    {
        Dictionary<string, string[]> errors = new();

        // The trimmed value is what reaches the column, so it is what the length bounds apply to.
        string trimmedProvider = provider.Trim();
        string trimmedSubject = subject.Trim();

        if (userId == Guid.Empty)
        {
            errors[nameof(UserId)] = ["User id is required."];
        }

        if (string.IsNullOrEmpty(trimmedProvider))
        {
            errors[nameof(Provider)] = ["Provider is required."];
        }
        // Refused rather than lowercased, and the comparison is ordinal on purpose. CK_credentials_provider
        // will not accept 'Google' either, so folding the case here would quietly make acceptable a value
        // the column is about to refuse; and UserRepository.FindUserIdByFederatedCredentialAsync matches the
        // column case-sensitively, so a folded write would store a row its own lookup could never find.
        // The length bound the column carries needs no branch of its own — no spelling other than the one
        // below gets this far.
        else if (!string.Equals(trimmedProvider, GoogleProvider, StringComparison.Ordinal))
        {
            errors[nameof(Provider)] = [$"Provider must be '{GoogleProvider}'."];
        }

        if (string.IsNullOrEmpty(trimmedSubject))
        {
            errors[nameof(Subject)] = ["Subject is required."];
        }
        else if (trimmedSubject.Length > MaxSubjectLength)
        {
            errors[nameof(Subject)] = [$"Subject must be {MaxSubjectLength} characters or fewer."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new Credential
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Type = CredentialType.Federated,
            Provider = trimmedProvider,
            Subject = trimmedSubject,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Mints the credential a passkey registration hangs its public key and signature counter off.
    /// </summary>
    public static Credential CreatePasskey(Guid userId, DateTime createdAtUtc)
    {
        Dictionary<string, string[]> errors = new();

        if (userId == Guid.Empty)
        {
            errors[nameof(UserId)] = ["User id is required."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        // Provider and Subject stay null: an authenticator holds this credential, nobody issued it.
        // See the remarks on Provider. Leaving them null is also what keeps the federated discovery
        // lookup on the (provider, subject) pair from ever matching a passkey row.
        return new Credential
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Type = CredentialType.Passkey,
            Provider = null,
            Subject = null,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Mints the credential that stands for one issued <b>set</b> of recovery codes — one call per
    /// set, never one per code.
    /// </summary>
    /// <remarks>
    /// The codes themselves are rows on <c>recovery_code_hashes</c> hanging off this credential, which
    /// is what lets redeeming one delete a row while the set — and the account's ability to redeem the
    /// rest — survives, and lets revoking the set be a single delete the cascade carries the codes away
    /// with. An account holds at most one set, a rule <c>IX_credentials_user_id_recovery_codes</c>
    /// owns: two sets would be two remaining-counts with nothing saying which one binds.
    /// </remarks>
    public static Credential CreateRecoveryCodes(Guid userId, DateTime createdAtUtc)
    {
        Dictionary<string, string[]> errors = new();

        // The rule the two factories above already apply, applied here rather than assumed. It matters
        // more here than anywhere else on the schema: a redemption arrives anonymous and adopts the
        // user_id it finds, and recovery_code_hashes is exempt from row-level security, so nothing
        // beneath the application would notice an ownerless set.
        if (userId == Guid.Empty)
        {
            errors[nameof(UserId)] = ["User id is required."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        // Provider and Subject stay null for the reason they do on a passkey: a set of codes is
        // generated by the product for the holder, so there is no issuer to name and no
        // issuer-assigned identifier to record. Leaving them null is also what keeps the federated
        // discovery lookup on the (provider, subject) pair from ever matching one of these rows.
        //
        // Type is the only thing separating this row from a passkey for everything downstream —
        // CK_credentials_type_shape's recovery_codes and passkey arms read the same predicate — and
        // what rides on the spelling starts with which kind of session it opens.
        return new Credential
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Type = CredentialType.RecoveryCodes,
            Provider = null,
            Subject = null,
            CreatedAtUtc = createdAtUtc,
        };
    }
}
