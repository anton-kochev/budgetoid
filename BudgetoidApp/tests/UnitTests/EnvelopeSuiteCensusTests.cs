using System.Reflection;
using System.Text.RegularExpressions;
using Domain.Security;

namespace UnitTests;

/// <summary>
/// One AEAD envelope suite the Domain assembly declares, and the version byte it defines itself at.
/// </summary>
/// <remarks>
/// A suite is discovered, never written down: it is a public static class in the
/// <c>Domain.Security</c> namespace declaring a <c>public const byte Version</c>. That is the shape
/// <see cref="CiphertextEnvelope"/> has and the shape <see cref="EncapsulatedValueEnvelope"/> was written to
/// match, and it is a discovery filter rather than a definition — see the census remarks for what it
/// is blind to, which is not nothing.
/// </remarks>
/// <param name="Type">The type name exactly as the assembly declares it.</param>
/// <param name="Version">The value of the constant, read from the metadata rather than from source.</param>
public sealed record EnvelopeSuite(string Type, byte Version);

/// <summary>
/// A person's statement of which cryptographic suite one envelope type's version byte denotes, and
/// what framing that suite puts on the wire.
/// </summary>
/// <param name="Type">
/// The type name, transcribed. This side is text and the discovered side is text, which is what makes
/// <see cref="EnvelopeSuiteCensus.PinningNoType" /> a bucket this census can report at all: a pin
/// written with <c>nameof</c> would not survive the deletion it is supposed to notice — it would fail
/// to compile, which says "a symbol is missing" rather than "a suite this repository had an argument
/// about is gone".
/// </param>
/// <param name="Version">
/// The version byte, transcribed. Reading it from the type's own constant would make every row agree
/// with whatever that constant became, which is the drift <see cref="EnvelopeSuiteCensus.Disagreeing" />
/// exists to catch.
/// </param>
/// <param name="Framing">
/// The layout, transcribed, in the notation both types use in their own summaries. Structural, and the
/// one field of a pin this census compares <em>between</em> rows: two suites with the same framing are
/// one suite, and a census listing them separately would be describing a distinction that does not
/// exist.
/// </param>
/// <param name="Description">
/// What the version byte denotes — the cryptographic suite, named. No default value: a row somebody
/// pasted without saying what the byte means is the state this member exists to leave behind, because
/// the whole point of the census is that <c>0x01</c> on its own means nothing until a column says which
/// of these two formats produced it.
/// </param>
/// <param name="Source">
/// The path of the file declaring the type, relative to the solution root, with <c>/</c> separators.
/// Carried because one rule about these declarations is not visible to reflection and has to be read
/// out of the source text — see
/// <see cref="EnvelopeSuiteCensusTests.EveryVersionByte_IsWrittenOutRatherThanAliased" />. A path that
/// names no file reddens there rather than being skipped, which is what stops the scan from passing by
/// finding nothing.
/// </param>
public sealed record EnvelopeSuitePin(
    string Type,
    byte Version,
    string Framing,
    string Description,
    string Source);

/// <summary>
/// Every discovered suite sorted by what the pins say about it.
/// </summary>
/// <param name="Unpinned">
/// Suites no pin claims. This is the bucket a third envelope format lands in, and it is red rather than
/// derived: there is nothing to compute for a suite nobody has said the cryptography of. Relax it and a
/// third format can be added, spend a version byte one of the other two already spends, and be
/// described nowhere.
/// </param>
/// <param name="PinningNoType">
/// Pins naming a type the scan did not find. Reported rather than dropped: a stale row silently shrinks
/// the census by one and lies in wait for whatever is next given that name. It is also the bucket that
/// catches a scan which came back empty — a discovery filter narrowed by an unrelated edit puts every
/// pin here at once, which no per-suite bucket would notice.
/// </param>
/// <param name="PinnedTwice">
/// Type names more than one pin claims. Two pins are two answers to "what does this type's version byte
/// mean", and the one nobody reads is the one that rots.
/// </param>
/// <param name="Disagreeing">
/// Suites whose pinned version byte differs from the constant the type actually declares, rendered as
/// both values so a failure says which way the byte moved. This is the bucket that catches the edit
/// this census was written for: a version bumped in code, on the reasoning that the two suites "should
/// not both be 1", with the argument for that byte left saying the old thing.
/// </param>
/// <param name="Agreed">
/// Suites exactly one pin claims and agrees with. Carried so a caller can prove the census read real
/// types rather than passing over an empty sequence.
/// </param>
public sealed record EnvelopeSuiteCensus(
    IReadOnlyList<string> Unpinned,
    IReadOnlyList<string> PinningNoType,
    IReadOnlyList<string> PinnedTwice,
    IReadOnlyList<string> Disagreeing,
    IReadOnlyList<string> Agreed);

/// <summary>
/// Pins every AEAD envelope suite this product defines to the cryptography its version byte denotes, so
/// that two suites spending <c>0x01</c> on different algorithms cannot be quietly reduced to one
/// constant.
/// </summary>
/// <remarks>
/// <para>
/// <b>The edit this exists to stop.</b> <see cref="CiphertextEnvelope.Version"/> and
/// <see cref="EncapsulatedValueEnvelope.Version"/> are both the literal <c>1</c>, declared twice. Every
/// instinct a reader brings to two identical constants in one namespace says to make the second an
/// alias of the first — which is what <see cref="Domain.Users.WrappedAccountKeys.EnvelopeVersion"/>
/// correctly is, because that <em>is</em> the AEAD format's byte. Here the two bytes are equal by
/// coincidence of both formats being first, and nothing else. Aliased, a successor to either suite
/// silently renumbers the other: values written under a suite this deployment implements start being
/// rejected, or worse,
/// values are written claiming a suite nothing implements. Neither failure is visible at the edit.
/// </para>
/// <para>
/// <b>Two suites sharing a version byte is legal here, deliberately.</b> A census refusing it would be
/// refusing a fact rather than a mistake — the bytes are separated by the column the value was read
/// from, not by their own contents, and that is the design.
/// <see cref="Census_AcceptsTwoSuitesSharingAVersionByte"/> states it permanently rather than leaving
/// it as a property of today's two rows. What is refused is a suite nobody pinned and a pin whose byte
/// has drifted from the declaration.
/// </para>
/// <para>
/// <b>The subject is discovered and only the disposition is written down,</b> the shape
/// <c>RepositoryAttributionCensusTests</c> and <c>ConflictKindDispositionCensusTests</c> both take. A
/// written list of the suites that <em>are</em> described fails open: the next format nobody adds to it
/// keeps the census green on the only day it matters. Requiring every discovered type to be claimed by
/// exactly one pin fails closed.
/// </para>
/// <para>
/// <b>What counts as a suite, and what that is blind to.</b> A public static class — <c>abstract</c>
/// and <c>sealed</c> in metadata — in the <c>Domain.Security</c> namespace or a descendant of it,
/// declaring a <c>public const byte</c> named <c>Version</c>. Three blind spots follow and none is
/// closed by widening the filter, because widening only moves the boundary: a suite declaring its
/// version as a <c>static readonly byte</c>, or as an <c>int</c>, or naming it anything else is
/// invisible (<see cref="Discovery_IsBlindToAStaticClassDeclaringNoByteVersionConstant"/>); a suite
/// living in another namespace or another assembly is invisible
/// (<see cref="Discovery_IsBlindToATypeOutsideTheSecurityNamespace"/>); and a suite carried on an
/// instance type rather than a static class is invisible. What covers them is that all three are ways
/// of writing an envelope format that looks nothing like the two this repository has — a reviewer
/// reading such a change is the control, and this file is what makes them read it.
/// </para>
/// <para>
/// <b>What this census cannot judge</b> is whether a pinned description is <em>true</em>. Nothing
/// executes the sentence "ECDH over NIST P-256, HKDF-SHA-256, then AES-256-GCM"; the server holds no
/// key and runs neither suite. The census holds the three things that can be checked — that a suite has
/// been described, that the byte in the description is the byte in the code, and that no two
/// descriptions claim the same framing — and leaves the cryptography to the reviewer who has to read
/// the row before the suite can land.
/// </para>
/// <para>
/// The synthetic cases below are permanent negative controls, not scaffolding. They feed the census
/// arrangements this solution does not have, so the live assertion cannot pass by having found nothing
/// to report.
/// </para>
/// </remarks>
public sealed partial class EnvelopeSuiteCensusTests
{
    /// <summary>
    /// The shortest description that can name a cryptographic suite rather than gesture at one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A floor on length is a crude instrument and it is the right one here. "AES" is four characters
    /// and says nothing about the mode, the nonce width or the tag width; naming a suite to the standard
    /// of the two rows below cannot be done in forty characters by accident, and can trivially be
    /// faked in forty-one. The floor is not what makes a description good — a reviewer is — it is what
    /// stops an empty string, a placeholder or a repeated type name from satisfying the member.
    /// </para>
    /// <para>
    /// <b>Deliberately not accompanied by a uniqueness rule over descriptions.</b> A test demanding that
    /// free-text justifications differ from one another pressures whoever adds the third row into
    /// inventing a difference — into writing around the test rather than about the suite. Uniqueness
    /// is asserted over <see cref="EnvelopeSuitePin.Framing"/> instead, where it is a structural fact
    /// with an answer, and where two rows colliding means something real.
    /// </para>
    /// </remarks>
    private const int ShortestUsefulDescription = 40;

    /// <summary>
    /// The suites, and what each one's version byte denotes. A third envelope format is red until it has
    /// a line here, and the line is where somebody says which cryptography that byte stands for.
    /// </summary>
    private static readonly EnvelopeSuitePin[] Pinned =
    [
        new(
            "CiphertextEnvelope",
            0x01,
            "version(1) || nonce(12) || ciphertext || tag(16)",
            "AES-256-GCM with a 96-bit nonce and a 128-bit tag. The symmetric suite, and the one both "
            + "of this product's symmetric constructions use: a narrative field is sealed under the "
            + "content key, an account key is wrapped under a factor's key-encryption key. Whoever "
            + "wraps holds the same key as whoever unwraps, which is why re-wrapping under a new key "
            + "needs the factor itself in hand",
            "Domain/Security/CiphertextEnvelope.cs"),
        new(
            "EncapsulatedValueEnvelope",
            0x01,
            "version(1) || ephemeral public key(65) || nonce(12) || ciphertext || tag(16)",
            "ECDH over NIST P-256 against the factor's public key, HKDF-SHA-256 over the shared secret, "
            + "then AES-256-GCM with a 96-bit nonce and a 128-bit tag. Asymmetric, which is the whole "
            + "point: encapsulating needs only the public half, so a value can be encapsulated to a "
            + "factor that is not present",
            "Domain/Security/EncapsulatedValueEnvelope.cs"),
    ];

    /// <summary>
    /// Every envelope suite the Domain assembly declares is described by exactly one pin, and every pin
    /// names a suite that exists and agrees with what it declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each bucket is asserted as a collection rather than as a joined string. Recorded as measured on
    /// TUnit 1.56 by the sibling census: the string form renders about a hundred characters of the
    /// received value and then an ellipsis, so a failure naming several offenders reports the first and
    /// hides the rest. A guard whose failure names one of several is the shape of guard that gets
    /// "fixed" one line at a time.
    /// </para>
    /// <para>
    /// <b>The four buckets are not reported together, and saying otherwise would be wrong.</b> A failing
    /// <c>Assert.That</c> throws, so the first non-empty bucket is the only one a run prints. Measured
    /// on a rename of one of the suites, which fills two buckets at once: the type under its new name
    /// landed in <c>Unpinned</c> and was reported, while <c>PinningNoType</c> — holding the pin that
    /// still named the old one — was never reached. What the collection form buys is therefore narrower
    /// than "everything at once": the bucket that does fail names <em>every</em> one of its own members.
    /// That is the half that matters, because the failure this census was written for puts several
    /// names in one bucket.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryEnvelopeSuite_IsPinnedToTheCryptographyItsVersionByteDenotes()
    {
        // Arrange — the subject is read off the assembly and never written down here.
        IReadOnlyList<EnvelopeSuite> found = EnvelopeSuites.DiscoveredIn(typeof(CiphertextEnvelope).Assembly);

        // Act
        EnvelopeSuiteCensus census = EnvelopeSuiteDisposition.Take(found, Pinned);

        // Assert
        await Assert.That(census.Unpinned).IsEmpty();
        await Assert.That(census.PinningNoType).IsEmpty();
        await Assert.That(census.PinnedTwice).IsEmpty();
        await Assert.That(census.Disagreeing).IsEmpty();

        // Non-vacuity, and the half that makes the four lines above mean anything. A scan that came
        // back empty empties three of the four buckets; every pin lands in PinningNoType, which is the
        // bucket that would actually redden — so the empty-scan case is covered above, and what these
        // lines add is the one it does not: a scan that found types but dropped one of them silently.
        // Written as a one-way difference so a failure names the row that went missing.
        string[] neverAgreed =
        [
            .. Pinned.Select(pin => pin.Type)
                .Except(census.Agreed, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        await Assert.That(neverAgreed).IsEmpty();
        await Assert.That(census.Agreed).IsNotEmpty();

        // Set difference is blind to a row counted twice, which is the one way Agreed could hold every
        // pinned name and still not be the pinned set.
        await Assert.That(census.Agreed.Count).IsEqualTo(Pinned.Length);
    }

    /// <summary>
    /// No two pins claim the same framing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one comparison this census makes between rows, and it is structural.</b> A framing is the
    /// layout a client slices on; two suites with identical framings are one suite under two names, and
    /// a census listing them separately would be asserting a distinction that does not exist — which is
    /// worse than no census, because the file would read as evidence that somebody had checked.
    /// </para>
    /// <para>
    /// It also covers the copy-paste that produces a third row: pasting an existing row and changing
    /// only the type name leaves the framing identical and reddens here. What it cannot see is a framing
    /// string that differs from its neighbour by a typo and from the real layout by everything — the
    /// framings are transcribed, not read out of either type, and the reason they are transcribed is
    /// the reason every pin in every census here is: a row derived from the code it describes agrees
    /// with the code whatever the code says. <c>EncapsulatedValueEnvelopeTests</c> and
    /// <c>CiphertextEnvelopeTests</c> are what hold each framing against its own constants.
    /// </para>
    /// </remarks>
    [Test]
    public async Task NoTwoPins_ClaimTheSameFraming()
    {
        // Act — grouped ordinally: two framings differing only by case are two strings, and a
        // case-insensitive comparison would report a collision between rows that do not collide.
        string[] shared =
        [
            .. Pinned
                .GroupBy(pin => pin.Framing, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group =>
                {
                    string types = string.Join(
                        ", ", group.Select(pin => pin.Type).Order(StringComparer.Ordinal));

                    return $"{group.Key}: {types}";
                })
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(shared).IsEmpty();

        // Non-vacuity: a Pinned table that had emptied out would satisfy the line above.
        await Assert.That(Pinned).IsNotEmpty();
    }

    /// <summary>
    /// Every suite's version byte is written out as a number, never as the other suite's constant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one rule about these declarations that nothing else in the build can see, and the reason
    /// it is worth reading source text to hold.</b> Measured: replacing
    /// <c>EncapsulatedValueEnvelope.Version = 1</c> with <c>= CiphertextEnvelope.Version</c> left all 26
    /// tests over these two types green. <c>FieldInfo.IsLiteral</c> is <see langword="true"/> and
    /// <c>GetRawConstantValue()</c> returns <c>1</c> for the alias exactly as for the literal, so the
    /// rest of this census — which reads metadata — is blind to it by construction, and so is every
    /// assertion in <c>EncapsulatedValueEnvelopeTests</c>.
    /// </para>
    /// <para>
    /// <b>The alias is inert on the day it lands, and that is what makes it worth a test rather than a
    /// remark.</b> It sets a trap. The day somebody bumps <see cref="CiphertextEnvelope.Version"/> to
    /// <c>2</c>, <c>EncapsulatedValueEnvelopeTests.Version_IsTheOneVersionDefined</c> goes red — and the
    /// obvious reading of that red is "the version moved, update the pin". A careful person updates the
    /// pin, the bar goes green, and every value this product encapsulates is now versioned under a suite no
    /// client ever agreed to. A test that leads a careful reader to the wrong repair is worse than no
    /// test, and this is what stops that red from being reachable.
    /// </para>
    /// <para>
    /// <b>What this does not prove, written down because the next reader will otherwise delete one of
    /// the two.</b> A source scan sees spelling, not meaning. It cannot tell a correct literal from a
    /// wrong one: <c>= 7</c> is a numeric literal and passes here.
    /// <c>EncapsulatedValueEnvelopeTests.Version_IsTheOneVersionDefined</c> and
    /// <c>CiphertextEnvelopeTests.Version_IsTheOneVersionDefined</c> are what hold the <em>values</em>,
    /// and they are blind to the spelling. Neither is redundant with this; the two halves fail on
    /// different edits and both are needed.
    /// </para>
    /// <para>
    /// <b>Both suites are scanned, not only the newer one</b>, because the trap is symmetric. Today
    /// <see cref="CiphertextEnvelope"/> is the older format and the natural direction of an alias is
    /// towards it, but a reader tidying two equal constants has no reason to prefer one direction, and
    /// an alias written the other way is the same defect with the same silent consequence. A rule that
    /// covered one row would also be a rule somebody has to remember to extend when a third suite
    /// lands; keyed on the pin table, a third suite is scanned the moment it is pinned.
    /// </para>
    /// <para>
    /// <b>The scan's honest risk is the comment, and it is real in this tree.</b>
    /// <c>EncapsulatedValueEnvelope.cs</c> contains the text <c>= CiphertextEnvelope.Version</c> inside
    /// its own XML remarks, in the paragraph arguing against exactly that spelling — so a scanner
    /// matching the bare phrase anywhere in the file would report the file that is doing the right
    /// thing loudest.
    /// <see cref="VersionDeclaration.In"/> anchors on a declaration at the start of a line instead, which
    /// no <c>///</c> line can be, and
    /// <see cref="Scan_IgnoresAnAliasQuotedInADocComment"/> is the permanent control for it.
    /// </para>
    /// <para>
    /// <b>The walker is re-derived here rather than shared,</b> for the reason
    /// <c>ConflictKindDispositionCensusTests</c> gives for re-deriving its own: the one over there is
    /// private to its guard, and promoting it would give one method two owners for the sake of ten
    /// lines. It throws rather than returning nothing when no ancestor holds the solution, which is what
    /// keeps "the test ran from a published output with no source tree" from reading as a pass.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EveryVersionByte_IsWrittenOutRatherThanAliased()
    {
        // Arrange
        DirectoryInfo root = VersionDeclaration.SolutionRootFrom(AppContext.BaseDirectory);

        // Act — three outcomes per pin, reported together rather than as a fail-fast chain: the file is
        // missing, the file holds no declaration this scan recognises, or the declaration is not a
        // number. The first two are what stop a scan that read nothing from passing.
        List<string> unreadable = [];
        List<string> aliased = [];
        int scanned = 0;

        foreach (EnvelopeSuitePin pin in Pinned)
        {
            string path = Path.Combine(root.FullName, pin.Source.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                unreadable.Add($"{pin.Type}: no file at {pin.Source}");
                continue;
            }

            string? initialiser = VersionDeclaration.In(File.ReadAllText(path));

            if (initialiser is null)
            {
                unreadable.Add(
                    $"{pin.Type}: {pin.Source} holds no single 'public const byte Version'");
                continue;
            }

            scanned++;

            if (!VersionDeclaration.IsNumericLiteral(initialiser))
            {
                aliased.Add($"{pin.Type}: Version = {initialiser}");
            }
        }

        // Assert
        await Assert.That(unreadable).IsEmpty();
        await Assert.That(aliased).IsEmpty();

        // Non-vacuity. Without this a scan that matched nothing anywhere leaves both lists empty — and
        // unlike the metadata census above, there is no third bucket here that would fill up instead.
        await Assert.That(scanned).IsEqualTo(Pinned.Length);
        await Assert.That(scanned).IsGreaterThan(0);
    }

    /// <summary>
    /// The scan reads a declaration and not a mention of one in a doc comment.
    /// </summary>
    /// <remarks>
    /// The permanent control for the failure that would be silent and backwards. Every <c>///</c> line
    /// is indented text, never a declaration, so anchoring the match on a line whose first non-space
    /// characters are the declaration itself is what separates the two — and the source this scan reads
    /// really does argue against the alias by quoting it. A scanner that matched the quoted form would
    /// redden the file that is correct and stay quiet about the file that is not.
    /// </remarks>
    [Test]
    public async Task Scan_IgnoresAnAliasQuotedInADocComment()
    {
        // Arrange — the shape EncapsulatedValueEnvelope.cs actually has: the alias named in prose,
        // the literal declared in code.
        const string source = """
            /// <remarks>
            /// The literal 1, transcribed, and never <c>= CiphertextEnvelope.Version</c>.
            /// </remarks>
            public const byte Version = 1;
            """;

        // Act
        string? initialiser = VersionDeclaration.In(source);

        // Assert
        await Assert.That(initialiser).IsEqualTo("1");
        await Assert.That(VersionDeclaration.IsNumericLiteral(initialiser!)).IsTrue();
    }

    /// <summary>
    /// The scan reads an alias as an alias, in both of the spellings one could be written in.
    /// </summary>
    /// <remarks>
    /// The positive control the live case cannot supply while the product is correct: with both
    /// declarations written out, <see cref="EveryVersionByte_IsWrittenOutRatherThanAliased"/> is
    /// satisfied by a scan that called everything a literal. These two are the arrangements that
    /// separate the two answers, and the second is not decoration — a reader aliasing across the two
    /// types is as likely to write the qualified name as the bare one, and a scan keyed on the word
    /// <c>CiphertextEnvelope</c> rather than on "is this a number" would miss an alias to a third
    /// suite entirely.
    /// </remarks>
    [Test]
    [Arguments("CiphertextEnvelope.Version")]
    [Arguments("Domain.Security.CiphertextEnvelope.Version")]
    public async Task Scan_ReadsAnAliasedVersionAsNotALiteral(string alias)
    {
        // Arrange
        string source = $"public const byte Version = {alias};";

        // Act
        string? initialiser = VersionDeclaration.In(source);

        // Assert
        await Assert.That(initialiser).IsEqualTo(alias);
        await Assert.That(VersionDeclaration.IsNumericLiteral(initialiser!)).IsFalse();
    }

    /// <summary>
    /// Both spellings a version byte is legitimately written in are read as literals.
    /// </summary>
    /// <remarks>
    /// Hex is what the two summaries and the persistence check constraints use, decimal is what the
    /// declarations use, and a scan admitting only one of them would push whoever lands the third suite
    /// into changing a spelling to satisfy a test rather than to say anything.
    /// </remarks>
    [Test]
    [Arguments("1")]
    [Arguments("0x01")]
    [Arguments("0xFF")]
    public async Task Scan_ReadsBothSpellingsOfANumberAsALiteral(string literal)
    {
        // Arrange
        string source = $"    public const byte Version = {literal};";

        // Act
        string? initialiser = VersionDeclaration.In(source);

        // Assert
        await Assert.That(initialiser).IsEqualTo(literal);
        await Assert.That(VersionDeclaration.IsNumericLiteral(initialiser!)).IsTrue();
    }

    /// <summary>
    /// A source holding no version declaration is reported as holding none, not as holding a literal.
    /// </summary>
    /// <remarks>
    /// The difference between <see langword="null"/> and a literal is what keeps the live case's
    /// non-vacuity count honest: a renamed constant, a moved declaration or a file the scan cannot parse
    /// has to land in <c>unreadable</c>, never pass silently as "nothing wrong found here".
    /// </remarks>
    [Test]
    public async Task Scan_ReportsASourceDeclaringNoVersionAtAll()
    {
        // Arrange
        const string source = "public const int NonceBytes = 12;";

        // Act
        string? initialiser = VersionDeclaration.In(source);

        // Assert
        await Assert.That(initialiser).IsNull();
    }

    /// <summary>
    /// Every pin says, in words, what cryptography its version byte denotes.
    /// </summary>
    /// <remarks>
    /// The member with no default value is what makes the row impossible to add silently; this is what
    /// makes it impossible to add emptily. Relax it and <c>0x01</c> can be pinned to <c>""</c>, which
    /// passes every other assertion in this file and leaves the next reader exactly where they were:
    /// holding a byte that means one thing in one column and another thing in another, with nothing
    /// written down about either. See <see cref="ShortestUsefulDescription"/> for why the floor is a
    /// length and why no uniqueness rule sits beside it.
    /// </remarks>
    [Test]
    public async Task EveryPin_SaysWhatItsVersionByteDenotes()
    {
        // Act — the length is reported beside the name so a failure says how far short the row fell
        // rather than only that it did.
        string[] unexplained =
        [
            .. Pinned
                .Where(pin => string.IsNullOrWhiteSpace(pin.Description)
                    || pin.Description.Length < ShortestUsefulDescription)
                .Select(pin => $"{pin.Type}: {pin.Description.Length} characters")
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(unexplained).IsEmpty();
        await Assert.That(Pinned).IsNotEmpty();
    }

    /// <summary>
    /// Two suites declaring the same version byte are both accepted, provided each is pinned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The state of the world, asserted permanently rather than left as a property of today's two
    /// rows.</b> Both shipped suites spend <c>0x01</c>, on different cryptography, and a census that
    /// refused that would be refusing a fact — somebody would then "fix" the code to satisfy it, by
    /// renumbering a format whose byte is already in stored rows and in check constraints.
    /// </para>
    /// <para>
    /// Written over synthetic suites so it keeps saying this after the real table changes: the day a
    /// third format takes <c>0x02</c>, the live census stops demonstrating the collision and this case
    /// is all that is left of the argument.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Census_AcceptsTwoSuitesSharingAVersionByte()
    {
        // Arrange
        EnvelopeSuite[] found = [new("FirstEnvelope", 0x01), new("SecondEnvelope", 0x01)];
        EnvelopeSuitePin[] pins =
        [
            new("FirstEnvelope", 0x01, "version(1) || nonce(12) || ciphertext || tag(16)", "a suite",
                "Synthetic/FirstEnvelope.cs"),
            new("SecondEnvelope", 0x01, "version(1) || point(65) || ciphertext || tag(16)", "another",
                "Synthetic/SecondEnvelope.cs"),
        ];

        // Act
        EnvelopeSuiteCensus census = EnvelopeSuiteDisposition.Take(found, pins);

        // Assert
        await Assert.That(string.Join(", ", census.Unpinned)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.PinningNoType)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.PinnedTwice)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(" | ", census.Disagreeing)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo("FirstEnvelope, SecondEnvelope");
    }

    /// <summary>
    /// A suite no pin claims is reported, and the pinned suites beside it are not dragged down with it.
    /// </summary>
    /// <remarks>
    /// The bucket a third envelope format lands in. Reported per suite rather than as a count, so the
    /// failure names the type somebody has to write a row about.
    /// </remarks>
    [Test]
    public async Task Census_ReportsASuiteNoPinClaims()
    {
        // Arrange
        EnvelopeSuite[] found = [new("KnownEnvelope", 0x01), new("ThirdEnvelope", 0x02)];
        EnvelopeSuitePin[] pins =
        [
            new("KnownEnvelope", 0x01, "version(1) || nonce(12) || ciphertext || tag(16)", "a suite",
                "Synthetic/KnownEnvelope.cs"),
        ];

        // Act
        EnvelopeSuiteCensus census = EnvelopeSuiteDisposition.Take(found, pins);

        // Assert
        await Assert.That(string.Join(", ", census.Unpinned)).IsEqualTo("ThirdEnvelope");
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo("KnownEnvelope");
    }

    /// <summary>
    /// A pin naming a type the scan did not find is reported rather than dropped.
    /// </summary>
    /// <remarks>
    /// Two things land here and both matter. A deleted or renamed suite leaves a row describing
    /// cryptography nothing implements, which will be inherited by whatever is next called that. And a
    /// discovery filter that narrowed to nothing puts every pin in this bucket at once — the only
    /// bucket that can see a scan which read no types at all, because the other three iterate over what
    /// was found.
    /// </remarks>
    [Test]
    public async Task Census_ReportsAPinNamingNoType()
    {
        // Arrange
        EnvelopeSuite[] found = [];
        EnvelopeSuitePin[] pins =
        [
            new("DepartedEnvelope", 0x01, "version(1) || nonce(12) || ciphertext || tag(16)", "a suite",
                "Synthetic/DepartedEnvelope.cs"),
        ];

        // Act
        EnvelopeSuiteCensus census = EnvelopeSuiteDisposition.Take(found, pins);

        // Assert
        await Assert.That(string.Join(", ", census.PinningNoType)).IsEqualTo("DepartedEnvelope");
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A type two pins claim is reported, and is not counted as agreed by either of them.
    /// </summary>
    /// <remarks>
    /// Two rows are two answers to what one byte means. Left alone, the reader who needs the answer
    /// reads whichever they find first, and the other is free to say the opposite — which is how a
    /// census becomes a place where a wrong sentence is protected by a right one sitting above it.
    /// </remarks>
    [Test]
    public async Task Census_ReportsATypePinnedTwice()
    {
        // Arrange
        EnvelopeSuite[] found = [new("KnownEnvelope", 0x01)];
        EnvelopeSuitePin[] pins =
        [
            new("KnownEnvelope", 0x01, "version(1) || nonce(12) || ciphertext || tag(16)", "a suite",
                "Synthetic/KnownEnvelope.cs"),
            new("KnownEnvelope", 0x01, "version(1) || nonce(12) || ciphertext || tag(16)", "again",
                "Synthetic/KnownEnvelope.cs"),
        ];

        // Act
        EnvelopeSuiteCensus census = EnvelopeSuiteDisposition.Take(found, pins);

        // Assert
        await Assert.That(string.Join(", ", census.PinnedTwice)).IsEqualTo("KnownEnvelope");
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A pin whose version byte has drifted from the constant is reported, as both values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bucket that catches a version bumped in code with the argument for it left saying the old
    /// thing — the likeliest way these two formats go wrong, because the reason to bump one of them is
    /// precisely that a reader noticed the two constants were equal.
    /// </para>
    /// <para>
    /// Rendered as both bytes in hex, because "pinned 0x01, declares 0x02" is a sentence somebody can
    /// act on and "a pin disagreed" is not. Hex rather than decimal to match how the byte is written
    /// everywhere else it appears: in the check constraints, in both types' summaries, and on the wire.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Census_ReportsAPinWhoseVersionByteDriftedFromTheDeclaration()
    {
        // Arrange
        EnvelopeSuite[] found = [new("KnownEnvelope", 0x02)];
        EnvelopeSuitePin[] pins =
        [
            new("KnownEnvelope", 0x01, "version(1) || nonce(12) || ciphertext || tag(16)", "a suite",
                "Synthetic/KnownEnvelope.cs"),
        ];

        // Act
        EnvelopeSuiteCensus census = EnvelopeSuiteDisposition.Take(found, pins);

        // Assert
        await Assert.That(string.Join(" | ", census.Disagreeing))
            .IsEqualTo("KnownEnvelope: pinned 0x01, declares 0x02");
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// Discovery reads the value the type declares, not the name of the constant.
    /// </summary>
    /// <remarks>
    /// <see cref="EnvelopeSuiteCensus.Disagreeing"/> is worth nothing if the discovered byte is a
    /// default rather than the metadata's constant, and the buckets cannot tell the two apart: a scan
    /// reporting <c>0</c> for everything would redden the live case, but so would a real drift, and a
    /// scan reporting the <em>pinned</em> value could never redden at all. This reads the one suite that
    /// existed before this census did and compares it with that type's own constant — an applied
    /// reading, deliberately not a literal, because the number itself is pinned in
    /// <c>CiphertextEnvelopeTests</c> and pinning it twice would mean two places to edit and one of
    /// them forgotten.
    /// </remarks>
    [Test]
    public async Task Discovery_ReadsTheDeclaredValueOfTheConstant()
    {
        // Act
        IReadOnlyList<EnvelopeSuite> found =
            EnvelopeSuites.In([typeof(CiphertextEnvelope)]);

        // Assert
        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].Type).IsEqualTo(nameof(CiphertextEnvelope));
        await Assert.That(found[0].Version).IsEqualTo(CiphertextEnvelope.Version);
    }

    /// <summary>
    /// A type outside the <c>Domain.Security</c> namespace is not a suite, whatever it declares.
    /// </summary>
    /// <remarks>
    /// A permanent demonstration of the scan's first blind spot rather than a defect to fix by widening
    /// it: an envelope format defined in <c>Application</c>, in the client, or in a namespace of its own
    /// is invisible here. Widening the filter only moves the boundary — what covers this is that a
    /// format defined outside the ring that owns the two existing ones is a change no reviewer reads
    /// past.
    /// </remarks>
    [Test]
    public async Task Discovery_IsBlindToATypeOutsideTheSecurityNamespace()
    {
        // Arrange — a real suite beside a public type from a namespace the scan does not read.
        Type[] types = [typeof(CiphertextEnvelope), typeof(EnvelopeSuiteCensusTests)];

        // Act
        IReadOnlyList<EnvelopeSuite> found = EnvelopeSuites.In(types);

        // Assert
        await Assert.That(found.Select(suite => suite.Type))
            .IsEquivalentTo(new[] { nameof(CiphertextEnvelope) });
    }

    /// <summary>
    /// A public static class in the namespace that declares no <c>public const byte Version</c> is not a
    /// suite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half of the filter that keeps the namespace's other residents out, demonstrated against a
    /// real one rather than a synthetic type: <see cref="NarrativeFieldLimits"/> is a public static class
    /// in <c>Domain.Security</c> holding two <c>public const int</c>s and no version, and it is not an
    /// envelope suite.
    /// </para>
    /// <para>
    /// The same line is the scan's second blind spot, stated from the other side: a suite that declared
    /// its version as a <c>static readonly byte</c>, as an <c>int</c>, or under another name would be
    /// passed over in exactly this way. That is not closed by loosening the filter — a filter matching
    /// any constant of any name would start reporting <see cref="NarrativeFieldLimits.NameBytes"/> as a
    /// version byte — it is closed by both existing suites having the same shape and a reviewer seeing
    /// a third that does not.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Discovery_IsBlindToAStaticClassDeclaringNoByteVersionConstant()
    {
        // Arrange
        Type[] types = [typeof(CiphertextEnvelope), typeof(NarrativeFieldLimits)];

        // Act
        IReadOnlyList<EnvelopeSuite> found = EnvelopeSuites.In(types);

        // Assert
        await Assert.That(found.Select(suite => suite.Type))
            .IsEquivalentTo(new[] { nameof(CiphertextEnvelope) });
    }

    /// <summary>
    /// Sorts each discovered suite by how the written pins claim it, and reports the pins nothing
    /// answers to.
    /// </summary>
    /// <remarks>
    /// <see cref="Take"/> takes both sides as parameters rather than reading the field above, which is
    /// what lets the synthetic cases prove every bucket without anyone deleting a real row to watch the
    /// suite go red.
    /// </remarks>
    private static class EnvelopeSuiteDisposition
    {
        internal static EnvelopeSuiteCensus Take(
            IEnumerable<EnvelopeSuite> found,
            IEnumerable<EnvelopeSuitePin> pins)
        {
            ArgumentNullException.ThrowIfNull(found);
            ArgumentNullException.ThrowIfNull(pins);

            EnvelopeSuite[] suites = [.. found.OrderBy(suite => suite.Type, StringComparer.Ordinal)];
            EnvelopeSuitePin[] claims = [.. pins];

            // Ordinal, and never a case-insensitive comparison: a type name differing only by case is a
            // different type, and a loose comparison would let a pin claim a suite it does not name.
            ILookup<string, EnvelopeSuitePin> byType =
                claims.ToLookup(pin => pin.Type, StringComparer.Ordinal);

            List<string> unpinned = [];
            List<string> pinnedTwice = [];
            List<string> disagreeing = [];
            List<string> agreed = [];

            foreach (EnvelopeSuite suite in suites)
            {
                EnvelopeSuitePin[] claiming = [.. byType[suite.Type]];

                switch (claiming.Length)
                {
                    case 0:
                        unpinned.Add(suite.Type);
                        break;
                    case 1 when claiming[0].Version != suite.Version:
                        disagreeing.Add(
                            $"{suite.Type}: pinned 0x{claiming[0].Version:X2}, "
                            + $"declares 0x{suite.Version:X2}");
                        break;
                    case 1:
                        agreed.Add(suite.Type);
                        break;
                    default:
                        pinnedTwice.Add(suite.Type);
                        break;
                }
            }

            HashSet<string> scanned = [.. suites.Select(suite => suite.Type)];

            List<string> pinningNoType =
            [
                .. claims
                    .Select(pin => pin.Type)
                    .Where(type => !scanned.Contains(type))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];

            return new EnvelopeSuiteCensus(unpinned, pinningNoType, pinnedTwice, disagreeing, agreed);
        }
    }

    /// <summary>
    /// Reads a suite's <c>Version</c> declaration out of its own source text, which is the only place
    /// the difference between a literal and an alias survives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Source text and not metadata, because there is nothing else left to read.</b> The C# compiler
    /// folds a <c>const</c> initialiser at compile time: an alias and a literal produce byte-identical
    /// metadata, so by the time an assembly is loaded the spelling is gone. That is the same argument
    /// <c>ConflictKindDispositionCensusTests</c> makes about a throw site — reflection cannot see an
    /// argument at a construction — and this follows that file's shape, including its walker's habit of
    /// throwing rather than returning nothing.
    /// </para>
    /// <para>
    /// <b>What it parses, and the two ways it fails closed.</b> One regex, anchored on a line whose
    /// first non-space characters are the declaration, capturing everything up to the semicolon. Zero
    /// matches and more than one match both return <see langword="null"/>, which the caller reports as
    /// unreadable rather than passing over: a renamed constant, a declaration split across lines, or a
    /// file holding two of them is a person's decision, not a silent green. Nothing here tries to parse
    /// C#; a scanner that did would be a second compiler with one user.
    /// </para>
    /// </remarks>
    private static partial class VersionDeclaration
    {
        private const string SolutionFileName = "BudgetoidApp.sln";

        /// <summary>
        /// Walks up from <paramref name="startDirectory"/> to the directory holding the solution, and
        /// throws rather than returning nothing when no ancestor does.
        /// </summary>
        internal static DirectoryInfo SolutionRootFrom(string startDirectory)
        {
            for (DirectoryInfo? directory = new(startDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                {
                    return directory;
                }
            }

            throw new InvalidOperationException(
                $"No ancestor of '{startDirectory}' holds {SolutionFileName}. This scan reads the "
                + "product's source from disk; it cannot run from a published output that carries no "
                + "source tree.");
        }

        /// <summary>
        /// The initialiser of the one <c>public const byte Version</c> declaration in
        /// <paramref name="source"/>, trimmed, or <see langword="null"/> when it holds none or more
        /// than one.
        /// </summary>
        internal static string? In(string source)
        {
            ArgumentNullException.ThrowIfNull(source);

            MatchCollection matches = Declaration().Matches(source);

            return matches.Count == 1 ? matches[0].Groups["initialiser"].Value.Trim() : null;
        }

        /// <summary>
        /// Whether <paramref name="initialiser"/> is a number written out, in either of the two
        /// spellings this repository uses for a version byte.
        /// </summary>
        /// <remarks>
        /// Deliberately a test for what is <em>allowed</em> rather than for the alias that is not.
        /// Keyed on the word <c>CiphertextEnvelope</c> it would pass an alias to a third suite, and
        /// pass <c>Version = SomeOtherConstant</c>, both of which set exactly the same trap.
        /// </remarks>
        internal static bool IsNumericLiteral(string initialiser)
        {
            ArgumentNullException.ThrowIfNull(initialiser);

            return NumericLiteral().IsMatch(initialiser);
        }

        /// <summary>
        /// A <c>public const byte Version</c> declaration at the start of its own line.
        /// </summary>
        /// <remarks>
        /// <c>Multiline</c> so <c>^</c> means "the start of a line" rather than "the start of the
        /// file", and the leading class is spaces and tabs only — never <c>\s</c>, which matches a
        /// newline and would let the anchor drift past the <c>///</c> of a doc comment. That is the
        /// difference between reading a declaration and reading the paragraph arguing about one, and
        /// <c>EncapsulatedValueEnvelope.cs</c> contains both.
        /// </remarks>
        [GeneratedRegex(
            @"^[ \t]*public\s+const\s+byte\s+Version\s*=\s*(?<initialiser>[^;]+);",
            RegexOptions.Multiline)]
        private static partial Regex Declaration();

        /// <summary>A decimal or hexadecimal integer literal, and nothing else.</summary>
        [GeneratedRegex(@"^(?:0[xX][0-9a-fA-F]+|[0-9]+)$")]
        private static partial Regex NumericLiteral();
    }

    /// <summary>
    /// Finds the envelope suites an assembly declares and renders each as its name and its version byte.
    /// </summary>
    private static class EnvelopeSuites
    {
        /// <summary>The namespace an envelope format lives in, and every namespace beneath it.</summary>
        private const string SecurityNamespace = "Domain.Security";

        /// <summary>The name every suite gives the constant that defines its version.</summary>
        private const string VersionConstant = "Version";

        /// <summary>Every suite the assembly declares, ordered by name.</summary>
        internal static IReadOnlyList<EnvelopeSuite> DiscoveredIn(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            return In(assembly.GetTypes());
        }

        /// <summary>
        /// The subject filter, applied to types rather than to an assembly so it can be aimed at chosen
        /// input.
        /// </summary>
        /// <remarks>
        /// A static class is <c>abstract sealed</c> in metadata, which is the whole of the "static"
        /// test; an enum is sealed and not abstract, and a delegate is neither, so neither needs naming.
        /// <c>IsPublic</c> is false for a nested type whatever its accessibility, which is what keeps
        /// compiler-generated closure and state-machine types out without a rule about them.
        /// </remarks>
        internal static IReadOnlyList<EnvelopeSuite> In(IEnumerable<Type> types)
        {
            ArgumentNullException.ThrowIfNull(types);

            return
            [
                .. types
                    .Where(type =>
                        type is { IsClass: true, IsAbstract: true, IsSealed: true, IsPublic: true })
                    .Where(type => InSecurityNamespace(type.Namespace))
                    .Select(type => (Type: type, Version: VersionOf(type)))
                    .Where(found => found.Version is not null)
                    .Select(found => new EnvelopeSuite(found.Type.Name, found.Version!.Value))
                    .OrderBy(suite => suite.Type, StringComparer.Ordinal),
            ];
        }

        /// <summary>
        /// The value of the type's own <c>public const byte Version</c>, or <see langword="null"/> when
        /// it declares no such member.
        /// </summary>
        /// <remarks>
        /// <c>IsLiteral</c> with <c>IsInitOnly</c> false is what distinguishes a <c>const</c> from a
        /// <c>static readonly</c> — the two are indistinguishable at the call site and not here, and it
        /// is the <c>const</c> that a check constraint can be written from.
        /// <c>DeclaredOnly</c> because a suite is a static class and inherits nothing; reading an
        /// inherited member would be reading a field some other type owns.
        /// </remarks>
        private static byte? VersionOf(Type type)
        {
            FieldInfo? version = type.GetField(
                VersionConstant,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

            return version is { IsLiteral: true, IsInitOnly: false } && version.FieldType == typeof(byte)
                ? (byte?)version.GetRawConstantValue()
                : null;
        }

        /// <summary>
        /// Whether a namespace is the security namespace or one nested inside it.
        /// </summary>
        /// <remarks>
        /// The trailing dot is load-bearing. A bare <c>StartsWith</c> would also claim a sibling
        /// namespace such as <c>Domain.SecurityLegacy</c>, and equality alone would let a
        /// <c>Domain.Security.Envelopes</c> folder hold a format the census never sees.
        /// </remarks>
        private static bool InSecurityNamespace(string? candidate) =>
            candidate is not null
            && (string.Equals(candidate, SecurityNamespace, StringComparison.Ordinal)
                || candidate.StartsWith($"{SecurityNamespace}.", StringComparison.Ordinal));
    }
}
