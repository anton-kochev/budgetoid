using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// <see cref="ClientKeyCustody" /> against two frozen vector files:
/// <c>docs/business-logic/vectors/factor-keypair-v1.json</c>, computed outside this codebase by a third
/// implementation and already reproduced, value for value, by the browser; and
/// <c>docs/business-logic/vectors/narrative-field-v1.json</c>, which the browser also reproduces and
/// which does not say who computed it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the file that closes the hole a round trip cannot reach.</b> The browser's own mutation
/// matrix established it: a lookup table with no cryptography in it at all passes every client test,
/// because the client's mint draws its own randomness and nothing frozen can be pushed through that API.
/// A second implementation, in another language, reproducing the same frozen bytes is what says the two
/// sides agree — and it says it about the <em>write</em> direction, which is the one a round trip is
/// blind to. Everything below is therefore a <b>pin</b>: a manufactured red is the only evidence that any
/// one of these lines does work, and the mutations each one catches are named in its own remarks.
/// </para>
/// <para>
/// <b>Nothing here touches PostgreSQL, and it lives in this assembly anyway.</b> The subject is a type in
/// this assembly that nothing else can see. Moving the pins to the unit tier would mean moving the
/// reproduction with them, away from its callers here — the eleven-factor registration test,
/// <see cref="AccountKeyFixture" /> and <c>RecoveryCodeCanonicalFormTests</c>.
/// </para>
/// <para>
/// <b>Bytes are compared as lower-case hex, never as collections.</b> TUnit's <c>IsEquivalentTo</c>
/// defaults to <c>CollectionOrdering.Any</c>, which is exactly blind to the one rule the manifest's
/// plaintext has — that its entries ascend. A hex string compares in order by construction, and a
/// failure prints something a reader can diff against the vector file.
/// </para>
/// <para>
/// <b>What a vector cannot hold, restated so nobody reads this file as more than it is.</b> The
/// encapsulated plaintext's half order is pinned only in the sense that the frozen 158-byte value opens
/// to two named keys in one order; the substitution property that binding both points into the HKDF
/// <c>info</c> buys is not pinned at all, here or anywhere, because a vector shows that the bytes are
/// present and never that they help. And no vector anywhere says which factor a handler filed a row
/// under — that claim belongs to <c>AccountRegistrationTests</c> and to the tag it opens with.
/// </para>
/// <para>
/// <b>A second file is read here too: <c>docs/business-logic/vectors/narrative-field-v1.json</c></b>,
/// which the browser also reproduces. Its cases pin the narrative associated-data grammar, the sealed
/// envelope and its wire spelling under the frozen nonce, and the open back — what
/// <see cref="AccountKeyFixture" /> seals and opens with.
/// </para>
/// </remarks>
public sealed class ClientKeyCustodyTests
{
    /// <summary>
    /// The frozen message a wrapped private key is bound to, reproduced field for field.
    /// </summary>
    /// <remarks>
    /// The longer of the two, and the only one carrying <see cref="ClientKeyCustody.PrivateKeyPurpose" />.
    /// Drop that field and what comes out is the <em>other</em> valid message of this same scheme, which
    /// is why the pair below it exists rather than one test and a derivation.
    /// </remarks>
    [Test]
    public async Task WrappedPrivateKeyAssociatedData_ReproducesTheFrozenMessage()
    {
        // Arrange
        FrozenVector vector = Vectors.Single(
            "wrappedPrivateKeyAssociatedData",
            candidate => candidate.GetProperty("version").GetInt32() == 1);

        // Act
        byte[] message = ClientKeyCustody.WrappedPrivateKeyAssociatedData(Vectors.FactorId);

        // Assert
        await Assert.That(Hex(message)).IsEqualTo(vector.Hex);
        await Assert.That(message.Length).IsEqualTo(vector.LengthBytes);
    }

    /// <summary>
    /// The frozen message an encapsulated pair of account keys is bound to, reproduced field for field.
    /// </summary>
    /// <remarks>
    /// Sixty-six bytes against seventy-eight, and pinned against its own frozen hex rather than derived
    /// from the message above by taking a prefix of it: a pin computed from the value it is checking
    /// agrees with whatever that value later becomes.
    /// </remarks>
    [Test]
    public async Task EncapsulatedAccountKeysAssociatedData_ReproducesTheFrozenMessage()
    {
        // Arrange
        FrozenVector vector = Vectors.Single("encapsulatedAccountKeysAssociatedData");

        // Act
        byte[] message = ClientKeyCustody.EncapsulatedAccountKeysAssociatedData(Vectors.FactorId);

        // Assert
        await Assert.That(Hex(message)).IsEqualTo(vector.Hex);
        await Assert.That(message.Length).IsEqualTo(vector.LengthBytes);
    }

    /// <summary>
    /// The shorter message is a <b>strict byte prefix</b> of the longer, which is the whole reason there
    /// are three builders and never one parameterised by purpose.
    /// </summary>
    /// <remarks>
    /// <b>It asserts the hazard rather than the fix, and that is deliberate.</b> A shared builder handed
    /// nothing for its purpose field does not produce garbage that fails to open — it produces a message
    /// this scheme considers valid, so a value sealed under it is bound to the wrong thing and opens
    /// under that binding for ever. This case is what a reader meets before reaching for the
    /// de-duplication, and it is over the two frozen strings rather than over two calls, so it stays true
    /// about the format even if both methods moved together.
    /// </remarks>
    [Test]
    public async Task EncapsulatedAccountKeysAssociatedData_IsAStrictPrefixOfTheWrappedPrivateKeyMessage()
    {
        // Arrange
        string shorter = Vectors.Single("encapsulatedAccountKeysAssociatedData").Hex;
        string longer = Vectors
            .Single(
                "wrappedPrivateKeyAssociatedData",
                candidate => candidate.GetProperty("version").GetInt32() == 1)
            .Hex;

        // Act
        bool isPrefix = longer.StartsWith(shorter, StringComparison.Ordinal);

        // Assert
        await Assert.That(isPrefix).IsTrue();
        await Assert.That(longer.Length).IsGreaterThan(shorter.Length);
    }

    /// <summary>
    /// The frozen manifest message at both frozen epochs.
    /// </summary>
    /// <remarks>
    /// <b>Epoch 10 is the case and epoch 1 is the control, and the second argument earns its place —
    /// measured.</b> A raw byte in place of the digits fails both arguments, so epoch 1 alone would have
    /// caught that one. What epoch 1 alone cannot catch is a rendering that happens to agree on a single
    /// digit: <c>ToString("x")</c> writes <c>1</c> for one and <c>a</c> for ten, and with only the first
    /// argument in the file that mutation is green. That is the whole of what "decimal, and two digits
    /// prove it" means, and it is why the vector file freezes both.
    /// </remarks>
    [Test]
    [Arguments(1)]
    [Arguments(10)]
    public async Task ManifestAssociatedData_ReproducesTheFrozenMessage(int rotationEpoch)
    {
        // Arrange
        FrozenVector vector = Vectors.Single(
            "manifestAssociatedData",
            candidate => candidate.GetProperty("rotationEpoch").GetInt32() == rotationEpoch);

        // Act
        byte[] message = ClientKeyCustody.ManifestAssociatedData(rotationEpoch);

        // Assert
        await Assert.That(Hex(message)).IsEqualTo(vector.Hex);
        await Assert.That(message.Length).IsEqualTo(vector.LengthBytes);
    }

    /// <summary>
    /// The <b>builder</b> reproduces the frozen message at version <c>0x80</c>, which a text composition
    /// of the same field list cannot: it comes out one byte wider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one case that reaches the decision to compose messages from bytes, and the only way to
    /// reach it is a version this scheme does not have.</b> At <c>0x01</c> the two compositions are
    /// byte-identical — measured, by rewriting
    /// <see cref="ClientKeyCustody.EncapsulatedAccountKeysAssociatedData" /> as an interpolated string
    /// and watching all 21 cases here stay green. So nothing in this file, or in the browser's, can tell
    /// a text-composed grammar from a byte-composed one <em>today</em>. From <c>0x80</c> up UTF-8 widens
    /// one code point into two, silently, and the frozen message is the width the byte composition gives.
    /// </para>
    /// <para>
    /// <b>It calls the builder, and it used to call <c>JoinFields</c> directly — which guarded the helper
    /// rather than the grammar.</b> The grammar's version was a private constant, so the builders could
    /// not be driven at another version and this case assembled the field list itself. Nothing then said
    /// the three builders route through the join at all: rewriting any one of them as an interpolated
    /// string left this case green, because the only byte composition being compared was the one built
    /// here. <see cref="ClientKeyCustody.WrappedPrivateKeyAssociatedData" /> now takes the version, so an
    /// interpolated builder reddens — measured — and the join is <see langword="private" /> again, with
    /// the rule held by an accessibility keyword rather than by a sentence asking nobody to call it.
    /// </para>
    /// <para>
    /// <b>The rival is built here rather than described</b>, because "an interpolated string would be
    /// wrong" is precisely the claim a reader doubts, and the two lines below are the measurement. Both
    /// sides run over the same four fields; only how the version field gets into the message differs.
    /// The separator in the rival below is written as a unicode escape and never as the character
    /// itself, the rule <c>ClientKeyCustody.UnitSeparator</c> states: a literal control byte is
    /// invisible in every tool a reviewer would read this file in.
    /// </para>
    /// </remarks>
    [Test]
    public async Task WrappedPrivateKeyAssociatedData_AtAVersionThisSchemeDoesNotHave_ReproducesWhatTextCompositionCannot()
    {
        // Arrange
        const byte NextVersion = 0x80;
        FrozenVector vector = Vectors.Single(
            "wrappedPrivateKeyAssociatedData",
            candidate => candidate.GetProperty("version").GetInt32() == NextVersion);
        string factorId = ClientKeyCustody.CanonicalFactorId(Vectors.FactorId);

        // Act
        byte[] asBytes =
            ClientKeyCustody.WrappedPrivateKeyAssociatedData(Vectors.FactorId, NextVersion);

        byte[] asText = ClientKeyCustody.Utf8(
            $"{ClientKeyCustody.FactorKeypairLabel}\u001F{(char)NextVersion}\u001F{factorId}"
            + $"\u001F{ClientKeyCustody.PrivateKeyPurpose}");

        // Assert
        await Assert.That(Hex(asBytes)).IsEqualTo(vector.Hex);
        await Assert.That(asBytes.Length).IsEqualTo(vector.LengthBytes);
        await Assert.That(asText.Length).IsEqualTo(vector.LengthBytes + 1);
    }

    /// <summary>
    /// The 138-byte PKCS#8 encoding .NET produces for the frozen scalar is the one the vector file froze.
    /// </summary>
    /// <remarks>
    /// <b>The plaintext of the wrapped private key, so it comes before the envelope over it.</b> A
    /// disagreement here — an optional public key omitted, an explicit curve rather than a named one —
    /// would produce a wrapped value of another width that the server refuses, which is the loud failure;
    /// what this rules out is the quiet one, a 138-byte encoding whose bytes differ from the browser's.
    /// The key is built from the raw scalar and point rather than imported from the frozen PKCS#8,
    /// because importing it and exporting it back would be asking the encoder to agree with itself.
    /// </remarks>
    [Test]
    public async Task ExportPkcs8PrivateKey_ForTheFrozenScalar_ReproducesTheFrozenEncoding()
    {
        // Arrange
        using ECDiffieHellman factor = Vectors.FactorKeyPair();

        // Act
        byte[] pkcs8 = factor.ExportPkcs8PrivateKey();

        // Assert
        await Assert.That(Hex(pkcs8)).IsEqualTo(Vectors.Derived("factorPkcs8Hex"));
    }

    /// <summary>
    /// The factor's own point comes back out of its unwrapped private key, byte-identical.
    /// </summary>
    /// <remarks>
    /// <b>The measurement the opening path rests on.</b> The HKDF <c>info</c> names both points, so an
    /// opener needs the recipient's own — and the obvious answer, carrying a second copy of it beside the
    /// envelope, is a copy something can disagree with. This case is why there is no such copy:
    /// <c>ExportParameters(includePrivateParameters: false).Q</c> after <c>ImportPkcs8PrivateKey</c>
    /// returns a populated <c>Q</c>, so the point is already inside the value the first step produced.
    /// </remarks>
    [Test]
    public async Task UncompressedPoint_AfterImportingTheFrozenPkcs8_ReproducesTheFrozenPoint()
    {
        // Arrange
        using ECDiffieHellman reimported = ClientKeyCustody.CreateFactorKeyPair();
        reimported.ImportPkcs8PrivateKey(Convert.FromHexString(Vectors.Derived("factorPkcs8Hex")), out _);

        // Act
        byte[] point = ClientKeyCustody.UncompressedPoint(reimported.PublicKey);

        // Assert
        await Assert.That(Hex(point)).IsEqualTo(Vectors.Derived("factorPublicKeyHex"));
        await Assert.That(point.Length).IsEqualTo(ClientKeyCustody.PublicKeyBytes);
    }

    /// <summary>
    /// The frozen 198-byte HKDF <c>info</c>: both points raw, <b>ephemeral first</b>.
    /// </summary>
    /// <remarks>
    /// <b>The one message in the grammar whose fields are not all text, and the one a swap hides
    /// in.</b> Exchange the two points and two matched implementations still agree with each other and
    /// with nothing else — no round trip anywhere can see it, and neither can the server, which derives
    /// nothing. Push either point through UTF-8 instead of joining it raw and the message comes out 129
    /// bytes wider per point, silently. Both defects are one hex comparison away from red here and
    /// invisible everywhere else in this repository.
    /// </remarks>
    [Test]
    public async Task EncapsulationInfo_ReproducesTheFrozen198ByteMessage()
    {
        // Arrange
        FrozenVector vector = Vectors.Single("hkdfInfo");

        // Act
        byte[] info = ClientKeyCustody.EncapsulationInfo(
            Vectors.FactorId,
            Convert.FromHexString(Vectors.Derived("ephemeralPublicKeyHex")),
            Convert.FromHexString(Vectors.Derived("factorPublicKeyHex")));

        // Assert
        await Assert.That(Hex(info)).IsEqualTo(vector.Hex);
        await Assert.That(info.Length).IsEqualTo(vector.LengthBytes);
    }

    /// <summary>
    /// The raw agreement and the key HKDF makes of it, both frozen.
    /// </summary>
    /// <remarks>
    /// <b>Two values in one case because the second is worthless without the first.</b> A wrong
    /// encapsulation key is either a wrong agreement or a wrong derivation over the right one, and the
    /// two are different mistakes: the agreement is the platform's — .NET's raw secret agreement against
    /// WebCrypto's <c>deriveBits</c>, which is the thing a reader doubts — while the derivation is this
    /// file's empty salt, SHA-256 and 32-byte width over the <c>info</c> above. Pinning only the second
    /// would leave a run that disagreed at the first step reporting a KDF problem.
    /// </remarks>
    [Test]
    public async Task EncapsulationKey_OverTheFrozenAgreement_ReproducesTheFrozenKey()
    {
        // Arrange
        using ECDiffieHellman factor = Vectors.FactorKeyPair();
        using ECDiffieHellman ephemeral = Vectors.EphemeralKeyPair();
        byte[] info = Convert.FromHexString(Vectors.Single("hkdfInfo").Hex);

        // Act
        byte[] agreement = ephemeral.DeriveRawSecretAgreement(factor.PublicKey);
        byte[] key = ClientKeyCustody.EncapsulationKey(agreement, info);

        // Assert
        await Assert.That(Hex(agreement)).IsEqualTo(Vectors.Derived("rawSharedSecretHex"));
        await Assert.That(Hex(key)).IsEqualTo(Vectors.Single("encapsulationKey").Hex);
    }

    /// <summary>
    /// The 167-byte stored value, reproduced byte for byte under the frozen nonce.
    /// </summary>
    /// <remarks>
    /// <b>The first of the two pins that a round trip cannot make.</b> Everything between the associated
    /// data and the framing is inside this one value — the AEAD suite's leading byte, the nonce's
    /// position, ciphertext before tag, and the binding to the factor — and every one of them survives a
    /// seal-then-open in an implementation that is wrong about it in both directions.
    /// </remarks>
    [Test]
    public async Task WrapPrivateKey_UnderTheFrozenNonce_ReproducesTheFrozenStoredValue()
    {
        // Arrange
        FrozenVector vector = Vectors.Single("wrappedPrivateKey");
        using ECDiffieHellman factor = Vectors.FactorKeyPair();

        // Act
        byte[] wrapped = ClientKeyCustody.WrapPrivateKeyUnderNonce(
            Convert.FromHexString(Vectors.Input("keyEncryptionKeyHex")),
            factor,
            Vectors.FactorId,
            Convert.FromHexString(Vectors.Input("wrappedPrivateKeyNonceHex")));

        // Assert
        await Assert.That(Hex(wrapped)).IsEqualTo(vector.Hex);

        // The width comes from the frozen file, never from a 167 typed here. A literal beside a hex
        // string that already carries the length is a second number able to disagree with it, in a file
        // whose whole argument is that the answers were authored elsewhere.
        await Assert.That(wrapped.Length).IsEqualTo(vector.LengthBytes);

        // The AEAD suite's byte, cast because IsEqualTo(1) on a byte compiles and then throws.
        await Assert.That(wrapped[0]).IsEqualTo((byte)1);
    }

    /// <summary>
    /// The 158-byte stored value, reproduced byte for byte under the frozen ephemeral pair and nonce.
    /// </summary>
    /// <remarks>
    /// <b>The ephemeral pair is supplied, which is the only way this value is reachable at all.</b> A
    /// mint draws its own, so two encapsulations of one plaintext to one recipient are different bytes
    /// and nothing frozen can be compared against either. What the supplied pair buys is the layout —
    /// the encapsulation suite's leading byte, the point before the nonce, both outside the
    /// authenticated data — and the plaintext's construction, content key first, which is the mistake
    /// that produces a value of exactly the right width carrying exactly the right version that stores,
    /// reads back and opens.
    /// </remarks>
    [Test]
    public async Task EncapsulateAccountKeys_UnderTheFrozenEphemeralPair_ReproducesTheFrozenStoredValue()
    {
        // Arrange
        FrozenVector vector = Vectors.Single("encapsulatedAccountKeys");
        using ECDiffieHellman factor = Vectors.FactorKeyPair();
        using ECDiffieHellman ephemeral = Vectors.EphemeralKeyPair();

        // Act
        byte[] value = ClientKeyCustody.EncapsulateAccountKeysUnder(
            ephemeral,
            Convert.FromHexString(Vectors.Input("encapsulationNonceHex")),
            factor.PublicKey,
            Convert.FromHexString(Vectors.Input("contentKeyHex")),
            Convert.FromHexString(Vectors.Input("indexKeyHex")),
            Vectors.FactorId);

        // Assert
        await Assert.That(Hex(value)).IsEqualTo(vector.Hex);
        await Assert.That(value.Length).IsEqualTo(vector.LengthBytes);
        await Assert.That(value[0]).IsEqualTo((byte)1);
    }

    /// <summary>
    /// The two frozen stored values open, through the real two-step, to the frozen content key and index
    /// key — <b>content key first</b>.
    /// </summary>
    /// <remarks>
    /// <b>The only place in either implementation where the order of the two halves is held.</b> Both are
    /// 32 bytes, so a reversed pair is the right width, the right version, stores, reads back and opens;
    /// the server says so at length and cannot do anything about it. Here the two keys are frozen
    /// separately and the halves are named, so a reader that split them the other way round is two
    /// failing assertions rather than none.
    /// </remarks>
    [Test]
    public async Task TryOpenAccountKeys_OnTheFrozenStoredValues_YieldsTheContentKeyFirst()
    {
        // Arrange
        byte[] wrapped = Convert.FromHexString(Vectors.Single("wrappedPrivateKey").Hex);
        byte[] encapsulated = Convert.FromHexString(Vectors.Single("encapsulatedAccountKeys").Hex);

        // Act
        bool opened = ClientKeyCustody.TryOpenAccountKeys(
            Convert.FromHexString(Vectors.Input("keyEncryptionKeyHex")),
            wrapped,
            encapsulated,
            Vectors.FactorId,
            out byte[] contentKey,
            out byte[] indexKey);

        // Assert
        await Assert.That(opened).IsTrue();
        await Assert.That(Hex(contentKey)).IsEqualTo(Vectors.Input("contentKeyHex"));
        await Assert.That(Hex(indexKey)).IsEqualTo(Vectors.Input("indexKeyHex"));
    }

    /// <summary>
    /// The frozen values do not open under <b>another factor's</b> identifier.
    /// </summary>
    /// <remarks>
    /// The accepting case above passes in an implementation that ignores its associated data entirely,
    /// which is the vacuity every pin in this file would otherwise share. The identifier is the only
    /// thing changed, and the first of the two steps is where it fires: a message built for one factor
    /// authenticates nothing belonging to another.
    /// </remarks>
    [Test]
    public async Task TryOpenAccountKeys_UnderAnotherFactorsIdentifier_Refuses()
    {
        // Arrange
        byte[] wrapped = Convert.FromHexString(Vectors.Single("wrappedPrivateKey").Hex);
        byte[] encapsulated = Convert.FromHexString(Vectors.Single("encapsulatedAccountKeys").Hex);

        // Act
        bool opened = ClientKeyCustody.TryOpenAccountKeys(
            Convert.FromHexString(Vectors.Input("keyEncryptionKeyHex")),
            wrapped,
            encapsulated,
            Vectors.SecondInstanceFactorId,
            out _,
            out _);

        // Assert
        await Assert.That(opened).IsFalse();
    }

    /// <summary>
    /// Three factors <b>supplied out of order</b> produce the frozen 310-byte plaintext.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This triple is the trap, and it is the only place in this repository where the trap can
    /// spring.</b> Two of the three identifiers order one way by their canonical spelling and the other
    /// way under <see cref="Guid.ToByteArray()" />'s mixed-endian layout — so an implementation sorting
    /// on raw <see cref="Guid" /> bytes writes a different plaintext here and an identical one for most
    /// other sets. <see cref="ManifestPlaintext_OrdersByCanonicalSpellingRatherThanGuidBytes" /> beside
    /// it asserts that the two orders really do differ, so this case cannot quietly stop being the trap
    /// if the vector file is ever regenerated.
    /// </para>
    /// <para>
    /// <b>The entries are read back out of the expected bytes</b>, which looks circular and is not: the
    /// vector file names the three identifiers and their order but carries their public keys only inside
    /// the plaintext it froze. What is taken from the expected value is the <em>input</em> — three points
    /// and three identifiers — and what is asserted is the composition over them, which is the count, the
    /// separators and the order. A parser that read the entries wrongly would produce a plaintext that
    /// does not match.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ManifestPlaintext_FromThreeFactorsSuppliedOutOfOrder_ReproducesTheFrozenPlaintext()
    {
        // Arrange
        FrozenVector vector = Vectors.Single(
            "manifestPlaintext",
            candidate => candidate.GetProperty("lengthBytes").GetInt32() == 310);
        IReadOnlyList<ClientKeyCustody.FactorPublicKey> supplied =
            Vectors.EntriesInSuppliedOrder(vector);

        // Act
        byte[] plaintext = ClientKeyCustody.ManifestPlaintext(supplied);

        // Assert
        await Assert.That(Hex(plaintext)).IsEqualTo(vector.Hex);
        await Assert.That(plaintext.Length).IsEqualTo(vector.LengthBytes);
    }

    /// <summary>
    /// The frozen triple orders one way by canonical spelling and another way by raw
    /// <see cref="Guid" /> bytes.
    /// </summary>
    /// <remarks>
    /// <b>A pin on the vector rather than on the implementation, and the case above depends on it.</b> If
    /// somebody regenerated the vectors with three identifiers the two orders agree on, the trap would
    /// stop being a trap and nothing would say so — the plaintext case would still pass, against a set
    /// that no longer separates the two sorts. Compared as one joined string rather than as two
    /// collections, because TUnit's <c>IsEquivalentTo</c> defaults to <c>CollectionOrdering.Any</c> and
    /// order is the entire subject.
    /// </remarks>
    [Test]
    public async Task ManifestPlaintext_OrdersByCanonicalSpellingRatherThanGuidBytes()
    {
        // Arrange
        FrozenVector vector = Vectors.Single(
            "manifestPlaintext",
            candidate => candidate.GetProperty("lengthBytes").GetInt32() == 310);
        IReadOnlyList<ClientKeyCustody.FactorPublicKey> supplied =
            Vectors.EntriesInSuppliedOrder(vector);

        // Act
        string canonical = string.Join(
            ", ",
            supplied
                .Select(factor => factor.FactorId.ToString("D"))
                .Order(StringComparer.Ordinal));
        string byGuidBytes = string.Join(
            ", ",
            supplied
                .OrderBy(
                    factor => factor.FactorId.ToByteArray(),
                    Comparer<byte[]>.Create(
                        (left, right) => left.AsSpan().SequenceCompareTo(right)))
                .Select(factor => factor.FactorId.ToString("D")));

        // Assert
        await Assert.That(canonical).IsEqualTo(
            string.Join(", ", vector.ExpectedOrder));
        await Assert.That(byGuidBytes).IsNotEqualTo(canonical);
    }

    /// <summary>
    /// Eleven factors — a passkey and a card of ten, which is what one registration writes — produce the
    /// frozen 1135-byte plaintext, and it leads with the two characters <c>11</c>.
    /// </summary>
    /// <remarks>
    /// <b>The count is decimal text and a single-digit set cannot show it.</b> A byte, a raw integer and
    /// the character <c>'3'</c> are three compositions that agree on the three-factor vector and part
    /// company here — the plaintext grows by one byte and opens <c>31 31</c>. It is also the width a real
    /// registration produces, so a format that costs more per entry than anybody expected shows up as a
    /// number rather than as a 400 at the end of a ceremony that has already drawn keys.
    /// </remarks>
    [Test]
    public async Task ManifestPlaintext_ForElevenFactors_ReproducesTheFrozenPlaintextAndItsTwoDigitCount()
    {
        // Arrange
        FrozenVector vector = Vectors.Single(
            "manifestPlaintext",
            candidate => candidate.GetProperty("lengthBytes").GetInt32() == 1135);
        IReadOnlyList<ClientKeyCustody.FactorPublicKey> entries = Vectors.Entries(vector);

        // Act — reversed, so a composition that kept the caller's order is red rather than lucky.
        byte[] plaintext = ClientKeyCustody.ManifestPlaintext(entries.Reverse());

        // Assert
        await Assert.That(entries.Count).IsEqualTo(11);
        await Assert.That(Hex(plaintext)).IsEqualTo(vector.Hex);
        await Assert.That(Encoding.UTF8.GetString(plaintext, 0, 2)).IsEqualTo("11");
    }

    /// <summary>
    /// The frozen sealed manifest: the three-factor plaintext under the account's <b>content key</b> at
    /// epoch 1, in AEAD framing.
    /// </summary>
    /// <remarks>
    /// <b>Which of the account's two keys a manifest is sealed under is held here and nowhere else.</b>
    /// The server enforces presence, framing and epoch and can never read a byte of it, so a manifest
    /// sealed under the index key stores, comes back, and fails to open on the day somebody rotates —
    /// the same class of silence as the encapsulated plaintext's half order. The epoch is in the
    /// associated data, so this value is also the pin that says the manifest's message is actually
    /// reached by the sealing path rather than merely exported from it.
    /// </remarks>
    [Test]
    public async Task SealManifest_UnderTheFrozenNonce_ReproducesTheFrozenSealedManifest()
    {
        // Arrange
        FrozenVector vector = Vectors.Single("manifestSealed");
        IReadOnlyList<ClientKeyCustody.FactorPublicKey> supplied = Vectors.EntriesInSuppliedOrder(
            Vectors.Single(
                "manifestPlaintext",
                candidate => candidate.GetProperty("lengthBytes").GetInt32() == 310));

        // Act
        byte[] sealedManifest = ClientKeyCustody.SealManifestUnderNonce(
            Convert.FromHexString(Vectors.Input("contentKeyHex")),
            supplied,
            vector.RotationEpoch,
            Convert.FromHexString(Vectors.Input("manifestNonceHex")));

        // Assert
        await Assert.That(Hex(sealedManifest)).IsEqualTo(vector.Hex);
        await Assert.That(sealedManifest.Length).IsEqualTo(vector.LengthBytes);
    }

    /// <summary>
    /// The frozen sealed manifest opens at the epoch it was sealed at, and at no other.
    /// </summary>
    /// <remarks>
    /// The refusal is the case; the acceptance beside it is what stops a reader that always returns
    /// <see langword="false" /> passing. A manifest replayed from before a rotation is a set that has
    /// moved, and the epoch in the associated data is the only thing that turns that into a tag failure
    /// rather than a current answer.
    /// </remarks>
    [Test]
    public async Task TryOpenManifest_AtAnotherEpoch_Refuses()
    {
        // Arrange
        FrozenVector vector = Vectors.Single("manifestSealed");
        byte[] sealedManifest = Convert.FromHexString(vector.Hex);
        byte[] contentKey = Convert.FromHexString(Vectors.Input("contentKeyHex"));

        // Act
        bool atItsOwnEpoch = ClientKeyCustody.TryOpenManifest(
            contentKey, sealedManifest, vector.RotationEpoch, out byte[] plaintext);
        bool atTheNext = ClientKeyCustody.TryOpenManifest(
            contentKey, sealedManifest, vector.RotationEpoch + 1, out _);

        // Assert
        await Assert.That(atItsOwnEpoch).IsTrue();
        await Assert.That(Hex(plaintext)).IsEqualTo(
            Vectors
                .Single(
                    "manifestPlaintext",
                    candidate => candidate.GetProperty("lengthBytes").GetInt32() == 310)
                .Hex);
        await Assert.That(atTheNext).IsFalse();
    }

    /// <summary>
    /// <c>adversarial.secondInstance</c>, end to end: a different key-encryption key, a different factor
    /// identifier, a different key pair and a different pair of account keys.
    /// </summary>
    /// <remarks>
    /// <b>Every other vector in the file shares one instance, so an implementation that ignored its
    /// arguments and returned the frozen values would pass all of them.</b> It cannot pass both
    /// instances. Both directions are exercised over the second instance for that reason: the stored
    /// values open to <em>its</em> account keys, and the wrapped private key is rebuilt from the
    /// plaintext the opening produced and compared against the frozen bytes — so a lookup table would
    /// have to carry two entries and a real derivation carries none.
    /// </remarks>
    [Test]
    public async Task SecondInstance_OpensToItsOwnKeysAndRebuildsItsOwnWrappedPrivateKey()
    {
        // Arrange
        JsonElement instance = Vectors.SecondInstance;
        byte[] keyEncryptionKey =
            Convert.FromHexString(Vectors.Text(instance, "keyEncryptionKeyHex"));
        byte[] wrapped = Convert.FromHexString(Vectors.Text(instance, "wrappedPrivateKeyHex"));
        byte[] encapsulated =
            Convert.FromHexString(Vectors.Text(instance, "encapsulatedAccountKeysHex"));

        // Act
        bool opened = ClientKeyCustody.TryOpenAccountKeys(
            keyEncryptionKey,
            wrapped,
            encapsulated,
            Vectors.SecondInstanceFactorId,
            out byte[] contentKey,
            out byte[] indexKey);

        using ECDiffieHellman factor = ClientKeyCustody.CreateFactorKeyPair();
        factor.ImportPkcs8PrivateKey(
            Convert.FromHexString(Vectors.Text(instance, "factorPkcs8Hex")), out _);

        // The nonce is lifted out of the frozen value itself — a wrapped private key carries it in the
        // clear, immediately after the version byte — so the rebuild needs nothing the file does not
        // already hold. The bounds are named rather than written as 1..13: a bare slice in a file whose
        // whole argument is that its numbers came from somewhere else reads as two magic constants, and
        // a reader has to count the framing out of the AEAD layout to see that it is the nonce.
        byte[] nonce = wrapped[EnvelopeVersionBytes..(EnvelopeVersionBytes + EnvelopeNonceBytes)];

        byte[] rebuilt = ClientKeyCustody.WrapPrivateKeyUnderNonce(
            keyEncryptionKey, factor, Vectors.SecondInstanceFactorId, nonce);

        // Assert
        await Assert.That(opened).IsTrue();
        await Assert.That(Hex(contentKey))
            .IsEqualTo(Vectors.Text(instance, "contentKeyHex"));
        await Assert.That(Hex(indexKey)).IsEqualTo(Vectors.Text(instance, "indexKeyHex"));
        await Assert.That(Hex(ClientKeyCustody.UncompressedPoint(factor.PublicKey)))
            .IsEqualTo(Vectors.Text(instance, "factorPublicKeyHex"));
        await Assert.That(Hex(rebuilt)).IsEqualTo(Vectors.Text(instance, "wrappedPrivateKeyHex"));
    }

    /// <summary>
    /// The associated data of every frozen narrative vector, rebuilt from its binding.
    /// </summary>
    /// <remarks>
    /// <b>The case that tells the binding from the cipher.</b> Every sealed vector fails with one
    /// indistinguishable tag error, so a label with one byte wrong, two fields in the other order or a
    /// separator of another value all look like a bad key from there. Green here and red below means the
    /// cipher; red here means the grammar. All three vectors are driven, including the two sealed ones,
    /// because they cover two bindings: <c>transactions</c>/<c>description</c> for two, and
    /// <c>payees</c>/<c>name</c> for mixed-width alone. Reasoned, not measured against this file: a
    /// builder that ignored its table and column and wrote <c>transactions</c> and <c>description</c>
    /// reddens only mixed-width; one that wrote <c>payees</c> and <c>name</c> reddens only the other two.
    /// </remarks>
    [Test]
    [Arguments("associated-data-only")]
    [Arguments("ascii")]
    [Arguments("mixed-width")]
    public async Task NarrativeFieldAssociatedData_ReproducesTheFrozenMessage(string name)
    {
        // Arrange
        NarrativeVector vector = NarrativeVectors.Single(name);

        // Act
        byte[] message = ClientKeyCustody.NarrativeFieldAssociatedData(
            vector.Table, vector.Column, vector.RowId);

        // Assert
        await Assert.That(Hex(message)).IsEqualTo(vector.AadHex);
        await Assert.That(message.Length).IsEqualTo(vector.AadLength);
    }

    /// <summary>
    /// Each frozen narrative envelope, reproduced byte for byte under its frozen nonce, and its wire
    /// spelling with it.
    /// </summary>
    /// <remarks>
    /// <b>The write direction, which is the one a round trip cannot see.</b> The plaintext enters as the
    /// frozen UTF-8 bytes rather than as the readable caption — the vector file says why at length: a
    /// JSON string cannot tell NFC from NFD, and the hex is the contract. The associated data is rebuilt
    /// through the builder rather than read off <c>aadHex</c>, so this case also says the builder is the
    /// one sealing actually reaches.
    /// </remarks>
    [Test]
    [Arguments("ascii")]
    [Arguments("mixed-width")]
    public async Task SealUnderNonce_OverTheFrozenNarrativeBinding_ReproducesTheFrozenEnvelopeAndWire(
        string name)
    {
        // Arrange
        NarrativeVector vector = NarrativeVectors.Single(name);

        // Act
        byte[] envelope = ClientKeyCustody.SealUnderNonce(
            NarrativeVectors.ContentKey,
            Convert.FromHexString(vector.NonceHex),
            Convert.FromHexString(vector.PlaintextUtf8Hex),
            ClientKeyCustody.NarrativeFieldAssociatedData(vector.Table, vector.Column, vector.RowId));

        // Assert
        await Assert.That(Hex(envelope)).IsEqualTo(vector.EnvelopeHex);
        await Assert.That(Base64UrlText.Encode(envelope)).IsEqualTo(vector.Wire);
    }

    /// <summary>
    /// Each frozen wire value opens, under the frozen content key and its own binding, to the frozen
    /// UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// Through <see cref="AccountKeyFixture.TryOpenNarrative" />, which is the opener the rest of the
    /// suite reaches for, so the fixture's decode, open and UTF-8 steps are held by the same frozen
    /// bytes. Compared as the plaintext's UTF-8 hex, never as the caption.
    /// </remarks>
    [Test]
    [Arguments("ascii")]
    [Arguments("mixed-width")]
    public async Task TryOpenNarrative_OnTheFrozenWire_YieldsTheFrozenPlaintext(string name)
    {
        // Arrange
        NarrativeVector vector = NarrativeVectors.Single(name);

        // Act
        bool opened = AccountKeyFixture.TryOpenNarrative(
            NarrativeVectors.ContentKey,
            vector.Table,
            vector.Column,
            vector.RowId,
            vector.Wire,
            out string text);

        // Assert
        await Assert.That(opened).IsTrue();
        await Assert.That(Hex(Encoding.UTF8.GetBytes(text))).IsEqualTo(vector.PlaintextUtf8Hex);
    }

    /// <summary>
    /// A frozen wire value does not open under the other vector's binding.
    /// </summary>
    /// <remarks>
    /// The accepting case above passes in an opener that ignores its associated data entirely; this is
    /// what stops it. Only the binding moves — the key, the envelope and the path are the accepting
    /// case's.
    /// </remarks>
    [Test]
    public async Task TryOpenNarrative_UnderAnotherRowsBinding_Refuses()
    {
        // Arrange
        NarrativeVector sealedHere = NarrativeVectors.Single("ascii");
        NarrativeVector elsewhere = NarrativeVectors.Single("mixed-width");

        // Act
        bool opened = AccountKeyFixture.TryOpenNarrative(
            NarrativeVectors.ContentKey,
            elsewhere.Table,
            elsewhere.Column,
            elsewhere.RowId,
            sealedHere.Wire,
            out _);

        // Assert
        await Assert.That(opened).IsFalse();
    }

    /// <summary>
    /// The locator throws rather than returning nothing when no ancestor holds <c>docs/</c>.
    /// </summary>
    /// <remarks>
    /// <b>"The test ran from a published output with no source tree" must never read as a pass</b>, which
    /// is the rule <c>EnvelopeSuiteCensusTests</c> keeps for the same reason and the reason this file
    /// does not skip when the vectors are missing. Every case above would go green, having asserted
    /// nothing, and the whole point of the factor-keypair file, whose path this case resolves, is that
    /// somebody else authored the bytes.
    /// </remarks>
    [Test]
    public async Task Vectors_WithNoSourceTreeAboveThem_Throw()
    {
        // Arrange
        string nowhere = Path.Combine(Path.GetTempPath(), Guid.CreateVersion7().ToString("D"));
        Directory.CreateDirectory(nowhere);

        try
        {
            // Act
            Action locate = () => Vectors.LocateFrom(nowhere);

            // Assert
            await Assert.That(locate).Throws<InvalidOperationException>();
        }
        finally
        {
            Directory.Delete(nowhere);
        }
    }

    /// <summary>
    /// The width of the AEAD framing's leading version byte, named so a slice into a stored value does
    /// not read as an offset somebody chose.
    /// </summary>
    /// <remarks>
    /// <b>Transcribed rather than read off <c>CiphertextEnvelope</c>, and that is deliberate.</b> This
    /// file is a reproduction of the <em>client's</em> half, pinned against frozen answers rather than
    /// against the server; a number taken from the server's constant would make the pin agree with
    /// whatever the server later says, which is the drift the whole file exists to refuse. It is used to
    /// locate a value inside frozen bytes, never to assert one.
    /// </remarks>
    private const int EnvelopeVersionBytes = 1;

    /// <summary>The width of the nonce the AEAD framing carries in the clear, after the version byte.</summary>
    /// <remarks><see cref="EnvelopeVersionBytes" />'s, for the same reason.</remarks>
    private const int EnvelopeNonceBytes = 12;

    /// <summary>Lower-case hex, which is the spelling the vector file uses.</summary>
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>One entry of the frozen file's <c>vectors</c> array, in the fields a case reads.</summary>
    /// <remarks>
    /// Every text field goes through <see cref="Vectors.Text" /> rather than a null-forgiving
    /// <c>GetString()!</c>. The two differ only when the vector file has been edited out from under this
    /// suite, and that is precisely when the difference is worth having: a named property beats a
    /// <see cref="NullReferenceException" /> raised somewhere inside a hex conversion.
    /// </remarks>
    private sealed record FrozenVector(JsonElement Element)
    {
        public string Hex => Vectors.Text(Element, "hex");

        public int LengthBytes => Element.GetProperty("lengthBytes").GetInt32();

        public int RotationEpoch => Element.GetProperty("rotationEpoch").GetInt32();

        public IReadOnlyList<string> ExpectedOrder =>
            [.. Element.GetProperty("expectedOrder").EnumerateArray().Select(Vectors.Text)];

        public IReadOnlyList<Guid> SuppliedOrder =>
            [.. Element.GetProperty("suppliedOrder").EnumerateArray().Select(id => Guid.Parse(Vectors.Text(id)))];
    }

    /// <summary>
    /// The frozen file, read off disk, and the values a case needs out of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It sits one level above the solution, and walking to the solution is what a reader will
    /// write.</b> <c>BudgetoidApp.sln</c> lives in <c>BudgetoidApp/</c>; <c>docs/</c> is its sibling's
    /// parent, so a walker anchored on the solution file stops one directory short and finds nothing. The
    /// anchor here is the vector file's own path.
    /// </para>
    /// <para>
    /// <b>It throws rather than skipping</b>, for the reason
    /// <see cref="Vectors_WithNoSourceTreeAboveThem_Throw" /> states: a run with no source tree must be
    /// red, not green and silent.
    /// </para>
    /// <para>
    /// Parsed once into a <see cref="JsonDocument" /> held for the life of the process. Nothing mutates
    /// it and every case reads the same bytes, so a copy per case would buy isolation from nothing.
    /// </para>
    /// </remarks>
    private static class Vectors
    {
        private const string RelativePath = "docs/business-logic/vectors/factor-keypair-v1.json";

        private static readonly JsonDocument Document =
            JsonDocument.Parse(File.ReadAllText(LocateFrom(AppContext.BaseDirectory)));

        /// <summary>The identifier every vector but the second instance is built for.</summary>
        public static Guid FactorId => Guid.Parse(Input("factorId"));

        /// <summary>The wholly independent instance's identifier.</summary>
        public static Guid SecondInstanceFactorId =>
            Guid.Parse(Text(SecondInstance, "factorId"));

        public static JsonElement SecondInstance =>
            Document.RootElement.GetProperty("adversarial").GetProperty("secondInstance");

        /// <summary>
        /// The absolute path of the frozen file, walking up from <paramref name="startDirectory" />.
        /// </summary>
        /// <exception cref="InvalidOperationException">No ancestor holds it.</exception>
        public static string LocateFrom(string startDirectory) =>
            LocateFrom(startDirectory, RelativePath);

        /// <summary>
        /// The absolute path of the frozen file at <paramref name="relativePath" />, walking up from
        /// <paramref name="startDirectory" />.
        /// </summary>
        /// <remarks>
        /// The one walker for both vector files, so the narrative file inherits the same refusal the
        /// case beside it pins rather than a second walker with an opinion of its own.
        /// </remarks>
        /// <exception cref="InvalidOperationException">No ancestor holds it.</exception>
        public static string LocateFrom(string startDirectory, string relativePath)
        {
            string relative = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (DirectoryInfo? directory = new(startDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, relative);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                $"No ancestor of '{startDirectory}' holds {relativePath}. A run that cannot read the "
                + "frozen vectors has pinned nothing, and it must not be mistaken for a run that agreed "
                + "with them.");
        }

        public static string Input(string name) =>
            Text(Document.RootElement.GetProperty("inputs"), name);

        public static string Derived(string name) =>
            Text(Document.RootElement.GetProperty("derived"), name);

        /// <summary>
        /// The string at <paramref name="property" /> of <paramref name="owner" />, or a throw naming it.
        /// </summary>
        /// <remarks>
        /// <b>In place of <c>GetString()!</c>, which is the spelling this file would otherwise carry a
        /// dozen times.</b> The null-forgiving operator asserts something about a file on disk that this
        /// assembly does not own and cannot see at compile time — and it pays off as a
        /// <see cref="NullReferenceException" /> from inside a hex conversion two frames away. The whole
        /// argument for reading these vectors rather than restating them is that each value then has one
        /// copy, and for the factor-keypair file that copy is a third party's; the matching refusal is
        /// one that says which value went missing.
        /// </remarks>
        public static string Text(JsonElement owner, string property) =>
            owner.TryGetProperty(property, out JsonElement value) && value.GetString() is { } text
                ? text
                : throw new InvalidOperationException(
                    $"The frozen vector file holds no string at '{property}'. A case that cannot read "
                    + "the value it pins has pinned nothing.");

        /// <summary>The string <paramref name="value" /> is, or a throw.</summary>
        /// <remarks>The same refusal for an element inside an array, which has no property to name.</remarks>
        public static string Text(JsonElement value) =>
            value.GetString()
            ?? throw new InvalidOperationException(
                "The frozen vector file holds a non-string where a case expected one.");

        /// <summary>
        /// The one vector named <paramref name="name" /> that satisfies <paramref name="where" />.
        /// </summary>
        /// <remarks>
        /// <b>Fails closed on none and on more than one.</b> Four names in the file are carried by two
        /// vectors each, so a selector that took the first match would silently pin the wrong epoch, the
        /// wrong version or the wrong factor count — and a case reading a vector that no longer exists
        /// must be red rather than reading a neighbour's bytes.
        /// </remarks>
        public static FrozenVector Single(string name, Func<JsonElement, bool>? where = null)
        {
            JsonElement[] matches =
            [
                .. Document.RootElement.GetProperty("vectors").EnumerateArray()
                    .Where(vector => vector.GetProperty("name").GetString() == name)
                    .Where(vector => where is null || where(vector)),
            ];

            return matches.Length == 1
                ? new FrozenVector(matches[0])
                : throw new InvalidOperationException(
                    $"{matches.Length} frozen vectors answer to '{name}' under this filter; exactly one "
                    + "must.");
        }

        /// <summary>The frozen scalar and point of the shared instance's factor, as a key pair.</summary>
        public static ECDiffieHellman FactorKeyPair() =>
            KeyPair(Input("factorPrivateScalarHex"), Derived("factorPublicKeyHex"));

        /// <summary>The frozen ephemeral pair — supplied, because a mint would draw its own.</summary>
        public static ECDiffieHellman EphemeralKeyPair() =>
            KeyPair(Input("ephemeralPrivateScalarHex"), Derived("ephemeralPublicKeyHex"));

        /// <summary>
        /// The entries a frozen manifest plaintext names, in the order the plaintext writes them.
        /// </summary>
        /// <remarks>
        /// <b>Positional, because the separator is not a delimiter here.</b> <c>0x1F</c> occurs inside a
        /// 65-byte point often enough that splitting on it would part an entry in the middle; the widths
        /// are what make the plaintext unambiguous. So the count is read up to the first separator and
        /// every entry after it is 36 bytes of identifier, a separator, 65 bytes of point, and a
        /// separator before the next.
        /// </remarks>
        public static IReadOnlyList<ClientKeyCustody.FactorPublicKey> Entries(FrozenVector vector)
        {
            byte[] plaintext = Convert.FromHexString(vector.Hex);
            int separator = Array.IndexOf(plaintext, (byte)0x1F);
            int count = int.Parse(
                Encoding.UTF8.GetString(plaintext, 0, separator),
                CultureInfo.InvariantCulture);

            List<ClientKeyCustody.FactorPublicKey> entries = new(count);
            int at = separator + 1;

            for (int index = 0; index < count; index++)
            {
                // One separator between entries and none at either end, which is the same rule the
                // plaintext is composed under — read here rather than assumed, so a plaintext framed some
                // other way parses into something that does not compose back.
                at += index > 0 ? 1 : 0;

                Guid factorId = Guid.Parse(Encoding.UTF8.GetString(plaintext, at, 36));
                at += 36 + 1;

                entries.Add(new ClientKeyCustody.FactorPublicKey(
                    factorId, plaintext[at..(at + ClientKeyCustody.PublicKeyBytes)]));
                at += ClientKeyCustody.PublicKeyBytes;
            }

            return entries;
        }

        /// <summary>
        /// The same entries, re-ordered into the <c>suppliedOrder</c> the vector names.
        /// </summary>
        /// <remarks>
        /// The supplied order is the input a caller would really hand over — a set assembled in whatever
        /// order somebody built it — and the expected bytes are what the composition owes regardless.
        /// </remarks>
        public static IReadOnlyList<ClientKeyCustody.FactorPublicKey> EntriesInSuppliedOrder(
            FrozenVector vector)
        {
            IReadOnlyList<ClientKeyCustody.FactorPublicKey> entries = Entries(vector);

            return
            [
                .. vector.SuppliedOrder.Select(
                    id => entries.Single(entry => entry.FactorId == id)),
            ];
        }

        private static ECDiffieHellman KeyPair(string scalarHex, string pointHex)
        {
            byte[] point = Convert.FromHexString(pointHex);

            return ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = Convert.FromHexString(scalarHex),
                Q = new ECPoint
                {
                    X = point[1..33],
                    Y = point[33..65],
                },
            });
        }
    }

    /// <summary>One entry of the narrative file's <c>vectors</c> array, in the fields a case reads.</summary>
    /// <remarks>
    /// The sealed fields are read lazily: the binding-only vector carries none of them, and reads them
    /// never.
    /// </remarks>
    private sealed record NarrativeVector(JsonElement Element)
    {
        private JsonElement Binding => Element.GetProperty("binding");

        public string Table => Vectors.Text(Binding, "table");

        public string Column => Vectors.Text(Binding, "column");

        public Guid RowId => Guid.Parse(Vectors.Text(Binding, "rowId"));

        public string AadHex => Vectors.Text(Element, "aadHex");

        public int AadLength => Element.GetProperty("aadLength").GetInt32();

        public string PlaintextUtf8Hex => Vectors.Text(Element, "plaintextUtf8Hex");

        public string NonceHex => Vectors.Text(Element, "nonceHex");

        public string EnvelopeHex => Vectors.Text(Element, "envelopeHex");

        public string Wire => Vectors.Text(Element, "wire");
    }

    /// <summary>
    /// <c>docs/business-logic/vectors/narrative-field-v1.json</c>, read off disk.
    /// </summary>
    /// <remarks>
    /// Located by <see cref="Vectors.LocateFrom(string, string)" />, so a run with no source tree is red
    /// here for the reason <see cref="Vectors_WithNoSourceTreeAboveThem_Throw" /> states, and parsed once
    /// for the same reason <see cref="Vectors" /> is.
    /// </remarks>
    private static class NarrativeVectors
    {
        private const string RelativePath = "docs/business-logic/vectors/narrative-field-v1.json";

        private static readonly JsonDocument Document = JsonDocument.Parse(
            File.ReadAllText(Vectors.LocateFrom(AppContext.BaseDirectory, RelativePath)));

        /// <summary>The one content key every sealed vector in the file is under.</summary>
        public static byte[] ContentKey =>
            Convert.FromHexString(Vectors.Text(Document.RootElement, "contentKeyHex"));

        /// <summary>The one vector named <paramref name="name" />, failing closed on none and on two.</summary>
        public static NarrativeVector Single(string name)
        {
            JsonElement[] matches =
            [
                .. Document.RootElement.GetProperty("vectors").EnumerateArray()
                    .Where(vector => vector.GetProperty("name").GetString() == name),
            ];

            return matches.Length == 1
                ? new NarrativeVector(matches[0])
                : throw new InvalidOperationException(
                    $"{matches.Length} frozen narrative vectors answer to '{name}'; exactly one must.");
        }
    }
}
