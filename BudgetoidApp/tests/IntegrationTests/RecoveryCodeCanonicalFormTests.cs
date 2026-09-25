using System.Text;
using System.Text.Json;

namespace IntegrationTests;

/// <summary>
/// <see cref="ClientKeyCustody.Canonical" /> and the two branches derived off it, against known answers
/// neither this codebase nor the browser authored:
/// <c>docs/business-logic/vectors/recovery-code-v1.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file exists because the fold is written twice and nothing compared the two.</b> The
/// authoritative implementation is <c>+core/security/recovery-code-canonical.ts</c> — it runs in a
/// browser and derives real keys. <see cref="ClientKeyCustody.Canonical" /> is a reproduction, kept so an
/// integration test can seed a code a redemption spends. Both were self-consistent and both were green,
/// and they disagreed on four code points: <c>U+FEFF</c>, <c>U+0085</c>, <c>U+00DF</c> and
/// <c>U+0131</c>. A disagreement here is not a cosmetic one — a recovery code is folded once and then
/// two independent HKDF branches run off the result, the verifier that crosses the wire and the
/// key-encryption key that never leaves the browser. Fold differently and a code that redeems fine opens
/// nothing, or the reverse, silently, long after the typing that caused it. That is CON-009's subject
/// exactly: every client implements the identical cryptographic contract.
/// </para>
/// <para>
/// <b>Every case below is a pin, and the frozen file is the contract.</b> The answers were computed
/// outside both codebases from the rule as prose, and the browser already reproduces them. When a case
/// here goes red, the implementation moved — the vector did not. Two of the fifteen canonical-form rows
/// are the only thing in this repository that holds the two platform-defined steps: <c>U+FEFF</c> and
/// <c>U+0085</c> are what a local whitespace test gets wrong, in opposite directions, and <c>U+00DF</c>
/// and <c>U+0131</c> are what a simple upper case gets wrong.
/// </para>
/// <para>
/// <b>Inputs are built from <c>inputUtf8Hex</c>, never from <c>input</c>.</b> Five rows carry a code
/// point nothing renders. Reading them through the hex is the only way a case can be certain its input
/// is the one the vector describes, and it takes the row out of reach of an editor that trims,
/// normalises or drops what it cannot draw. Outputs are compared the same way: the assertion that counts
/// is over <c>canonicalUtf8Hex</c>.
/// </para>
/// <para>
/// <b>Nothing here touches PostgreSQL, and it lives in this assembly anyway</b>, for the reason
/// <c>ClientKeyCustodyTests</c> gives: the subject is a type in this assembly that nothing else can see.
/// </para>
/// </remarks>
public sealed class RecoveryCodeCanonicalFormTests
{
    /// <summary>
    /// Each frozen canonical-form row, folded and compared as UTF-8 hex.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One case per row rather than one case over all fifteen</b>, so a failure names the rule that
    /// moved rather than a count. The row's own <c>why</c> is carried into the message, because the
    /// argument for a row is the only thing that says whether a departure is a bug or a decision nobody
    /// wrote down.
    /// </para>
    /// <para>
    /// Both the string and the hex are asserted. The hex is the one that holds: two strings that differ
    /// only by an invisible code point print identically in a failure message, and a reader comparing
    /// them by eye would call them equal.
    /// </para>
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(CanonicalFormRowIndices))]
    public async Task CanonicalForm_ReproducesTheFrozenRow(int index)
    {
        // Arrange
        JsonElement row = Vectors.CanonicalFormRow(index);
        string why = Vectors.Text(row, "why");
        string input = Utf8FromHex(Vectors.Text(row, "inputUtf8Hex"));

        // Act
        string canonical = ClientKeyCustody.Canonical(input);

        // Assert
        await Assert.That(Utf8Hex(canonical))
            .IsEqualTo(Vectors.Text(row, "canonicalUtf8Hex"))
            .Because(why);
        await Assert.That(canonical).IsEqualTo(Vectors.Text(row, "canonical")).Because(why);
    }

    /// <summary>
    /// The <c>input</c> a reader sees beside each row is the string <c>inputUtf8Hex</c> spells.
    /// </summary>
    /// <remarks>
    /// <b>The rows are driven off the hex, so the human-readable field is otherwise asserted by
    /// nothing.</b> That is exactly the field an editor mangles — a trimmed trailing space, a byte order
    /// mark a tool helpfully removed, a no-break space normalised to a plain one — and a row whose two
    /// halves have drifted apart is a row whose <c>why</c> is describing something the case no longer
    /// runs.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(CanonicalFormRowIndices))]
    public async Task CanonicalFormRow_SpellsItsOwnInputTwice(int index)
    {
        // Arrange
        JsonElement row = Vectors.CanonicalFormRow(index);

        // Act
        string fromHex = Utf8FromHex(Vectors.Text(row, "inputUtf8Hex"));

        // Assert
        await Assert.That(Utf8Hex(Vectors.Text(row, "input")))
            .IsEqualTo(Utf8Hex(fromHex))
            .Because(Vectors.Text(row, "why"));
    }

    /// <summary>
    /// The file still holds every row this suite was written against.
    /// </summary>
    /// <remarks>
    /// <b>Without it, deleting a row is a silent way to make a red case green.</b> The per-row cases are
    /// generated from the file, so a file with fourteen rows runs fourteen green cases and reports
    /// nothing missing. The count is written out rather than read from anywhere, which is the whole of
    /// what makes it a pin.
    /// </remarks>
    [Test]
    public async Task CanonicalForm_HoldsEveryRowThisSuiteWasWrittenAgainst()
    {
        // Arrange, Act
        int rows = Vectors.CanonicalFormRowCount;

        // Assert
        await Assert.That(rows).IsEqualTo(15);
    }

    /// <summary>The verifier branch reproduces the frozen value for each derivation row.</summary>
    /// <remarks>
    /// <b>The second row is the whole point of the fold</b>: the same code typed back grouped and in
    /// lower case must reach the value the minted code reached. Its canonical form is asserted too, so a
    /// failure says whether the fold moved or the derivation did.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(DerivationRowIndices))]
    public async Task Verifier_ReproducesTheFrozenValue(int index)
    {
        // Arrange
        JsonElement row = Vectors.DerivationRow(index);
        string code = Vectors.Text(row, "code");

        // Act
        string verifier = ClientKeyCustody.VerifierOf(code);

        // Assert
        await Assert.That(ClientKeyCustody.Canonical(code))
            .IsEqualTo(Vectors.Text(row, "canonical"));
        await Assert.That(verifier)
            .IsEqualTo(Vectors.Text(row, "verifierBase64Url"))
            .Because(Vectors.Text(row, "why"));
    }

    /// <summary>The key branch reproduces the frozen value for each derivation row.</summary>
    /// <remarks>
    /// <b>Asserted separately from the verifier, never derived from it.</b> The two branches share an
    /// HKDF extract over the same canonical text and differ only in <c>info</c>; that difference is the
    /// one thing keeping the value a redemption sends from being the value that opens the account, and a
    /// case that checked only one of them would be blind to the two swapping.
    /// </remarks>
    [Test]
    [MethodDataSource(nameof(DerivationRowIndices))]
    public async Task KeyEncryptionKey_ReproducesTheFrozenValue(int index)
    {
        // Arrange
        JsonElement row = Vectors.DerivationRow(index);
        string code = Vectors.Text(row, "code");

        // Act
        byte[] keyEncryptionKey = ClientKeyCustody.KeyEncryptionKeyFromRecoveryCode(code);

        // Assert
        await Assert.That(Convert.ToHexString(keyEncryptionKey).ToLowerInvariant())
            .IsEqualTo(Vectors.Text(row, "keyEncryptionKeyHex"))
            .Because(Vectors.Text(row, "why"));
    }

    /// <summary>Both HKDF <c>info</c> strings are the ones the frozen file names.</summary>
    /// <remarks>
    /// <b>The derivation rows cannot catch an <c>info</c> that moved on both branches at once</b> — they
    /// could only catch it as two values that no longer match, and a pair of constants edited together
    /// stays self-consistent. These two lines are what say the branch labels themselves are the
    /// contract's.
    /// </remarks>
    [Test]
    public async Task Infos_AreTheFrozenStrings()
    {
        // Arrange
        JsonElement infos = Vectors.Infos;

        // Act, Assert
        await Assert.That(ClientKeyCustody.RecoveryCodeVerifierInfo)
            .IsEqualTo(Vectors.Text(infos, "verifier"));
        await Assert.That(ClientKeyCustody.RecoveryCodeKeyEncryptionKeyInfo)
            .IsEqualTo(Vectors.Text(infos, "keyEncryptionKey"));
    }

    /// <summary>
    /// The locator throws rather than returning nothing when no ancestor holds <c>docs/</c>.
    /// </summary>
    /// <remarks>
    /// <b>"The test ran from a published output with no source tree" must never read as a pass.</b> Every
    /// case above would go green, having asserted nothing, and the whole point of the file is that
    /// somebody else authored the answers. <c>ClientKeyCustodyTests</c> keeps the same rule over its own
    /// vector file.
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

    /// <summary>One case per <c>canonicalForm</c> row the frozen file carries.</summary>
    public static IEnumerable<Func<int>> CanonicalFormRowIndices() =>
        Indices(Vectors.CanonicalFormRowCount);

    /// <summary>One case per <c>derivations</c> row the frozen file carries.</summary>
    public static IEnumerable<Func<int>> DerivationRowIndices() => Indices(Vectors.DerivationRowCount);

    private static IEnumerable<Func<int>> Indices(int count) =>
        Enumerable.Range(0, count).Select<int, Func<int>>(index => () => index);

    /// <summary>The string a run of lower-case hex spells in UTF-8.</summary>
    private static string Utf8FromHex(string hex) => Encoding.UTF8.GetString(Convert.FromHexString(hex));

    /// <summary>Lower-case hex of the UTF-8 bytes, which is the spelling the vector file uses.</summary>
    private static string Utf8Hex(string text) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant();

    /// <summary>
    /// The frozen file, read off disk, and the rows a case needs out of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It sits one level above the solution.</b> <c>BudgetoidApp.sln</c> lives in
    /// <c>BudgetoidApp/</c>; <c>docs/</c> is its sibling, so a walker anchored on the solution file stops
    /// one directory short and finds nothing. The anchor here is the vector file's own path.
    /// </para>
    /// <para>
    /// <b>It throws rather than skipping</b>, for the reason
    /// <see cref="Vectors_WithNoSourceTreeAboveThem_Throw" /> states.
    /// </para>
    /// </remarks>
    private static class Vectors
    {
        private const string RelativePath = "docs/business-logic/vectors/recovery-code-v1.json";

        private static readonly JsonDocument Document =
            JsonDocument.Parse(File.ReadAllText(LocateFrom(AppContext.BaseDirectory)));

        public static JsonElement Infos => Document.RootElement.GetProperty("infos");

        public static int CanonicalFormRowCount => CanonicalFormRows.GetArrayLength();

        public static int DerivationRowCount => DerivationRows.GetArrayLength();

        public static JsonElement CanonicalFormRow(int index) => CanonicalFormRows[index];

        public static JsonElement DerivationRow(int index) => DerivationRows[index];

        /// <summary>
        /// The absolute path of the frozen file, walking up from <paramref name="startDirectory" />.
        /// </summary>
        /// <exception cref="InvalidOperationException">No ancestor holds it.</exception>
        public static string LocateFrom(string startDirectory)
        {
            string relative = RelativePath.Replace('/', Path.DirectorySeparatorChar);

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
                $"No ancestor of '{startDirectory}' holds {RelativePath}. These are known answers this "
                + "codebase did not author; a run that cannot read them has pinned nothing, and it must "
                + "not be mistaken for a run that agreed with them.");
        }

        /// <summary>
        /// The string at <paramref name="property" /> of <paramref name="owner" />, or a throw naming it.
        /// </summary>
        /// <remarks>
        /// <b>In place of <c>GetString()!</c>.</b> The null-forgiving operator asserts something about a
        /// file on disk that this assembly does not own and cannot see at compile time, and it pays off
        /// as a <see cref="NullReferenceException" /> two frames away. A case that cannot read the value
        /// it pins has pinned nothing, and the refusal should say which value went missing.
        /// </remarks>
        public static string Text(JsonElement owner, string property) =>
            owner.TryGetProperty(property, out JsonElement value) && value.GetString() is { } text
                ? text
                : throw new InvalidOperationException(
                    $"The frozen vector file holds no string at '{property}'. These are known answers "
                    + "this codebase did not author; a case that cannot read the value it pins has "
                    + "pinned nothing.");

        private static JsonElement CanonicalFormRows =>
            Document.RootElement.GetProperty("canonicalForm");

        private static JsonElement DerivationRows => Document.RootElement.GetProperty("derivations");
    }
}
