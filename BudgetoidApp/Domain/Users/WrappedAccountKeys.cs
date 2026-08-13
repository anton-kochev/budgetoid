using Domain.Common;

namespace Domain.Users;

/// <summary>
/// The account's content key and index key as one recovery factor holds them: two fixed-width AEAD
/// envelopes, each wrapped under a key-encryption key derived from that factor.
/// </summary>
/// <remarks>
/// <para>
/// Its own entity referencing its credential and its user by id, for the reason
/// <see cref="PasskeyPublicKey"/> gives: hanging it off <see cref="User"/> would grow a root that is
/// loaded on every authenticated request. One row per factor, so registering a second passkey or
/// issuing a set of recovery codes adds a way back into the same two keys rather than re-keying the
/// account.
/// </para>
/// <para>
/// <b>The server can open neither envelope and holds no value that could.</b> Both keys are generated
/// in the browser and wrapped under a key-encryption key derived from a factor this server never sees
/// — a PRF output evaluated inside an authenticator, or a recovery code stored only as
/// <see cref="RecoveryCodeHash"/>, which it cannot invert. So nothing on this type takes an unwrapped
/// key, a key-encryption key, a PRF output or a recovery code, and nothing may be added that does: a
/// member accepting any of those would put the whole account's plaintext within reach of the operator,
/// and it would do so without failing a single test, because there is no test that can notice a value
/// the design says never arrives.
/// </para>
/// <para>
/// <b>The two envelope columns are distinguishable only by which one a value landed in.</b> Both are
/// 61 bytes, both carry the same version byte, both are <c>NOT NULL</c>: a swapped pair satisfies every
/// width check, every version check and every database constraint here. What separates them is the
/// associated data each envelope was sealed with, which binds the key's purpose — so a swap fails to
/// open in the browser, months later, with no server-side symptom at any point in between. That is the
/// reason the binding exists, and the reason the factory assigns each argument exactly once.
/// </para>
/// </remarks>
public sealed class WrappedAccountKeys
{
    /// <summary>
    /// The only legal width of an envelope over a 32-byte key:
    /// <c>1 (version) + 12 (nonce) + 32 (ciphertext) + 16 (tag)</c>.
    /// </summary>
    /// <remarks>
    /// <b>A width, not a cap.</b> AES-GCM ciphertext is exactly the length of its plaintext, and the
    /// plaintext is a 32-byte key, so an envelope over a wrapped account key has one legal size and
    /// both sides of the bound are refused. Refused rather than padded or truncated: either repair
    /// would store a well-formed row holding an envelope whose tag cannot verify, and the account would
    /// look registered until the day somebody needed the keys.
    /// </remarks>
    public const int EnvelopeLength = 61;

    /// <summary>
    /// The one envelope version defined today (IFR-007): AES-256-GCM, 96-bit nonce, 128-bit tag.
    /// </summary>
    /// <remarks>
    /// The version is refused <em>here</em> rather than left to the client because the successor does
    /// not exist: a row carrying version 2 is a client claiming a contract this deployment has never
    /// implemented, and storing it would file bytes no version of this system can interpret. The
    /// database restates this bound and the width one; this factory is where the mistake is cheap.
    /// </remarks>
    public const byte EnvelopeVersion = 1;

    private WrappedAccountKeys()
    {
    }

    /// <summary>The credential standing for the recovery factor these envelopes are wrapped under.</summary>
    public Guid CredentialId { get; private set; }

    /// <summary>
    /// The client-minted identifier of the factor, and the associated data of both envelopes.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <see cref="Credential.Id"/>.</b> The first leg of
    /// <see href="../../../docs/decisions/0014-scope-the-credential-delete-in-the-application.md">ADR
    /// 0014</see> is that no source of a <see cref="Credential"/> accepts a caller-chosen id — every
    /// factory mints its own — so a fabricated instance can never name an existing row. That holds
    /// because the credential deletes are issued by primary key against a table carrying no row-level
    /// security policy, which leaves the id's unguessability doing real work. Binding an envelope to
    /// <c>credentials.id</c> would put that id in the client's hands and, worse, would require the
    /// client to choose it before the credential existed.
    /// </remarks>
    public Guid FactorId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>
    /// The type of the credential these envelopes were filed against, carried on the row for the reason
    /// <see cref="PasskeyPublicKey.CredentialType"/> gives.
    /// </summary>
    public CredentialType CredentialType { get; private set; }

    /// <summary>The wrapped content key — the key the account's transaction data is encrypted under.</summary>
    public ReadOnlyMemory<byte> WrappedContentKey { get; private set; }

    /// <summary>The wrapped index key — the key the account's searchable index is derived under.</summary>
    public ReadOnlyMemory<byte> WrappedIndexKey { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Files the account's two keys as <paramref name="credential"/>'s factor holds them.
    /// </summary>
    /// <remarks>
    /// <b>It takes the loaded credential rather than three loose ids</b>, the argument
    /// <see cref="PasskeyPublicKey.Register"/> and <see cref="RecoveryCodeHash.From"/> both make. All
    /// three copied columns are compared against <c>credentials(id, user_id, type)</c> by a composite
    /// foreign key, so a row whose owner disagreed with its credential's is unstorable — but a factory
    /// taking three ids is one transposed argument away from filing an account's wrapped keys against
    /// another account's factor, and the id it would need is one the caller already has in hand.
    /// </remarks>
    /// <exception cref="ArgumentNullException">No credential was supplied.</exception>
    /// <exception cref="ValidationException">
    /// The credential does not stand for a recovery factor, the factor identifier is empty, or an
    /// envelope is not <see cref="EnvelopeLength"/> bytes carrying version <see cref="EnvelopeVersion"/>.
    /// </exception>
    public static WrappedAccountKeys For(
        Credential credential,
        Guid factorId,
        ReadOnlyMemory<byte> wrappedContentKey,
        ReadOnlyMemory<byte> wrappedIndexKey,
        DateTime createdAtUtc)
    {
        // The owner and the type are both read off the credential, so there is nothing to validate
        // without one. No user typed this; a caller handed over nothing.
        ArgumentNullException.ThrowIfNull(credential);

        Dictionary<string, string[]> errors = new();

        // Enumerated as the two factors that have a key-encryption key, so that adding a third
        // credential type does not silently gain the ability to hold the account's keys. Identity and
        // key custody are two tiers and the provider is only ever on the first: OAuth has no PRF
        // equivalent, so a row filed against the federated credential would be two envelopes nothing in
        // the world can open, presented as a way back into the account.
        if (credential.Type is not (CredentialType.Passkey or CredentialType.RecoveryCodes))
        {
            errors[nameof(CredentialType)] =
                ["Account keys may only be wrapped under a passkey or a set of recovery codes."];
        }

        // All-zeros is a storable uuid and it is what an unset field sends. It is also the one value two
        // accounts reach independently, so accepting it turns a unique index into a cross-account
        // collision the second account experiences as a refusal to register — and since it is the
        // associated data of both envelopes, nothing downstream can tell a deliberate zero from a
        // mistake.
        if (factorId == Guid.Empty)
        {
            errors[nameof(FactorId)] = ["Factor id is required."];
        }

        // Keyed on the property the value ends up in, as PasskeyPublicKey.Register keys its own length
        // refusals.
        if (DescribeMalformedEnvelope(wrappedContentKey) is { } contentProblem)
        {
            errors[nameof(WrappedContentKey)] = [contentProblem];
        }

        if (DescribeMalformedEnvelope(wrappedIndexKey) is { } indexProblem)
        {
            errors[nameof(WrappedIndexKey)] = [indexProblem];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new WrappedAccountKeys
        {
            CredentialId = credential.Id,
            UserId = credential.UserId,
            CredentialType = credential.Type,
            FactorId = factorId,

            // Copied, not aliased, the rule PasskeyPublicKey.Register keeps for its COSE key. A
            // ReadOnlyMemory<byte> is a view over an array the caller still owns, and a buffer reused
            // for the next envelope would rewrite a wrapped key that has already been accepted.
            WrappedContentKey = wrappedContentKey.ToArray(),
            WrappedIndexKey = wrappedIndexKey.ToArray(),
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Says what is wrong with <paramref name="envelope"/>, or <see langword="null"/> if it is
    /// well-formed — one definition, so the two columns cannot end up judged by different rules.
    /// </summary>
    private static string? DescribeMalformedEnvelope(ReadOnlyMemory<byte> envelope) =>
        // Width before version, and not merely for message quality: an empty envelope has no leading
        // byte to read.
        envelope.Length != EnvelopeLength
            ? $"A wrapped key envelope must be exactly {EnvelopeLength} bytes."
            : envelope.Span[0] != EnvelopeVersion
                ? $"A wrapped key envelope must carry version {EnvelopeVersion}."
                : null;
}
