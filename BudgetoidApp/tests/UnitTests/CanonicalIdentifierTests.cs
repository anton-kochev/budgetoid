using Application.Security;

namespace UnitTests;

/// <summary>
/// The one parse-and-validate step for a client-minted identifier that crosses the wire as text: which
/// of the many texts denoting one <see cref="Guid" /> this API accepts, and what a refusal hands back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Direct rather than through a caller.</b> Until now the type's whole safety net was indirect —
/// <c>RecoveryCodeSetValidationTests</c> reaches it through one of its four calling paths, and only that
/// one path drives a non-canonical spelling at all. That was tolerable while the type had a single
/// subject. It is acquiring a second — narrative row identifiers — and on that path a wrong answer is
/// silent, permanent and has no migration: the value is the associated data an AEAD envelope was sealed
/// with, so a spelling accepted here and rendered differently on the way back out makes a person's text
/// unopenable with nothing anywhere naming the cause. A rule that is only reachable through one caller
/// is a rule the other three callers cannot be shown to keep.
/// </para>
/// <para>
/// <b>Three rules, not one, and each has its own cases.</b> The method refuses for three separate
/// reasons and they are worth separating because a mutation that removes one leaves the other two
/// answering <see langword="false" /> on most inputs anyway:
/// </para>
/// <list type="number">
/// <item>
/// <b>The parse.</b> <c>Guid.TryParseExact(value, "D", …)</c> settles absence, emptiness, garbage, and
/// the four sibling formats — <c>N</c>, <c>B</c>, <c>P</c> and <c>X</c>.
/// </item>
/// <item>
/// <b>The round trip.</b> Comparing the text ordinally against <c>parsed.ToString("D")</c> is what
/// settles case folding and surrounding whitespace, neither of which the parse refuses: <c>"D"</c> is a
/// format and not a spelling, so <c>TryParseExact</c> admits upper- and mixed-case hex, and it trims
/// before it ever looks at the format. That trim is the reason the round trip exists rather than being
/// a restatement of the line above it.
/// </item>
/// <item>
/// <b>The all-zero uuid</b>, refused for a reason of its own and not as a spelling. It is canonically
/// spelled — it round-trips perfectly — so nothing in rules 1 or 2 touches it. It is what an unset
/// field sends and the one value two accounts reach independently.
/// </item>
/// </list>
/// <para>
/// <b>Which control covers which claim.</b>
/// <see cref="TryParse_WithTheCanonicalSpelling_AssignsTheParsedIdentifier" /> is the provable-fail
/// control for every refusal below: without it, a method that returned <see langword="false" />
/// unconditionally would pass all of them. It asserts the <c>out</c> parameter and not only the boolean,
/// because a <c>TryParse</c> that answered <see langword="true" /> and left <c>id</c> at
/// <see cref="Guid.Empty" /> is green against a suite that reads the result alone, and its caller would
/// then write the all-zero uuid into the column the method exists to protect.
/// </para>
/// <para>
/// <b>Every refusal asserts the <c>out</c> parameter too</b>, rather than one shared test doing it once.
/// A refusal that left a partially-parsed value behind is a caller reading a value it was told not to
/// have, and where that leak appears depends on <em>which</em> rule refused: an assignment moved above
/// the round trip leaks on the case-folding and whitespace cases only, one moved above the empty check
/// leaks on nothing at all, since the value there is <see cref="Guid.Empty" /> already. Stating it on
/// every case is what makes the position of that assignment observable rather than assumed.
/// </para>
/// <para>
/// <b>The sibling-format cases hold a claim from the other direction.</b> They are green whether the
/// parse is <c>TryParseExact(value, "D", …)</c> or the lenient <c>Guid.TryParse(value, …)</c>, because
/// the round trip refuses <c>N</c>, <c>B</c>, <c>P</c> and <c>X</c> on its own. That substitution is an
/// equivalent mutant rather than a hole, and these cases are what shows it: they pin the behaviour, and
/// the rule they pin has two owners, one of which is redundant today.
/// </para>
/// </remarks>
public sealed class CanonicalIdentifierTests
{
    /// <summary>
    /// The spelling the API accepts: lower-case hex, hyphenated, unbraced, unspaced.
    /// </summary>
    /// <remarks>
    /// Chosen with hex letters in every group, so that a case-folding fault has somewhere to show. A
    /// uuid of digits alone renders identically in both cases and would make the upper-case case below
    /// indistinguishable from the accepting one.
    /// </remarks>
    private const string Canonical = "3f2a9c81-7d4e-4b6a-9e15-c0ab8d7f2e34";

    /// <summary>The same identifier with every hex letter upper-cased.</summary>
    private const string UpperCase = "3F2A9C81-7D4E-4B6A-9E15-C0AB8D7F2E34";

    /// <summary>The same identifier with two of its five groups upper-cased and three left alone.</summary>
    /// <remarks>
    /// Separate from <see cref="UpperCase" /> because a rule written as "reject a value that equals its
    /// own upper-casing" passes the all-upper case and admits this one.
    /// </remarks>
    private const string MixedCase = "3f2a9c81-7D4E-4b6a-9e15-c0ab8d7f2e34";

    /// <summary>The canonical spelling with no hyphens — <c>Guid</c>'s <c>N</c> format.</summary>
    private const string NoHyphens = "3f2a9c817d4e4b6a9e15c0ab8d7f2e34";

    /// <summary>The canonical spelling in braces — <c>Guid</c>'s <c>B</c> format.</summary>
    private const string Braced = "{3f2a9c81-7d4e-4b6a-9e15-c0ab8d7f2e34}";

    /// <summary>The canonical spelling in parentheses — <c>Guid</c>'s <c>P</c> format.</summary>
    private const string Parenthesised = "(3f2a9c81-7d4e-4b6a-9e15-c0ab8d7f2e34)";

    /// <summary>The same identifier as a hex object — <c>Guid</c>'s <c>X</c> format.</summary>
    private const string HexObject =
        "{0x3f2a9c81,0x7d4e,0x4b6a,{0x9e,0x15,0xc0,0xab,0x8d,0x7f,0x2e,0x34}}";

    /// <summary>
    /// The smallest identifier the method accepts: the all-zero uuid with its final nibble set.
    /// </summary>
    /// <remarks>
    /// Accepted alongside <see cref="Canonical" /> so the all-zero rule is pinned as the equality it is
    /// and not as something looser in the same neighbourhood. Measured: a check written
    /// <c>value.StartsWith("00000000")</c> — the shape someone reaches for when they remember the rule
    /// as "not the empty one" — refuses this and is green against every other case in the file.
    /// </remarks>
    private const string SmallestNonEmpty = "00000000-0000-0000-0000-000000000001";

    /// <summary>The largest identifier, every nibble set, and so entirely hex letters.</summary>
    /// <remarks>
    /// The third accepted value, and the reason there are three: one accepted value can be satisfied by
    /// a method that hard-codes it, which is green against every refusal here because refusing is what
    /// it does with everything else. Three unrelated values, one of them all digits in its leading
    /// group and one of them all letters, cannot be met that way.
    /// </remarks>
    private const string AllHexLetters = "ffffffff-ffff-ffff-ffff-ffffffffffff";

    /// <summary>
    /// The all-zero uuid, written canonically.
    /// </summary>
    /// <remarks>
    /// The one refused value that satisfies both rules above it: it parses, and it renders back exactly
    /// as it was sent. Nothing but the dedicated check refuses it.
    /// </remarks>
    private const string AllZero = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// The canonical spelling is accepted and the parsed identifier is handed back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>out</c> parameter is asserted against the value the text denotes, not merely against
    /// "not <see cref="Guid.Empty" />". A method that assigned some other well-formed uuid would satisfy
    /// the weaker form and would still be sealing every later envelope under associated data the client
    /// cannot rebuild.
    /// </para>
    /// <para>
    /// <b>Three values rather than one</b>, for two measured reasons rather than for coverage. One
    /// accepted value is met by a method that hard-codes that value and refuses everything else, which
    /// passes every refusal in this file because refusal is all it does. And
    /// <see cref="SmallestNonEmpty" /> is what tells the all-zero rule apart from an over-broad
    /// neighbour: both mutations survive a version of this case that drives
    /// <see cref="Canonical" /> alone, and both redden against this one.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(Canonical)]
    [Arguments(SmallestNonEmpty)]
    [Arguments(AllHexLetters)]
    public async Task TryParse_WithTheCanonicalSpelling_AssignsTheParsedIdentifier(string value)
    {
        // Arrange
        Guid expected = Guid.Parse(value);

        // Act
        bool accepted = CanonicalIdentifier.TryParse(value, out Guid id);

        // Assert
        await Assert.That(accepted).IsTrue();
        await Assert.That(id).IsEqualTo(expected);
        await Assert.That(id).IsNotEqualTo(Guid.Empty);
    }

    /// <summary>
    /// Hex in any case but lower is refused, and nothing is handed back.
    /// </summary>
    /// <remarks>
    /// The parse admits both of these — <c>"D"</c> is case-insensitive — so this case is driven entirely
    /// by the round trip. It is also the case where a misplaced assignment leaks: the value is
    /// well-formed and non-empty, so an <c>id = parsed</c> written above the comparison hands the caller
    /// a usable identifier alongside <see langword="false" />.
    /// </remarks>
    [Test]
    [Arguments(UpperCase)]
    [Arguments(MixedCase)]
    public async Task TryParse_WithHexInAnyCaseButLower_Refuses(string value)
    {
        // Act
        bool accepted = CanonicalIdentifier.TryParse(value, out Guid id);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(id).IsEqualTo(Guid.Empty);
    }

    /// <summary>
    /// A canonically spelled identifier padded with whitespace is refused, on either side and on both.
    /// </summary>
    /// <remarks>
    /// This is the case the round trip exists for. <c>Guid.TryParseExact</c> trims leading and trailing
    /// whitespace <em>before</em> it reads the format, so the parse above the comparison accepts all
    /// three of these and reports the same <see cref="Guid" /> the untrimmed text would have produced.
    /// Both sides are driven separately, and then together, because an implementation that trimmed one
    /// end — or compared against a value it had trimmed itself — is green on one of the three.
    /// </remarks>
    [Test]
    [Arguments(" " + Canonical)]
    [Arguments(Canonical + " ")]
    [Arguments(" " + Canonical + " ")]
    public async Task TryParse_WithSurroundingWhitespace_Refuses(string value)
    {
        // Act
        bool accepted = CanonicalIdentifier.TryParse(value, out Guid id);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(id).IsEqualTo(Guid.Empty);
    }

    /// <summary>
    /// The four sibling uuid formats are refused, however legible each of them is.
    /// </summary>
    /// <remarks>
    /// Each denotes the same identifier as <see cref="Canonical" /> and each is a distinct string on the
    /// wire, which is the whole of the objection: the row hands back one rendering, so a client that
    /// sealed under any of these four cannot rebuild what it sealed with. These are the cases that stay
    /// green under both spellings of the parse — see the note on the class — because the round trip
    /// refuses all four without help.
    /// </remarks>
    [Test]
    [Arguments(NoHyphens)]
    [Arguments(Braced)]
    [Arguments(Parenthesised)]
    [Arguments(HexObject)]
    public async Task TryParse_WithAUuidFormatOtherThanD_Refuses(string value)
    {
        // Act
        bool accepted = CanonicalIdentifier.TryParse(value, out Guid id);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(id).IsEqualTo(Guid.Empty);
    }

    /// <summary>
    /// The all-zero uuid is refused even though it is canonically spelled.
    /// </summary>
    /// <remarks>
    /// A separate rule from the spelling one and it has to be driven separately: this value parses and
    /// round-trips, so deleting either of the two checks above leaves it refused all the same, and
    /// deleting this one leaves every other case here green. It is what an unset client field sends, and
    /// it is the one identifier two accounts reach independently — on a key unique across the whole
    /// table, that turns a client bug into a cross-account collision.
    /// </remarks>
    [Test]
    public async Task TryParse_WithTheAllZeroUuid_Refuses()
    {
        // Act
        bool accepted = CanonicalIdentifier.TryParse(AllZero, out Guid id);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(id).IsEqualTo(Guid.Empty);
    }

    /// <summary>
    /// An absent, empty or unparseable value is refused rather than treated as an absent identifier.
    /// </summary>
    /// <remarks>
    /// <see langword="null" /> is the shape a missing JSON property arrives in — the value a client that
    /// has not implemented the member yet sends — and it is worth its own case because it is the one
    /// input that could reach the comparison as a null reference rather than as text.
    /// </remarks>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not-a-uuid")]
    public async Task TryParse_WithNothingParseable_Refuses(string? value)
    {
        // Act
        bool accepted = CanonicalIdentifier.TryParse(value, out Guid id);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(id).IsEqualTo(Guid.Empty);
    }
}
