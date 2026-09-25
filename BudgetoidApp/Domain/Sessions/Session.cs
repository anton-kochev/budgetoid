using Domain.Common;
using Domain.Users;

namespace Domain.Sessions;

/// <summary>
/// One established sign-in, holding the credential that opened it and how much of the account that
/// credential can reach.
/// </summary>
/// <remarks>
/// Its own aggregate rather than part of <see cref="User"/>, and it references its user and its
/// credential by id for the same reason <see cref="Credential"/> does. Modelling it inside the user
/// would make atomicity automatic, but the root would then grow to accumulate sessions and passkeys,
/// and a root loaded on every authenticated request is the wrong place for either.
/// </remarks>
public sealed class Session
{
    private Session()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid CredentialId { get; private set; }

    /// <summary>
    /// The type of the credential that opened this session — the same fact <see cref="Kind" /> is
    /// derived from, carried on the row so the database can check the derivation instead of trusting
    /// that every INSERT went through <see cref="Establish" />.
    /// </summary>
    public CredentialType CredentialType { get; private set; }

    public SessionKind Kind { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    /// <summary>Whether this session may reach the account's budget content.</summary>
    /// <remarks>
    /// Delegated to <see cref="SessionKindReach" /> rather than compared here, because the API asks the
    /// same question of a claim and never of an entity. See that type for what a second copy of the
    /// comparison would cost.
    /// </remarks>
    public bool ReadsBudgetContent => Kind.ReadsBudgetContent();

    public static Session Establish(Credential credential, DateTime createdAtUtc, DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(credential);

        Dictionary<string, string[]> errors = new();

        // Accumulated into a dictionary though there is one rule today: the shape is what lets the
        // next rule join without restructuring the method or the exception it throws.
        if (expiresAtUtc <= createdAtUtc)
        {
            errors[nameof(ExpiresAtUtc)] = ["Expiry must be after the moment the session was created."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new Session
        {
            Id = Guid.CreateVersion7(),
            UserId = credential.UserId,
            CredentialId = credential.Id,
            CredentialType = credential.Type,
            Kind = KindFor(credential.Type),
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
        };
    }

    /// <summary>
    /// Ends this session at <paramref name="revokedAtUtc"/>, or leaves an already revoked session
    /// exactly as it is.
    /// </summary>
    public void Revoke(DateTime revokedAtUtc)
    {
        // Idempotent so that "revoke every session this credential established" can run twice —
        // after a retry, or after a second report of the same compromise — without rewriting the
        // instant at which access actually ended.
        if (RevokedAtUtc is not null)
        {
            return;
        }

        RevokedAtUtc = revokedAtUtc;
    }

    /// <summary>Whether this session is live at <paramref name="instantUtc"/>.</summary>
    public bool IsActiveAt(DateTime instantUtc)
    {
        // Expiry ends a session on its own, with nobody revoking anything, so a reading that
        // consulted only RevokedAtUtc would report a long-expired session as live. The boundary is
        // exclusive: the session is live up to its expiry and not at it.
        return RevokedAtUtc is null && instantUtc < ExpiresAtUtc;
    }

    // An authorization exchange with an identity provider returns claims, not a secret the client
    // can turn into a key, so any account reachable by a provider sign-in would be an account the
    // provider's holder could read. Federated is the only credential type that cannot hold the
    // account's keys, which is why it is the only one whose session does not reach budget content:
    // a passkey's authenticator holds them, and a set of recovery codes is the secret the content
    // and index keys are wrapped under, so the code the holder typed unwraps them. That is FR-109,
    // and the two-tier argument behind it lives in §8.C of the SRS. Every arm is written out rather
    // than folded into a default so that adding a CredentialType member is a decision someone has to
    // make here — the rule runs by enumeration, not by "anything that is not federated". The
    // derivation is restated in the schema by CK_sessions_kind_matches_credential: a rule deciding
    // what a session may read is not one to leave to a single factory while the column list stays
    // reachable by any INSERT.
    private static SessionKind KindFor(CredentialType credentialType) => credentialType switch
    {
        CredentialType.Federated => SessionKind.Locked,
        CredentialType.Passkey => SessionKind.Full,
        CredentialType.RecoveryCodes => SessionKind.Full,
        _ => throw new ArgumentOutOfRangeException(
            nameof(credentialType),
            credentialType,
            $"A {nameof(CredentialType)} member was added and nobody chose which kind of session it opens."),
    };
}
