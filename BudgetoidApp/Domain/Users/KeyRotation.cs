using Domain.Common;

namespace Domain.Users;

/// <summary>
/// The staging row a content-key rotation runs under: the next generation of the account's two
/// wrapped keys, held beside the generation still in force until a single completion step promotes it
/// into <see cref="WrappedAccountKeys"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rotation is chunked across several requests, which is the whole reason this type exists.</b>
/// Re-encrypting every narrative column in an account is not one request's worth of work — the API
/// caps a request body at 64 KB — so both generations have to be readable for as long as a run is in
/// flight. Swapping the keys first strands every row not yet rewritten; swapping them last strands
/// every row already rewritten if the tab closes. Filed here, the new envelopes are readable
/// throughout and move exactly once, at the end.
/// </para>
/// <para>
/// <b><see cref="UserId"/> is the primary key, and that is load-bearing rather than convenient.</b>
/// "At most one rotation in flight per account" is the invariant the chunking depends on: two
/// concurrent runs would each re-wrap a subset of the same rows under a <em>different</em> new content
/// key, and the account would end holding columns sealed under two keys with nothing recording which
/// got which. Keyed on the user, a second <see cref="Begin"/> for an account that already has one
/// collides on the primary key and is refused by the database — the lowest layer that can hold the
/// rule <em>declaratively</em>, which is what
/// <see href="../../../docs/decisions/0002-enforce-rules-at-the-lowest-capable-layer.md">ADR 0002</see>
/// asks for. A "check, then insert" in a handler is two statements with a window between them, and the
/// window is exactly wide enough for the second browser tab. The cost is that an abandoned run has to
/// be deleted rather than marked, because a status column cannot buy back the key.
/// </para>
/// <para>
/// <b>The pair carried here is unstorable against another account's factor</b>, because
/// <c>key_rotations</c> references <c>wrapped_account_keys</c> by a composite foreign key on
/// <c>(factor_id, user_id)</c> — the idiom every sibling child table on this schema uses. That is the
/// persistence half of the same rule the factory keeps by reading the owner off the credential rather
/// than taking it as an argument: neither half needs a column this type does not already have.
/// </para>
/// <para>
/// <b>Every refusal below is <see cref="WrappedAccountKeys"/>'s refusal, restated — and the width and
/// the version are <em>read</em> from it rather than restated.</b> The two types accept the same
/// material: two AEAD envelopes over the same two 32-byte keys, under the same factor identifier. A
/// second copy of <c>61</c> or of the version byte is the copy that drifts, and a rule one type keeps
/// and the other does not is a hole that opens on the day a rotation is run rather than on the day one
/// is written. <c>KeyRotationTests</c> writes both values out as literals for the opposite and correct
/// reason: it is the only independent statement of them on this path, and a test that read the same
/// constants would be comparing a constant with itself.
/// </para>
/// <para>
/// <b>Nothing here takes an unwrapped key, a key-encryption key or a PRF output</b>, the rule
/// <see cref="WrappedAccountKeys"/> states at length. A rotation changes which keys the account is
/// sealed under; it does not make the server able to open either generation, and a member that
/// accepted one of those values would hand the operator the whole account's plaintext without
/// reddening a single test.
/// </para>
/// </remarks>
public sealed class KeyRotation
{
    private KeyRotation()
    {
    }

    /// <summary>
    /// The account the rotation belongs to, and the primary key — see the type's remarks for why those
    /// are the same sentence.
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// The client-minted identifier of this run: what every later chunk quotes to say which rotation it
    /// is continuing, and what the completion step quotes to say which one it is promoting.
    /// </summary>
    public Guid RotationId { get; private set; }

    /// <summary>
    /// The factor the staged envelopes were wrapped under, and their associated data — the same value
    /// <see cref="WrappedAccountKeys.FactorId"/> carries, which is what the promotion step files the new
    /// generation against.
    /// </summary>
    public Guid FactorId { get; private set; }

    /// <summary>The next generation's wrapped content key, not yet in force.</summary>
    public ReadOnlyMemory<byte> WrappedContentKey { get; private set; }

    /// <summary>The next generation's wrapped index key, not yet in force.</summary>
    public ReadOnlyMemory<byte> WrappedIndexKey { get; private set; }

    public DateTime StartedAtUtc { get; private set; }

    /// <summary>
    /// Stages the next generation of the account's two keys as <paramref name="passkey"/>'s factor
    /// wrapped them, opening a rotation for that account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the loaded credential rather than a loose user id</b>, the argument
    /// <see cref="WrappedAccountKeys.For"/> and <see cref="RecoveryCodeHash.From"/> both make: the
    /// owner is read off the credential, so a factory taking ids is one transposed argument away from
    /// opening a rotation on somebody else's account.
    /// </para>
    /// <para>
    /// <b>The parameter is named <c>passkey</c> because only a passkey may begin a run.</b> A federated
    /// credential derives no key-encryption key at all — OAuth has no PRF equivalent — so envelopes
    /// staged under it are two blobs nothing in the world can open, presented as the account's next
    /// generation. A set of recovery codes is worse than useless rather than useless: it is <em>ten</em>
    /// factors under one credential, so "the factor this rotation began under" has ten answers and the
    /// client would have to choose one without proving it holds the matching code. It follows from the
    /// other end too — beginning is gated on a server-verified passkey assertion, and a set of codes
    /// produces no assertion to verify. The consequence is deliberate and a reader will file it as a
    /// bug: somebody who signed in with a recovery code cannot begin a rotation until they register a
    /// passkey.
    /// </para>
    /// <para>
    /// <b>No <see cref="DateTimeKind"/> check</b>, matching <see cref="WrappedAccountKeys.For"/> and
    /// the rest of the Domain, which names <see cref="DateTimeKind"/> nowhere. The
    /// <c>timestamptz</c> column underneath refuses a non-UTC <see cref="DateTime"/>, and that is the
    /// lower layer and already declarative; adding the check here alone would make two types that write
    /// the same column type disagree about it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">No credential was supplied.</exception>
    /// <exception cref="ValidationException">
    /// The credential is not a passkey, an identifier is empty, or an envelope is not
    /// <see cref="WrappedAccountKeys.EnvelopeLength"/> bytes carrying version
    /// <see cref="WrappedAccountKeys.EnvelopeVersion"/>.
    /// </exception>
    public static KeyRotation Begin(
        Credential passkey,
        Guid factorId,
        Guid rotationId,
        ReadOnlyMemory<byte> wrappedContentKey,
        ReadOnlyMemory<byte> wrappedIndexKey,
        DateTime startedAtUtc)
    {
        // The owner is read off the credential, so there is nothing to validate without one. No user
        // typed this; a caller handed over nothing.
        ArgumentNullException.ThrowIfNull(passkey);

        Dictionary<string, string[]> errors = new();

        // Keyed as the three sibling factories key the identical refusal, even though this row carries
        // no credential_type column of its own to point at. KeyRotationTests deliberately asserts only
        // that Errors is non-empty, leaving the spelling to this type; two things decide it. Every key
        // in the product is PascalCase because they are all nameof() over a property, and nothing
        // configures a JsonSerializerOptions.DictionaryKeyPolicy — ValidationExceptionHandler hands the
        // dictionary to ValidationProblemDetails verbatim — so nameof(passkey) would ship the one
        // lowercase field name in any 400 the API returns. And a client reacting to "wrong credential
        // type" should not have to know which of two spellings it got.
        if (passkey.Type != CredentialType.Passkey)
        {
            errors[nameof(CredentialType)] =
                ["A key rotation may only be begun under a passkey credential."];
        }

        // All-zeros is a storable uuid and it is what an unset field sends. Here it is also the
        // associated data of both staged envelopes and what the completion step reads to decide which
        // WrappedAccountKeys row the new generation replaces — a zero there does not fail, it points at
        // no factor.
        if (factorId == Guid.Empty)
        {
            errors[nameof(FactorId)] = ["Factor id is required."];
        }

        // The value a later chunk quotes to say which run it is continuing. All-zeros is what a client
        // that has not begun a run sends, and the one value two accounts reach independently, so
        // accepting it means a chunk cannot be told from a chunk of an abandoned attempt.
        if (rotationId == Guid.Empty)
        {
            errors[nameof(RotationId)] = ["Rotation id is required."];
        }

        // Keyed on the property the value ends up in, as WrappedAccountKeys.For keys its own.
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

        return new KeyRotation
        {
            UserId = passkey.UserId,
            RotationId = rotationId,
            FactorId = factorId,

            // Copied, not aliased, the rule WrappedAccountKeys.For and PasskeyPublicKey.Register both
            // keep. A ReadOnlyMemory<byte> is a view over an array the caller still owns, and the
            // caller on this path is minting a generation of envelopes with every reason to be reusing
            // one buffer.
            WrappedContentKey = wrappedContentKey.ToArray(),
            WrappedIndexKey = wrappedIndexKey.ToArray(),
            StartedAtUtc = startedAtUtc,
        };
    }

    /// <summary>
    /// Says what is wrong with <paramref name="envelope"/>, or <see langword="null"/> if it is
    /// well-formed — one definition, so the two columns cannot end up judged by different rules.
    /// </summary>
    /// <remarks>
    /// <b>Both bounds come from <see cref="WrappedAccountKeys"/>.</b> A rotation stages the same two
    /// wrapped 32-byte keys that type holds, so it has no width and no version of its own to state, and
    /// a second copy of either number is the copy that drifts. Only the sentences are written here,
    /// because the sibling's are private to it.
    /// <para>
    /// <b>Width before version, and not merely for message quality</b> — the reason the sibling's own
    /// comment gives: an empty envelope has no leading byte to read, so reading
    /// <c>Span[0]</c> first throws <see cref="IndexOutOfRangeException"/> out of the Domain and a
    /// client that sent an unset field is told the server broke rather than that its envelope was
    /// malformed.
    /// </para>
    /// </remarks>
    private static string? DescribeMalformedEnvelope(ReadOnlyMemory<byte> envelope) =>
        envelope.Length != WrappedAccountKeys.EnvelopeLength
            ? $"A wrapped key envelope must be exactly {WrappedAccountKeys.EnvelopeLength} bytes."
            : envelope.Span[0] != WrappedAccountKeys.EnvelopeVersion
                ? $"A wrapped key envelope must carry version {WrappedAccountKeys.EnvelopeVersion}."
                : null;
}
