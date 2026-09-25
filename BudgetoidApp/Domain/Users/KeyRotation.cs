using Domain.Common;

namespace Domain.Users;

/// <summary>
/// The staging row a content-key rotation runs under: the next generation's manifest of factor public
/// keys and the epoch it will be filed at, held beside the generation still in force until a single
/// completion step promotes it into <see cref="FactorManifest"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rotation is chunked across several requests, which is the whole reason this type exists.</b>
/// Re-encrypting every narrative column in an account is not one request's worth of work — the API
/// caps a request body at 64 KB — so both generations have to be readable for as long as a run is in
/// flight. Swapping the keys first strands every row not yet rewritten; swapping them last strands
/// every row already rewritten if the tab closes. Filed here, the next generation is readable
/// throughout and moves exactly once, at the end.
/// </para>
/// <para>
/// <b><see cref="UserId"/> is the primary key, and that is load-bearing rather than convenient.</b>
/// "At most one rotation in flight per account" is the invariant the chunking depends on: two
/// concurrent runs would each re-encrypt a subset of the same rows under a <em>different</em> new content
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
/// <b>The staged row names no factor, and that absence is the shape of the change rather than a column
/// somebody forgot.</b> Under a key-encryption key there was exactly one factor a run could have been
/// begun under, because re-wrapping needed the secret that factor derives; under ECDH there is no such
/// thing. Encapsulating the next generation's account keys takes public halves only, so a run produces
/// <em>one value per surviving factor</em> — those are <see cref="KeyRotationSeal"/> rows hanging off
/// this one — and "the factor this rotation was performed under" has stopped being a question with an
/// answer. It did not become plural either: a list of factor ids here would be the seals' own key set
/// restated on the parent, which is the copy that drifts.
/// </para>
/// <para>
/// <b>What the vanished <c>factor_id</c> was doing for security is held, and was always really held,
/// one layer up.</b> "Only somebody holding a passkey may begin a run" is the re-authentication gate on
/// the route, which runs a server-verified assertion to completion before anything below it is read —
/// <see href="../../../docs/business-logic/key-rotation.md">key-rotation.md</see> argues its ordering.
/// The <see cref="CredentialType.Passkey"/> refusal in <see cref="Begin"/> survives and the factory goes
/// on taking the loaded <see cref="Credential"/>, but read it for what it now is: <b>a restatement</b>.
/// It no longer decides anything about the staged material, because no staged value is bound to that
/// credential; what it still buys is that a caller assembling this type out of a recovery-code or
/// federated credential is refused at the object rather than at the route it forgot to gate.
/// </para>
/// <para>
/// <b>Every bound below is <see cref="FactorManifest"/>'s bound, <em>read</em> from it rather than
/// restated.</b> The staged manifest is the manifest a promotion writes into that row, so a width or an
/// epoch this table accepted and that one refused is a row that stores here and fails at promotion — at
/// the one moment in the run where the old generation has already gone. It is the rule this type
/// already kept for the envelope bounds it used to read off <see cref="WrappedAccountKeys"/>, for the
/// same reason. <c>KeyRotationTests</c> writes the values out as literals for the opposite and correct
/// reason: it is the only independent statement of them on this path, and a test that read the same
/// constants would be comparing a constant with itself.
/// </para>
/// <para>
/// <b>The manifest's <em>format</em> is not this type's business.</b> Presence and the cap are checked
/// and nothing else: the bytes are authenticated by a key this server does not hold, so any structural
/// reading of them would be a second, unverifiable grammar sitting where a client's is authoritative.
/// A later slice owns whatever parses them, and it will not be this one.
/// </para>
/// <para>
/// <b>Nothing here takes an unwrapped key, a private key, a key-encryption key or a PRF output</b>, the
/// rule <see cref="WrappedAccountKeys"/> states at length. A rotation changes which keys the account is
/// sealed under; it does not make the server able to open either generation, and a member that accepted
/// one of those values would hand the operator the whole account's plaintext without reddening a single
/// test.
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
    /// The next generation's manifest of factor public keys, not yet in force — the bytes a promotion
    /// writes into <see cref="FactorManifest.Manifest"/>.
    /// </summary>
    /// <remarks>
    /// Authenticated as a <em>set</em> by a key the client holds, which is why nothing on this side reads
    /// into it. It is staged rather than written straight through for the reason the whole type exists:
    /// until the run completes, the manifest in force is still the old one and a client asking which
    /// factors the account has must get one answer, not two.
    /// </remarks>
    public ReadOnlyMemory<byte> StagedManifest { get; private set; }

    /// <summary>
    /// The generation <see cref="StagedManifest"/> will be filed at when the run completes.
    /// </summary>
    /// <remarks>
    /// <b>The epoch is bound in the manifest and nowhere else</b> — not in any encapsulated value's KDF
    /// <c>info</c> and not in any associated data. Binding it into a value would make every encapsulation
    /// of a generation unopenable the moment the epoch it was produced under stopped being current, which
    /// is a property nobody wants and which would turn a resumable run into a disposable one. Carried
    /// here, it is a number the promotion writes and the client compares.
    /// </remarks>
    public int StagedRotationEpoch { get; private set; }

    public DateTime StartedAtUtc { get; private set; }

    /// <summary>
    /// Stages the next generation of the account's manifest, opening a rotation for that account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It takes the loaded credential rather than a loose user id</b>, the argument
    /// <see cref="WrappedAccountKeys.For"/> and <see cref="RecoveryCodeHash.From"/> both make: the
    /// owner is read off the credential, so a factory taking ids is one transposed argument away from
    /// opening a rotation on somebody else's account. That is the whole of what the credential buys here
    /// now — the type refusal beside it is the restatement the type's remarks describe.
    /// </para>
    /// <para>
    /// <b>The parameter is still named <c>passkey</c>, because only a passkey may begin a run.</b> A
    /// federated credential derives no key-encryption key at all — OAuth has no PRF equivalent — so it
    /// holds no factor key pair and can decapsulate nothing. A set of recovery codes holds ten key pairs
    /// under one credential, so a begin made under it names a credential and not a factor. It follows from
    /// the other end too — beginning is gated on a server-verified passkey assertion, and a set of codes
    /// produces no assertion to verify. The consequence is deliberate and a reader will file it as a bug:
    /// somebody who signed in with a recovery code cannot begin a rotation until they register a passkey.
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
    /// The credential is not a passkey, the rotation identifier is empty, the staged manifest is empty or
    /// wider than <see cref="FactorManifest.MaximumBytes"/>, or the staged epoch is below
    /// <see cref="FactorManifest.MinimumRotationEpoch"/>.
    /// </exception>
    public static KeyRotation Begin(
        Credential passkey,
        Guid rotationId,
        ReadOnlyMemory<byte> stagedManifest,
        int stagedRotationEpoch,
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

        // The value a later chunk quotes to say which run it is continuing. All-zeros is what a client
        // that has not begun a run sends, and the one value two accounts reach independently, so
        // accepting it means a chunk cannot be told from a chunk of an abandoned attempt.
        if (rotationId == Guid.Empty)
        {
            errors[nameof(RotationId)] = ["Rotation id is required."];
        }

        // Emptiness before width, and the two are one key because they are one column — the shape
        // FactorManifest.For keeps over the same bytes. An empty bytea is exactly what an unset member
        // sends, so a caller that forgot to attach the manifest would otherwise stage a generation naming
        // no factor at all, and the promotion would file it: an account with no way back in, stored as
        // though it had one. Written as an else-if because a value cannot be both.
        if (stagedManifest.IsEmpty)
        {
            errors[nameof(StagedManifest)] = ["A staged factor manifest is required."];
        }
        else if (stagedManifest.Length > FactorManifest.MaximumBytes)
        {
            errors[nameof(StagedManifest)] =
                [$"A staged factor manifest must be at most {FactorManifest.MaximumBytes} bytes."];
        }

        // Below the floor, not merely at zero: a negative epoch names no generation either, and one
        // comparison refuses both. Epoch 0 is the absence of a manifest row, so staging it would stage a
        // generation asserting its own absence — FactorManifest.MinimumRotationEpoch carries that
        // argument, and this is the same rule read off the same constant rather than a second copy of it.
        if (stagedRotationEpoch < FactorManifest.MinimumRotationEpoch)
        {
            errors[nameof(StagedRotationEpoch)] =
            [
                $"A staged rotation epoch must be at least {FactorManifest.MinimumRotationEpoch}.",
            ];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return new KeyRotation
        {
            UserId = passkey.UserId,
            RotationId = rotationId,

            // Copied, not aliased, the rule WrappedAccountKeys.For and PasskeyPublicKey.Register both
            // keep. A ReadOnlyMemory<byte> is a view over an array the caller still owns, and the
            // caller on this path is assembling a generation with every reason to be reusing one buffer.
            StagedManifest = stagedManifest.ToArray(),
            StagedRotationEpoch = stagedRotationEpoch,
            StartedAtUtc = startedAtUtc,
        };
    }
}
