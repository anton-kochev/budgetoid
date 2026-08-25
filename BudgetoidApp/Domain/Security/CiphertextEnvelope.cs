namespace Domain.Security;

/// <summary>
/// The framing every AEAD envelope this system stores carries:
/// <c>version(1) || nonce(12) || ciphertext || tag(16)</c>, with <c>0x01</c> — AES-256-GCM, 96-bit
/// nonce, 128-bit tag — as the only version defined.
/// </summary>
/// <remarks>
/// <para>
/// <b>In <c>Domain</c> because the numbers already live here.</b>
/// <see cref="Users.WrappedAccountKeys.EnvelopeLength"/> and
/// <see cref="Users.WrappedAccountKeys.EnvelopeVersion"/> are what the persistence check constraints
/// are written from, so a format type sitting one ring out would be a second home for a rule the
/// innermost ring already owns. In <c>Security</c> rather than <c>Common</c> because that folder holds
/// exception types: a wire format is a domain concept, not a utility.
/// </para>
/// <para>
/// <b>Framing is the whole of what is checkable here, and that is not a shortcoming.</b> The server
/// holds no value that opens an envelope — every key-encryption key is derived in a browser from a
/// recovery factor this server never sees — so the only questions answerable on this side are whether
/// the leading byte is one this deployment recognises and whether the fixed parts of the format have
/// room to exist. A nonce of zeros and a tag of zeros are well-formed by every rule this type owns.
/// Anything stronger would need a key, and a design in which the server had one is the design this
/// product exists to avoid.
/// </para>
/// <para>
/// <b>This type answers and never refuses.</b> The refusal sentences belong to the callers:
/// <see cref="Users.WrappedAccountKeys"/> keeps the entity's, and the request surface keeps the wire's,
/// which differ from each other on purpose. A message-producing member here would be a fourth owner of
/// one rule's wording, and the copy nobody updates is always the one furthest from the reader.
/// </para>
/// <para>
/// <b>What it deliberately does not carry is a width.</b> The wrapped-key path has exactly one legal
/// size and enforces it on top of this, where it is correct; narrative text is as long as whatever
/// somebody typed, and AES-GCM ciphertext is exactly the length of its plaintext, so there the version
/// byte is the only thing standing between this server and bytes no version of it can interpret.
/// Folding a width back in here would refuse every entry longer than an empty one, and the person would
/// find out by not being able to save what they wrote.
/// </para>
/// </remarks>
public static class CiphertextEnvelope
{
    /// <summary>
    /// The one envelope version defined today (IFR-007): AES-256-GCM, 96-bit nonce, 128-bit tag.
    /// </summary>
    /// <remarks>
    /// Version 2 is a client claiming a contract this deployment has never implemented; version 0 is a
    /// field nobody set — an all-zero buffer of a legal length is what an uninitialised member, a
    /// zero-filled allocation and a stubbed client all send. Stored either way the symptom is silent and
    /// late: the row looks fine and the bytes turn out to be uninterpretable on the day somebody needs
    /// them back.
    /// </remarks>
    public const byte Version = 1;

    /// <summary>The width of the leading version byte.</summary>
    public const int VersionBytes = 1;

    /// <summary>The width of the AES-GCM nonce that follows the version byte — 96 bits.</summary>
    public const int NonceBytes = 12;

    /// <summary>The width of the AES-GCM authentication tag that closes the envelope — 128 bits.</summary>
    public const int TagBytes = 16;

    /// <summary>
    /// The shortest envelope the format can produce: a version, a nonce and a tag, with no ciphertext
    /// between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A floor, not a width.</b> An empty plaintext is a legitimate value — a note with no text, a
    /// field somebody cleared — and it seals to exactly this many bytes, which is why
    /// <see cref="IsWellFormed"/> compares with <c>&gt;=</c>. Anything longer is the same format over a
    /// longer plaintext.
    /// </para>
    /// <para>
    /// <b>Const arithmetic over the three components, and it has to stay const and stay arithmetic.</b>
    /// A method or a computed property would not compile at the call sites that need this number in an
    /// attribute argument, and a hand-written <c>29</c> would let the sum and its parts drift apart. The
    /// layout is the contract — these three widths are what a client's AES-GCM implementation slices on
    /// — so the parts are what the tests pin and the sum is what the code reads.
    /// </para>
    /// </remarks>
    public const int MinimumLength = VersionBytes + NonceBytes + TagBytes;

    /// <summary>
    /// Whether <paramref name="envelope"/> is long enough to hold the format's fixed parts and leads
    /// with a version this deployment recognises.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Length is judged before version, and the order is a correctness rule rather than a style
    /// one.</b> An empty buffer — an absent member, a zero-length field — has no leading byte for a
    /// version check to look at, so an implementation reaching for <c>envelope[0]</c> first would not
    /// answer <see langword="false"/>; it would throw, out of a method whose entire contract is to
    /// answer without one. The <c>&amp;&amp;</c> below is what enforces that order, and swapping the two
    /// operands is the edit a later reader will make while thinking they changed nothing.
    /// </para>
    /// <para>
    /// It says nothing about whether the envelope opens. That answer needs a key, and this side has
    /// none.
    /// </para>
    /// </remarks>
    /// <param name="envelope">The bytes as they arrived or as they were stored.</param>
    /// <returns>
    /// <see langword="true"/> when the framing is one this deployment can interpret; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool IsWellFormed(ReadOnlySpan<byte> envelope) =>
        envelope.Length >= MinimumLength && envelope[0] == Version;
}
