using Domain.Common;
using Domain.Security;

namespace Domain.Users;

/// <summary>
/// One recovery factor's share of the account: the ECDH private key that factor owns, wrapped under the
/// key-encryption key it derives, and the account's content and index keys, encapsulated to that
/// factor's public key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three verbs, and they are not interchangeable</b> — <em>sealed under</em> a key over data,
/// <em>wrapped under</em> a key over another key, <em>encapsulated to</em> a public key. This row carries
/// one value of the second kind and one of the third, and the two property names are the only place that
/// distinction is written into the code.
/// </para>
/// <para>
/// <b>The key pair is what makes a rotation possible at all, and reading it as an extra layer is the
/// mistake to avoid.</b> Wrapping the account's two keys directly under the factor's key-encryption key —
/// which is what this row used to hold — means re-wrapping them needs that key, and for a passkey that
/// key comes from a WebAuthn PRF output which exists only while that authenticator is being touched. So a
/// rotation needed <em>every</em> registered authenticator present at once, and an account with a hardware
/// key in a drawer could not rotate at all. Encapsulating to a factor's <em>public</em> half needs only
/// the public half, and <see cref="FactorManifest"/> carries every factor's — so a rotation needs the old
/// content key and a set of public keys, and no authenticator but the one already in the person's hand.
/// </para>
/// <para>
/// Its own entity referencing its credential and its user by id, for the reason
/// <see cref="PasskeyPublicKey"/> gives: hanging it off <see cref="User"/> would grow a root that is
/// loaded on every authenticated request. Registering a second passkey or issuing a set of recovery
/// codes adds a way back into the same two account keys rather than re-keying the account.
/// </para>
/// <para>
/// <b>One row per factor, and a factor is not a credential.</b> A passkey is one of each, so it holds one
/// row. A set of recovery codes is <em>ten separate secrets</em> under a single <see cref="Credential"/>
/// — a set is issued, counted and revoked as a unit — and the client derives a key-encryption key from
/// each <em>code</em>. Ten codes are therefore ten key-encryption keys, ten key pairs and ten of these,
/// each with its own <see cref="FactorId"/>, its own wrapped private key and its own encapsulated copy of
/// the account's keys; no one of them can stand for the others. <see cref="For"/> is called once per
/// factor, which for a set means ten times, and <c>factor_id</c> — not <c>credential_id</c> — is what
/// identifies the row.
/// </para>
/// <para>
/// <b>The server can open neither value and holds nothing that could.</b> The account's two keys are
/// generated in the browser and encapsulated to a public key whose private half is itself wrapped under a
/// key-encryption key derived from a factor this server never sees — a PRF output evaluated inside an
/// authenticator, or a recovery code stored only as <see cref="RecoveryCodeHash"/>, which it cannot
/// invert. So nothing on this type takes an unwrapped key, a <em>private</em> key in the clear, a
/// key-encryption key, a PRF output or a recovery code, and nothing may be added that does: a member
/// accepting any of those would put the whole account's plaintext within reach of the operator, and it
/// would do so without failing a single test, because there is no test that can notice a value the design
/// says never arrives.
/// </para>
/// <para>
/// <b>The two columns can no longer be swapped into one another, and the hazard that replaces that one
/// sits a level in, inside the encapsulated plaintext.</b> A swap is refused by this factory now — 167
/// bytes against 158, two framings and two version constants, each column judged by its own describer —
/// where both envelopes were once 61 bytes carrying the same version byte and a swapped pair satisfied
/// every check on either side of the wire. What nothing on this side can see is the <em>order of the two
/// halves inside <see cref="EncapsulatedAccountKeys"/></em>: it is one 64-byte plaintext holding two
/// 32-byte keys, <b>content key first</b>, and the server never sees that plaintext. A client that
/// encapsulated them the other way round produces a value of exactly the right width, carrying exactly
/// the right version byte, which stores, reads back and opens — and yields an index key used to encrypt
/// narrative text and a content key used to compute blind indexes, so every field is unreadable and every
/// index is keyed under the wrong secret. No factory check, no <c>CHECK</c> constraint and no test that
/// could ever be written here will notice. The order is a contract between clients and is held by
/// nothing on this side, ever.
/// </para>
/// <para>
/// <b>A constant may be derived from another constant of the <em>same</em> cryptographic suite. It may
/// never be derived from a constant of a <em>different</em> suite.</b> That one rule decides all four
/// constants below, and it needs writing down because the two halves of it read as a contradiction
/// otherwise: <see cref="WrappedPrivateKeyLength"/> argues <em>for</em> deriving while
/// <see cref="EncapsulatedValueEnvelope.Version"/> argues <em>against</em> aliasing, and the next reader
/// meeting four constants where there were two will reach for exactly the de-duplication the rule
/// forbids. <see cref="WrappedPrivateKeyVersion"/> = <see cref="CiphertextEnvelope.Version"/> is legal,
/// because a wrapped private key <em>is</em> an AEAD envelope of that suite and this entity has no
/// version of its own to state; <see cref="EncapsulatedAccountKeysVersion"/> =
/// <see cref="EncapsulatedValueEnvelope.Version"/> is legal for the mirror reason. Cross-wiring either
/// pair is forbidden, and <b>nothing in the build would notice</b>, because both suites are at their
/// first version and both bytes are <c>1</c> today. <c>EnvelopeSuiteCensusTests</c> reads source text for
/// exactly this mistake, and its discovery filter is public static classes in <c>Domain.Security</c>
/// declaring a <c>public const byte Version</c> — which is not this file, so the census does not reach
/// these two declarations and this paragraph is what stands in its place.
/// </para>
/// </remarks>
public sealed class WrappedAccountKeys
{
    /// <summary>
    /// The width of one of the account's two keys — one AES-256 key.
    /// </summary>
    /// <remarks>
    /// Private because it is a fact about this entity's payload and not about either format. Neither
    /// shared framing carries a width at all: narrative text seals to whatever length it happens to be,
    /// so a 32 published beside <see cref="CiphertextEnvelope.MinimumLength"/> or
    /// <see cref="EncapsulatedValueEnvelope.MinimumLength"/> would read like part of the format to the
    /// next caller of either.
    /// </remarks>
    private const int AccountKeyBytes = 32;

    /// <summary>
    /// The width of the plaintext inside <see cref="EncapsulatedAccountKeys"/> — both account keys, as
    /// one value.
    /// </summary>
    /// <remarks>
    /// <b>One encapsulation over both keys rather than one each, and that is a cryptographic decision
    /// rather than a saving.</b> Two values encapsulated to the same public key under the same KDF
    /// <c>info</c> are two AES-GCM streams under one derived key, and an implementation that also reused
    /// the ephemeral key pair across the pair — the obvious way to write "encapsulate these two to this
    /// factor" — reuses the keystream outright: the exclusive-or of the two ciphertexts is the
    /// exclusive-or of the two account keys, which destroys the independence of the content key and the
    /// index key that the whole design rests on. One plaintext, one encapsulation, one nonce, and the
    /// question does not arise. The order of the halves is the price, and it is argued in the type's
    /// remarks: <b>content key first</b>, held by nothing here.
    /// </remarks>
    private const int AccountKeysPlaintextBytes = 2 * AccountKeyBytes;

    /// <summary>
    /// The width of the plaintext inside <see cref="WrappedPrivateKey"/> — one PKCS#8-encoded ECDH
    /// P-256 private key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written out, because there is nothing in this repository to derive it from.</b> Every other
    /// number on this type is either a format's or a count of AES-256 keys; this one is a fact about what
    /// three browser implementations of WebCrypto actually export. Measured on Chrome 152, Firefox 156 and
    /// WebKit 26.6, 120 samples per engine: one distinct length on all three, and it is <c>138</c>.
    /// <c>raw</c> import of an EC <em>private</em> key is refused by all three, so the bare 32-byte scalar
    /// is unreachable from a browser and PKCS#8 is the only exportable binary form there is to wrap.
    /// </para>
    /// <para>
    /// <b>Do not "correct" this to 32 + 65 + DER overhead.</b> That sum is right for one encoder and is
    /// the arithmetic a reader will reach for on finding a number with no expression behind it — a PKCS#8
    /// <c>ECPrivateKey</c> may or may not carry the optional public key, the curve may be named or
    /// explicit, and each choice moves the total. The width here is not a derivation anybody got right; it
    /// is what the three engines this product runs in produce, and a number recomputed from a structure
    /// diagram would refuse every key every one of them exports.
    /// </para>
    /// </remarks>
    private const int PrivateKeyBytes = 138;

    /// <summary>
    /// The only legal width of <see cref="WrappedPrivateKey"/>: the AEAD framing stretched over one
    /// PKCS#8 P-256 private key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A width, not a cap.</b> AES-GCM ciphertext is exactly the length of its plaintext, and the
    /// plaintext is a fixed-width private key, so this value has one legal size and both sides of the
    /// bound are refused. Refused rather than padded or truncated: either repair would store a well-formed
    /// row holding an envelope whose tag cannot verify, and the factor would look usable until the day
    /// somebody needed the account's keys through it.
    /// </para>
    /// <para>
    /// <b>Computed from <see cref="CiphertextEnvelope.MinimumLength"/> rather than written out, and it
    /// stays a <see langword="const"/>.</b> The version, nonce and tag widths are the shared AEAD format's
    /// to state; what this entity adds is the one plaintext it wraps, so the arithmetic is the sentence a
    /// reader needs and <c>167</c> is not. Deriving from <see cref="CiphertextEnvelope"/> is the legal
    /// direction under the type's rule — a wrapped private key is an envelope of that suite. Deriving it
    /// from <see cref="EncapsulatedValueEnvelope.MinimumLength"/> instead would be the forbidden spelling,
    /// and would silently mean 65 bytes more.
    /// </para>
    /// <para>
    /// <b>The arithmetic is a way of saying it, not the thing that holds it.</b> The same argument the
    /// deleted <c>EnvelopeLength</c> made about itself and measured: writing the sum out as a literal
    /// leaves the suite green, because nothing in the build derives one of these numbers from the other.
    /// What catches a drift is two independent pins that do not follow an edit — one in
    /// <c>CiphertextEnvelopeTests</c> asserting the relation to
    /// <see cref="CiphertextEnvelope.MinimumLength"/>, and one in <c>WrappedAccountKeysTests</c> building
    /// every value it feeds this factory from a literal of its own. Move the format's widths and the first
    /// goes red; move this constant and both do. Do not read the expression as the guard and delete
    /// either.
    /// </para>
    /// <para>
    /// <see langword="const"/> and not a computed property because the widths on this type are read in
    /// <c>[Arguments(...)]</c> and in default parameter values, neither of which admits anything but a
    /// constant expression.
    /// </para>
    /// </remarks>
    public const int WrappedPrivateKeyLength = CiphertextEnvelope.MinimumLength + PrivateKeyBytes;

    /// <summary>
    /// The one AEAD envelope version defined today (IFR-007): AES-256-GCM, 96-bit nonce, 128-bit tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The version is refused <em>here</em> rather than left to the client because the successor does not
    /// exist: a row carrying version 2 is a client claiming a contract this deployment has never
    /// implemented, and storing it would file bytes no version of this system can interpret. The column's
    /// check constraints restate this bound and the width one, rendered from these constants rather than
    /// typed out beside them; this factory is where the mistake is cheap.
    /// </para>
    /// <para>
    /// <b>An alias for <see cref="CiphertextEnvelope.Version"/>, not a second declaration of it, and
    /// never <see cref="EncapsulatedValueEnvelope.Version"/>.</b> A wrapped private key and a sealed
    /// narrative field are the same format over different plaintexts, so this entity has no AEAD version
    /// of its own to state — that is the legal derivation. The forbidden spelling is the one next door:
    /// <c>= EncapsulatedValueEnvelope.Version</c> compiles, holds the same <c>1</c>, and reddens nothing,
    /// and it would make a bump to the <em>encapsulation</em> suite renumber a value this column stores
    /// under the AEAD one.
    /// </para>
    /// <para>
    /// <b>The alias is not what keeps the two bytes equal.</b> Measured on the constant it replaces:
    /// writing <c>1</c> out here leaves the unit suite green. Two independent pins are what would notice —
    /// <c>CiphertextEnvelopeTests.Version_IsTheOneVersionDefined</c>, which asserts the format's byte is
    /// literally 1, and <c>WrappedAccountKeysTests</c>, which builds accepted values from a literal
    /// <c>1</c> of its own and would refuse them all if this constant moved. Neither follows an edit to
    /// the constant it checks, which is the whole of why they are literals.
    /// </para>
    /// </remarks>
    public const byte WrappedPrivateKeyVersion = CiphertextEnvelope.Version;

    /// <summary>
    /// The only legal width of <see cref="EncapsulatedAccountKeys"/>: the encapsulation framing stretched
    /// over both account keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A width, not a cap</b>, for the reason <see cref="WrappedPrivateKeyLength"/> gives — the AEAD
    /// at the end of the key agreement produces ciphertext exactly as long as its plaintext, and the
    /// plaintext here is two fixed-width keys. It is <em>smaller</em> than the wrapped private key beside
    /// it despite carrying a 65-byte ephemeral point, which is worth noticing before somebody
    /// "corrects" one of the two: 158 against 167 is what a 64-byte plaintext against a 138-byte one
    /// comes to.
    /// </para>
    /// <para>
    /// <b>Computed from <see cref="EncapsulatedValueEnvelope.MinimumLength"/>, which is the legal
    /// direction under the type's rule</b> — an encapsulated account-keys value is a value of that suite,
    /// and the version, point, nonce and tag widths are that format's to state. Deriving it from
    /// <see cref="CiphertextEnvelope.MinimumLength"/> is the forbidden spelling, and it is the easy one to
    /// write, because the two differ by exactly the ephemeral point and the shorter expression looks like
    /// the shared one: a value accepted at 93 bytes has no room for a point at all.
    /// </para>
    /// <para>
    /// <b>The arithmetic is a way of saying it, not the thing that holds it</b>, the same argument
    /// <see cref="WrappedPrivateKeyLength"/> makes and for the same reason: nothing in the build derives
    /// one of these numbers from the other, so what catches a drift is the pin in
    /// <c>EncapsulatedValueEnvelopeTests</c> over the format's own widths and the literal
    /// <c>WrappedAccountKeysTests</c> builds its values from.
    /// </para>
    /// </remarks>
    public const int EncapsulatedAccountKeysLength =
        EncapsulatedValueEnvelope.MinimumLength + AccountKeysPlaintextBytes;

    /// <summary>
    /// The one encapsulation version defined today (IFR-014, IFR-015): ECDH over NIST P-256,
    /// HKDF-SHA-256 with an empty salt, then AES-256-GCM with a 96-bit nonce and a 128-bit tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An alias for <see cref="EncapsulatedValueEnvelope.Version"/>, and never
    /// <see cref="CiphertextEnvelope.Version"/>.</b> The legal derivation, under the type's rule: the
    /// value this column stores is an encapsulated value of that suite, so the entity has no version of
    /// its own to state. The forbidden spelling is <c>= CiphertextEnvelope.Version</c>, which is what a
    /// reader tidying four constants into two will write, since both are <c>1</c> and the AEAD constant is
    /// the one already named twice on this type. It compiles, it is green, and it makes a bump to the AEAD
    /// suite renumber a column storing values of the other one — bytes claiming a suite no client ever
    /// agreed to, authenticating perfectly and opening nothing, discovered on the day somebody rotates.
    /// </para>
    /// <para>
    /// <b>Nothing in the build can tell the two spellings apart</b>, which is why the argument has to live
    /// in prose here. Measured, and recorded at <see cref="EncapsulatedValueEnvelope.Version"/>:
    /// <c>FieldInfo.GetRawConstantValue()</c> returns <c>1</c> and <c>IsLiteral</c> is
    /// <see langword="true"/> for an alias exactly as for a literal, so the compiler folds the spelling
    /// away before anything can read it. The source-text census that closes this on the two format types
    /// does not reach this file.
    /// </para>
    /// </remarks>
    public const byte EncapsulatedAccountKeysVersion = EncapsulatedValueEnvelope.Version;

    private WrappedAccountKeys()
    {
    }

    /// <summary>
    /// The credential the factor was registered under — a passkey's own credential, or the one
    /// credential standing for a whole issued set of recovery codes.
    /// </summary>
    /// <remarks>
    /// <b>Not the identity of the row and not unique.</b> A set's ten rows all carry this same value,
    /// which is the reason the key is <see cref="FactorId"/>: keyed here, a set could store one key pair
    /// and nine of its codes would hold nothing that opens the account.
    /// </remarks>
    public Guid CredentialId { get; private set; }

    /// <summary>
    /// The client-minted identifier of the factor, and the associated data of the wrapped private key.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <see cref="Credential.Id"/>.</b> The first leg of
    /// <see href="../../../docs/decisions/0014-scope-the-credential-delete-in-the-application.md">ADR
    /// 0014</see> is that no source of a <see cref="Credential"/> accepts a caller-chosen id — every
    /// factory mints its own — so a fabricated instance can never name an existing row. That holds
    /// because the credential deletes are issued by primary key against a table carrying no row-level
    /// security policy, which leaves the id's unguessability doing real work. Binding a wrapped value to
    /// <c>credentials.id</c> would put that id in the client's hands and, worse, would require the
    /// client to choose it before the credential existed.
    /// </remarks>
    public Guid FactorId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>
    /// The type of the credential this row was filed against, carried on the row for the reason
    /// <see cref="PasskeyPublicKey.CredentialType"/> gives.
    /// </summary>
    public CredentialType CredentialType { get; private set; }

    /// <summary>
    /// The factor's ECDH P-256 private key, <em>wrapped under</em> the key-encryption key that factor
    /// derives — the one value on this row that a key-encryption key alone opens.
    /// </summary>
    /// <remarks>
    /// It is what makes this row a two-step opening rather than a one-step one: a factor presented gives
    /// a key-encryption key, that unwraps this, and this decapsulates
    /// <see cref="EncapsulatedAccountKeys"/>. The public half is not here and is not on any row — see
    /// <see cref="FactorManifest"/>, which is its sole carrier.
    /// </remarks>
    public ReadOnlyMemory<byte> WrappedPrivateKey { get; private set; }

    /// <summary>
    /// The account's content key and index key as one 64-byte plaintext, content key first,
    /// <em>encapsulated to</em> this factor's public key.
    /// </summary>
    /// <remarks>
    /// The order of the two halves is a client contract this server cannot check and will never be able
    /// to — see the type's remarks, where the consequence of getting it backwards is spelled out.
    /// </remarks>
    public ReadOnlyMemory<byte> EncapsulatedAccountKeys { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// Files <paramref name="credential"/>'s factor: its wrapped private key, and the account's two keys
    /// encapsulated to the public half of that pair.
    /// </summary>
    /// <remarks>
    /// <b>It takes the loaded credential rather than three loose ids</b>, the argument
    /// <see cref="PasskeyPublicKey.Register"/> and <see cref="RecoveryCodeHash.From"/> both make. All
    /// three copied columns are compared against <c>credentials(id, user_id, type)</c> by a composite
    /// foreign key, so a row whose owner disagreed with its credential's is unstorable — but a factory
    /// taking three ids is one transposed argument away from filing an account's key material against
    /// another account's factor, and the id it would need is one the caller already has in hand.
    /// </remarks>
    /// <exception cref="ArgumentNullException">No credential was supplied.</exception>
    /// <exception cref="ValidationException">
    /// The credential does not stand for a recovery factor, the factor identifier is empty, the wrapped
    /// private key is not <see cref="WrappedPrivateKeyLength"/> bytes carrying version
    /// <see cref="WrappedPrivateKeyVersion"/>, or the encapsulated account keys are not
    /// <see cref="EncapsulatedAccountKeysLength"/> bytes carrying version
    /// <see cref="EncapsulatedAccountKeysVersion"/>.
    /// </exception>
    public static WrappedAccountKeys For(
        Credential credential,
        Guid factorId,
        ReadOnlyMemory<byte> wrappedPrivateKey,
        ReadOnlyMemory<byte> encapsulatedAccountKeys,
        DateTime createdAtUtc)
    {
        // The owner and the type are both read off the credential, so there is nothing to validate
        // without one. No user typed this; a caller handed over nothing.
        ArgumentNullException.ThrowIfNull(credential);

        Dictionary<string, string[]> errors = new();

        // Enumerated as the two factors that have a key-encryption key, so that adding a third
        // credential type does not silently gain the ability to hold a share of the account's keys.
        // Identity and key custody are two tiers and the provider is only ever on the first: OAuth has no
        // PRF equivalent, so a row filed against the federated credential would be a private key nothing
        // in the world can unwrap, presented as a way back into the account.
        if (credential.Type is not (CredentialType.Passkey or CredentialType.RecoveryCodes))
        {
            errors[nameof(CredentialType)] =
                ["Account keys may only be encapsulated to a passkey or a set of recovery codes."];
        }

        // All-zeros is a storable uuid and it is what an unset field sends. It is also the one value two
        // accounts reach independently, so accepting it turns the primary key into a cross-account
        // collision the second account experiences as a refusal to register — and since it is the
        // associated data of the wrapped private key, nothing downstream can tell a deliberate zero from
        // a mistake. Within one account it is worse than that: a set of recovery codes writes ten of
        // these in one save, so a client sending the same identifier twice would take its own set down.
        if (factorId == Guid.Empty)
        {
            errors[nameof(FactorId)] = ["Factor id is required."];
        }

        // Two describers rather than one, and each is keyed on the property its value ends up in, as
        // PasskeyPublicKey.Register keys its own length refusals.
        if (DescribeMalformedWrappedPrivateKey(wrappedPrivateKey) is { } privateKeyProblem)
        {
            errors[nameof(WrappedPrivateKey)] = [privateKeyProblem];
        }

        if (DescribeMalformedEncapsulatedAccountKeys(encapsulatedAccountKeys) is { } accountKeysProblem)
        {
            errors[nameof(EncapsulatedAccountKeys)] = [accountKeysProblem];
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
            // for the next factor would rewrite key material that has already been accepted.
            WrappedPrivateKey = wrappedPrivateKey.ToArray(),
            EncapsulatedAccountKeys = encapsulatedAccountKeys.ToArray(),
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Says what is wrong with <paramref name="envelope"/> as a wrapped private key, or
    /// <see langword="null"/> if it is well-formed.
    /// </summary>
    /// <remarks>
    /// <b>One describer per column, and a shared helper here would be the defect.</b> The single
    /// definition this replaced was argued as "one definition, so the two columns cannot end up judged by
    /// different rules" — and that argument inverts the moment the two columns stop holding the same kind
    /// of value. They now differ in width, in floor, in version constant and in cryptographic suite, so a
    /// helper parameterised over those four is a helper with nothing left of its own, and the one thing it
    /// would still share is the sentence — which is exactly what must not be shared, because a message
    /// naming "an envelope" tells a client nothing about which of the two framings this deployment was
    /// judging its bytes by.
    /// <para>
    /// <b>Width before version, and not merely for message quality:</b> an empty value has no leading byte
    /// to read, so a version check reached first throws <see cref="IndexOutOfRangeException"/> out of the
    /// Domain and a client that sent an unset field is told the server broke rather than that its bytes
    /// were malformed.
    /// </para>
    /// </remarks>
    private static string? DescribeMalformedWrappedPrivateKey(ReadOnlyMemory<byte> envelope) =>
        envelope.Length != WrappedPrivateKeyLength
            ? $"A wrapped private key must be exactly {WrappedPrivateKeyLength} bytes."
            : envelope.Span[0] != WrappedPrivateKeyVersion
                ? $"A wrapped private key must carry AEAD framing version {WrappedPrivateKeyVersion}."
                : null;

    /// <summary>
    /// Says what is wrong with <paramref name="value"/> as an encapsulated pair of account keys, or
    /// <see langword="null"/> if it is well-formed.
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="DescribeMalformedWrappedPrivateKey"/>, naming its own suite for the reason
    /// stated there, and ordering its two checks the same way for the same reason. Every number it reads
    /// belongs to the encapsulation framing; borrowing one from the AEAD side would accept a value 65
    /// bytes too short to hold an ephemeral point.
    /// </remarks>
    private static string? DescribeMalformedEncapsulatedAccountKeys(ReadOnlyMemory<byte> value) =>
        value.Length != EncapsulatedAccountKeysLength
            ? $"Encapsulated account keys must be exactly {EncapsulatedAccountKeysLength} bytes."
            : value.Span[0] != EncapsulatedAccountKeysVersion
                ? "Encapsulated account keys must carry encapsulation framing version "
                  + $"{EncapsulatedAccountKeysVersion}."
                : null;
}
