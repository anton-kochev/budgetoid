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
        // the column is about to refuse; and UserRepository.FindByFederatedCredentialAsync matches the
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
}
