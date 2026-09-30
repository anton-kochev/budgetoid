namespace Domain.Users;

/// <summary>
/// What a fresh provider assertion means for an account's federated credential and its address — a
/// decision, never an act: nothing here touches the <see cref="User"/> it was asked about.
/// </summary>
/// <remarks>
/// A new subject is one credential retired and one filed, never the subject rewritten in place, because
/// <c>credentials</c> holds no <c>UPDATE</c> grant of any shape and immutable columns are enforced by
/// their omission from the grant (ADR 0004) — an update here would be refused with <c>42501</c>.
/// </remarks>
public sealed class FederatedIdentityChange
{
    private FederatedIdentityChange(bool isNoChange, Email email, Credential? retired, Credential? filed)
    {
        IsNoChange = isNoChange;
        Email = email;
        Retired = retired;
        Filed = filed;
    }

    /// <summary>Whether the assertion matches the account as stored, so there is nothing to write.</summary>
    public bool IsNoChange { get; }

    /// <summary>The address the account should hold afterwards, whether or not it moved.</summary>
    public Email Email { get; }

    /// <summary>The credential to delete — the caller's own instance — or <see langword="null"/>.</summary>
    public Credential? Retired { get; }

    /// <summary>The replacement credential to insert, or <see langword="null"/>.</summary>
    public Credential? Filed { get; }

    /// <summary>
    /// Compares the asserted subject and address against the account's current federated credential.
    /// </summary>
    /// <param name="user">The account the assertion is about. Never mutated.</param>
    /// <param name="current">The account's federated credential, as loaded.</param>
    /// <param name="subject">The provider's <c>sub</c> claim, compared ordinally after trimming.</param>
    /// <param name="email">The provider's asserted address, compared ordinally once normalised.</param>
    /// <param name="nowUtc">The instant a replacement credential is filed at.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="current"/> is not federated, or belongs to another account.
    /// </exception>
    /// <exception cref="Common.ValidationException">
    /// The address or the replacement subject is not one this product accepts.
    /// </exception>
    public static FederatedIdentityChange Decide(
        User user, Credential current, string subject, string email, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(subject);

        if (current is not
            { Type: CredentialType.Federated, Provider: { } provider, Subject: { } currentSubject })
        {
            throw new ArgumentException("The current credential must be federated.", nameof(current));
        }

        if (current.UserId != user.Id)
        {
            throw new ArgumentException("The current credential belongs to another account.", nameof(current));
        }

        // Validated through the path the creation uses, so a refused address throws before anything is
        // decided. Ordinal on purpose: the unique index's case-insensitive collation says who else may
        // hold an address, not whether this one changed.
        Email decidedEmail = Email.Create(email);
        bool addressChanged = !string.Equals(decidedEmail.Value, user.Email.Value, StringComparison.Ordinal);

        // CreateFederated stored the trimmed subject, so the assertion is trimmed before it is compared.
        // Ordinal because the sub claim is case-sensitive: a case-only difference is another identity.
        bool subjectChanged = !string.Equals(subject.Trim(), currentSubject, StringComparison.Ordinal);

        if (!subjectChanged)
        {
            return new FederatedIdentityChange(!addressChanged, decidedEmail, retired: null, filed: null);
        }

        // The provider is read off the credential being retired rather than named here, so a replacement
        // can never file under an issuer the account did not already hold.
        Credential filed = Credential.CreateFederated(user.Id, provider, subject, nowUtc);

        return new FederatedIdentityChange(isNoChange: false, decidedEmail, current, filed);
    }
}
