namespace Domain.Security;

/// <summary>
/// The framing a value encapsulated to a recovery factor's public key carries:
/// <c>version(1) || ephemeral public key(65) || nonce(12) || ciphertext || tag(16)</c>, with
/// <c>0x01</c> — ECDH over NIST P-256, HKDF-SHA-256 with an empty salt, then AES-256-GCM with a
/// 96-bit nonce and a 128-bit tag — as the only version defined.
/// </summary>
/// <remarks>
/// <para>
/// <b>In <c>Domain</c>, and in <c>Security</c>, for the reasons <see cref="CiphertextEnvelope"/>
/// gives.</b> A wire format is a domain concept rather than a utility, and the layer that owns the
/// numbers a persistence check constraint is written from is the innermost one. Sitting beside the
/// sibling is itself part of the point: the two framings are read together or one of them is read
/// wrong.
/// </para>
/// <para>
/// <b>The word is <em>encapsulated</em> and not <em>sealed</em>, and that is a decision rather than a
/// preference.</b> <em>Sealed under</em> already names symmetric encryption throughout this repository
/// — a narrative field is sealed under the content key, an account key is wrapped under a factor's
/// key-encryption key — so "sealed <em>to</em> a public key" would stand one preposition away from
/// "sealed <em>under</em> a key", for two constructions whose confusion is silent: both spellings
/// compile, both sets of bytes authenticate, and the wrong one opens nothing. <em>Encapsulated</em> is
/// where RFC 9180 puts the word: it calls the ECDH step a <em>key encapsulation</em>, and calls the
/// ephemeral public key this format carries the <em>encapsulated key</em>. Tidying the name back to
/// <em>sealed</em> restores exactly the collision the choice was made to avoid. What the AEAD after the
/// agreement does is still <em>sealing</em>, because there the word is in its symmetric sense and is
/// the right one.
/// </para>
/// <para>
/// <b>A second type rather than a wider <see cref="CiphertextEnvelope"/>, because the layouts differ
/// in the middle and not at the end.</b> The 65-byte ephemeral point sits <em>between</em> the version
/// byte and the nonce, so a reader slicing encapsulated bytes by the AEAD framing takes the first twelve
/// bytes of a P-256 point for a nonce and the rest of the point for ciphertext — and gets an
/// authentication failure at the far end of a key rotation rather than a refusal here. Neither format
/// carries a byte saying which of the two it is; both lead with <c>0x01</c>. The column the bytes were
/// read from is the only thing that distinguishes them, which is exactly why each needs a floor, a
/// version and a well-formedness rule of its own.
/// </para>
/// <para>
/// <b>Why this format exists at all.</b> Under the AEAD framing an account key is wrapped
/// <em>symmetrically</em>, under a key-encryption key a factor derives — so whoever wraps holds the
/// same key as whoever unwraps, and re-wrapping under a new key needs every authenticator physically
/// present. Encapsulating to a factor's public half would need only that public half, so a value could
/// be encapsulated to a factor nobody is holding. That capability is why the layout is written down
/// here before anything produces one: no factor has a keypair today and no column stores a value in
/// this framing, and the numbers a second implementation slices on are worth fixing while there are no
/// stored bytes to disagree with.
/// </para>
/// <para>
/// <b>Framing is the whole of what is checkable here, and that is not a shortcoming.</b> Opening a
/// value in this framing takes the private half of the key it was encapsulated to, and the server holds
/// no private key of any kind — nothing is typed for one and no route accepts one — so it can run
/// neither the ECDH this format names nor the AES-GCM after it. That is structural rather than a gap,
/// and it is the same structure the sibling rests on: key material that never reaches this server
/// cannot be read off it, and a design in which the server held such a key is the design this product
/// exists to avoid. The questions answerable on this side are whether the leading byte is one this
/// deployment recognises and whether the fixed parts have room to exist. An ephemeral "public key" of
/// 65 zeros is not a point on P-256 and is well-formed by every rule this type owns. Anything stronger
/// would need a key.
/// </para>
/// <para>
/// <b>This type answers and never refuses.</b> The refusal sentences belong to the callers, for the
/// reason the sibling gives: a message-producing member here would be another owner of one rule's
/// wording, and the copy nobody updates is always the one furthest from the reader.
/// </para>
/// <para>
/// <b>What it deliberately does not carry is a width.</b> An encapsulated value carries a plaintext,
/// and how long that plaintext is belongs to whatever entity comes to store it — the same split
/// <see cref="CiphertextEnvelope"/> argues, where the wrapped-key path enforces its one legal size on
/// top of the format rather than inside it. Folding a width in here would tie the format to its first
/// consumer and refuse the next.
/// </para>
/// </remarks>
public static class EncapsulatedValueEnvelope
{
    /// <summary>
    /// The one encapsulated-value version defined today (IFR-014, IFR-015): ECDH over NIST P-256,
    /// HKDF-SHA-256 with an empty salt, then AES-256-GCM with a 96-bit nonce and a 128-bit tag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The literal <c>1</c>, transcribed, and never
    /// <c>= CiphertextEnvelope.Version</c>.</b> The two constants hold the same number today by
    /// coincidence of both suites being first, and they number <em>different</em> cryptography: one byte
    /// says "AES-256-GCM under a key both sides hold", the other says "ECDH to a public key, then
    /// AES-256-GCM". Aliased, a bump to either suite silently renumbers the other, and the failure is
    /// not visible at the edit — a value produced under a version the client never agreed to
    /// authenticates perfectly and opens nothing, on the day somebody rotates a key.
    /// </para>
    /// <para>
    /// <b>Nothing in the build can tell the two spellings apart.</b> Measured:
    /// <c>FieldInfo.GetRawConstantValue()</c> returns <c>1</c> and <c>IsLiteral</c> is
    /// <see langword="true"/> for the alias exactly as for the literal, so the census over these
    /// declarations cannot see the difference and neither can the compiler. What holds this is the
    /// transcribed pin next door — <c>EncapsulatedValueEnvelopeTests.Version_IsTheOneVersionDefined</c>
    /// asserts the byte is literally 1, and would go on passing against an alias whose target had moved,
    /// which is why the argument has to live here in prose. Do not "de-duplicate" these two constants.
    /// </para>
    /// <para>
    /// Version 2 is a client claiming a suite this deployment has never implemented; version 0 is a field
    /// nobody set — an all-zero buffer of a legal length is what an uninitialised member, a zero-filled
    /// allocation and a stubbed client all send. Stored either way the symptom is silent and late: the
    /// row looks fine and the bytes turn out to be uninterpretable when they are next needed.
    /// </para>
    /// </remarks>
    public const byte Version = 1;

    /// <summary>The width of the leading version byte.</summary>
    /// <remarks>
    /// An alias, unlike <see cref="Version"/> beside it, and the difference is the whole point: this
    /// number is not a suite identifier but a shared layout width. The two formats put one byte of
    /// version at the front because it is the same framing convention, so there is one owner of the
    /// number and this is a second name for it.
    /// </remarks>
    public const int VersionBytes = CiphertextEnvelope.VersionBytes;

    /// <summary>
    /// The width of the ephemeral P-256 public key that follows the version byte — an uncompressed SEC1
    /// point (IFR-019), a <c>0x04</c> prefix and two 32-byte coordinates.
    /// </summary>
    /// <remarks>
    /// The one width with no twin next door, and the one a later reader can get wrong honestly: a
    /// compressed point is 33 bytes and a bare coordinate pair is 64. Either number re-cuts every slice
    /// after it, so the nonce a client reads is not the nonce that was written and the tag never
    /// verifies — with nothing at this layer able to say so, because a point is not checkable without
    /// running the curve arithmetic the server has no reason to hold.
    /// </remarks>
    public const int EphemeralPublicKeyBytes = 65;

    /// <summary>The width of the AES-GCM nonce that follows the ephemeral point — 96 bits.</summary>
    /// <remarks>
    /// An alias for the reason <see cref="VersionBytes"/> gives: the AEAD after the key agreement is the
    /// same AES-256-GCM the sibling names, so the nonce width has one owner.
    /// </remarks>
    public const int NonceBytes = CiphertextEnvelope.NonceBytes;

    /// <summary>
    /// The width of the AES-GCM authentication tag that closes an encapsulated value — 128 bits.
    /// </summary>
    /// <remarks>
    /// An alias for the reason <see cref="VersionBytes"/> gives: same AEAD, same tag width, one owner.
    /// </remarks>
    public const int TagBytes = CiphertextEnvelope.TagBytes;

    /// <summary>
    /// The shortest encapsulated value the format can produce: a version, an ephemeral point, a nonce
    /// and a tag, with no ciphertext between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A floor, not a width.</b> AES-GCM ciphertext is exactly the length of its plaintext, so an
    /// empty plaintext comes to exactly this many bytes and anything longer is the same format over a
    /// longer one — which is why <see cref="IsWellFormed"/> compares with <c>&gt;=</c>. How wide the
    /// plaintext actually is belongs to whatever entity comes to store the value, the split
    /// <see cref="CiphertextEnvelope.MinimumLength"/> argues for the AEAD framing and
    /// <see cref="Users.WrappedAccountKeys.EnvelopeLength"/> is the other half of. An implementation
    /// comparing this for equality would be correct for exactly one consumer and would refuse every
    /// other.
    /// </para>
    /// <para>
    /// <b>Const arithmetic over the four components, and it has to stay const and stay arithmetic.</b> A
    /// method or a computed property would not compile where a number like this is needed in an
    /// attribute argument or a default parameter value, and a hand-written <c>94</c> would let the sum
    /// and its parts drift apart. The layout is the contract — these four widths are what a client's own
    /// implementation slices on — so the parts are what the tests pin and the sum is what the code reads.
    /// </para>
    /// <para>
    /// It is 65 bytes longer than <see cref="CiphertextEnvelope.MinimumLength"/>, and that gap is what
    /// makes a buffer sized for the AEAD framing refusable here rather than merely short.
    /// </para>
    /// </remarks>
    public const int MinimumLength = VersionBytes + EphemeralPublicKeyBytes + NonceBytes + TagBytes;

    /// <summary>
    /// Whether <paramref name="value"/> is long enough to hold the format's fixed parts and leads with a
    /// version this deployment recognises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Length is judged before version, and the order is a correctness rule rather than a style
    /// one.</b> An empty buffer — an absent member, a zero-length column — has no leading byte for a
    /// version check to look at, so an implementation reaching for <c>value[0]</c> first would not answer
    /// <see langword="false"/>; it would throw, out of a method whose entire contract is to answer
    /// without one. The <c>&amp;&amp;</c> below is what enforces that order, and swapping the two
    /// operands is the edit a later reader will make while thinking they changed nothing.
    /// </para>
    /// <para>
    /// <b>Not <c>CiphertextEnvelope.IsWellFormed(value)</c>.</b> The two suites share a version byte and
    /// three of their five widths, so delegating reads as reuse rather than as a mistake — and it would
    /// admit everything from 29 bytes upward, which means every buffer 65 bytes too short to hold an
    /// ephemeral point would pass as an encapsulated value.
    /// </para>
    /// <para>
    /// It says nothing about whether the value opens, and nothing about whether the 65 bytes it counted
    /// are a point on the curve. Both answers need cryptography this side does not run.
    /// </para>
    /// </remarks>
    /// <param name="value">The bytes as they arrived or as they were stored.</param>
    /// <returns>
    /// <see langword="true"/> when the framing is one this deployment can interpret; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool IsWellFormed(ReadOnlySpan<byte> value) =>
        value.Length >= MinimumLength && value[0] == Version;
}
