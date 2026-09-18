using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Api.Endpoints;
using Api.Infrastructure;
using Application.RecoveryCodes.GenerateRecoveryCodes;
using Domain.Security;
using Domain.Users;

namespace IntegrationTests;

/// <summary>
/// The server's half of <c>docs/business-logic/vectors/account-keys-wire-v1.json</c>: the four messages
/// that carry an account's key custody, compared member for member against the one file both suites
/// read, and the widths those members may hold compared against the constants that enforce them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing else in this repository binds the two sides.</b> There is no OpenAPI artifact, no
/// generated client and no captured fixture, so the shape of a request is asserted twice — once by a
/// record here and once by an interface in the browser — and until the artifact existed the two
/// assertions never met. That is not theoretical: the backend moved a factor from its own copy of the
/// account's keys to an ECDH keypair, both suites stayed green throughout, and the client went on
/// sending members no route bound. Each side was checking itself.
/// </para>
/// <para>
/// <b>The design property is that this file can only be greened by editing a file the other suite also
/// reads.</b> A contract test whose expectation lives in the suite that produces the value can be
/// satisfied by pasting the actual over the expected, and the paste is invisible in review because the
/// diff reads as a test being updated beside its code. Here the same paste moves the browser's
/// expectation too, so renaming a member on one side reddens the other — which is the whole point.
/// </para>
/// <para>
/// <b>Every case is a pin and will be green on arrival.</b> A manufactured red is the only evidence any
/// line of it works, and what each one catches is named in its own remarks.
/// </para>
/// <para>
/// <b>Why the wire spelling comes from the serializer rather than from a transform written here.</b>
/// The records are PascalCase and the wire is camelCase; a hand-rolled conversion would agree with
/// itself while disagreeing with what ships. <see cref="WireOptions" /> is
/// <see cref="JsonSerializerDefaults.Web" /> plus exactly the two converters <c>Program.cs</c> adds,
/// and the names are read off the resulting <see cref="JsonTypeInfo" /> — the same metadata the
/// serializer writes a body from, so a <c>[JsonPropertyName]</c> or a <c>[JsonIgnore]</c> on any member
/// is reflected here without this file knowing those attributes exist.
/// </para>
/// <para>
/// <b>Why this assembly and not the unit tier.</b> Three of the four records are nested inside
/// <c>Api.Endpoints</c> types and <c>UnitTests.csproj</c> deliberately holds no reference to
/// <c>Api</c> — an absence that is a pinned row in <c>ProjectReferenceGraphTests</c>, so adding one
/// there reddens a test rather than merely contradicting a comment. Nothing below touches PostgreSQL;
/// it lives here for the same reason <see cref="ClientKeyCustodyTests" /> does.
/// </para>
/// <para>
/// <b>Private nested records are reachable, and it was checked rather than assumed.</b> The three
/// endpoint records are <c>private sealed record</c> inside public static classes. Reflection's
/// discovery is not access-checked:
/// <c>GetNestedTypes(BindingFlags.NonPublic)</c> returns them, and
/// <see cref="JsonSerializerOptions.GetTypeInfo" /> resolves properties on them, provided the options
/// carry a resolver — which <see cref="JsonSerializerOptions.MakeReadOnly(bool)" /> supplies, exactly
/// as the runtime does on first use.
/// </para>
/// </remarks>
public sealed class AccountKeyWireContractTests
{
    /// <summary>The message names this file binds, and the whole set the artifact may carry.</summary>
    /// <remarks>
    /// Kept beside the cases rather than inside them so <see cref="Messages_AreExactlyTheSetThisFileBinds" />
    /// can compare against the same four names the cases below use, instead of a second list able to
    /// drift from them.
    /// </remarks>
    private static readonly string[] BoundMessages =
    [
        "registrationRequest",
        "recoveryCodeSubmission",
        "accountKeysResponse",
        "accountKeyEntry",
    ];

    /// <summary>The widths this file accounts for, every one of them bound to a server constant.</summary>
    /// <remarks>
    /// <para>
    /// <b>Five names, not six, and the manifest is what makes a reader count wrongly.</b> The artifact
    /// names five widths; four are a single <c>exactBytes</c> and the fifth — <c>manifest</c> — is a band
    /// carrying two numbers, so a census over the <em>numbers</em> comes to six and a census over the
    /// <em>names</em> comes to five. This list is names, and
    /// <see cref="Widths_AreExactlyTheSetThisFileAccountsFor" /> compares it against names.
    /// </para>
    /// <para>
    /// <b><c>accountKeysPlaintext</c> is bound, and the sentence that once said it could not be is the
    /// defect this replaced.</b> <c>WrappedAccountKeys.AccountKeysPlaintextBytes</c> is <c>private</c>,
    /// which is a fact about one symbol and not about the width: the width is already held by arithmetic
    /// over two <em>literal</em> pins that do not follow an edit —
    /// <c>EncapsulatedValueEnvelopeTests.MinimumLength_IsAVersionAPointANonceAndATag</c> writes <c>94</c>
    /// out, and <see cref="ExactWidth_IsWhatTheServerConstantHolds" /> holds
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" /> against the artifact's <c>158</c>.
    /// <see cref="AccountKeysPlaintextWidth_IsWhatTheTwoPinnedConstantsLeaveBetweenThem" /> is that
    /// subtraction. Declaring the width unpinnable invited exactly one repair — widening a private
    /// constant to <c>public</c> for a test's convenience — to close a gap that was never open.
    /// </para>
    /// <para>
    /// <b>What is genuinely unpinnable here is the <em>order</em> of the two halves inside that 64-byte
    /// plaintext, and no test on this side can ever hold it.</b> Content key first. Both halves are 32
    /// bytes, so a client that encapsulated them the other way round produces a value of exactly the
    /// right width carrying exactly the right version, which stores, reads back and opens — and yields
    /// an index key used to seal narrative text and a content key used to compute blind indexes. The
    /// server never sees the plaintext and holds nothing that could. The only place either
    /// implementation holds the order is the frozen <c>encapsulatedAccountKeys</c> vector in
    /// <c>factor-keypair-v1.json</c>, opening to two <em>different</em> named keys —
    /// <see cref="ClientKeyCustodyTests.TryOpenAccountKeys_OnTheFrozenStoredValues_YieldsTheContentKeyFirst" />.
    /// </para>
    /// </remarks>
    private static readonly string[] NamedWidths =
    [
        "wrappedPrivateKey",
        "encapsulatedAccountKeys",
        "factorPublicKey",
        "accountKeysPlaintext",
        "manifest",
    ];

    /// <summary>
    /// The serializer configuration the API serves these messages under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reconstructed rather than read off a running host</b>, because standing the API up costs a
    /// PostgreSQL database for a question that has nothing to do with one.
    /// <c>Program.cs</c> calls <c>ConfigureHttpJsonOptions</c> and adds a
    /// <see cref="JsonStringEnumConverter" /> and an <see cref="OptionalJsonConverterFactory" /> and
    /// nothing else — in particular no <c>PropertyNamingPolicy</c>, so the camelCase below is
    /// <see cref="JsonSerializerDefaults.Web" />'s own. Both converters are added here even though
    /// neither can affect a property name: a converter that later did would then be visible to this
    /// file instead of invisible to it.
    /// </para>
    /// <para>
    /// The gap this leaves is stated rather than hidden. A naming policy added to <c>Program.cs</c>
    /// tomorrow would change the wire and not this file, and every case below would keep passing
    /// against the old spelling. What closes that is the browser: the artifact is the client's
    /// expectation too, and a server that started emitting <c>Wrapped_Private_Key</c> would break the
    /// client's own reads against the same file.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions WireOptions = BuildWireOptions();

    /// <summary>
    /// <c>POST /api/registration/</c>'s body binds exactly the members the artifact names.
    /// </summary>
    /// <remarks>
    /// The message the browser composes from three sources — a passkey payload, a pair of factor
    /// envelopes and three members declared in place — so the side most able to grow a member nothing
    /// reads is this one. <c>sub</c>, <c>email</c> and <c>rotationEpoch</c> are refused on the record
    /// and absent from the artifact; the second direction below is what makes those three absences a
    /// gate rather than a pair of comments.
    /// </remarks>
    [Test]
    public async Task RegistrationRequest_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.Members("registrationRequest");
        Type record = NestedRecord(typeof(RegistrationEndpoints), "RegistrationRequest");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(record);

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// The submission that carries one recovery code's whole share of the account binds exactly four.
    /// </summary>
    /// <remarks>
    /// The one record of the four that is <c>public</c> and not nested, and the only one reached by two
    /// routes — registration files ten of them and the regeneration route files ten more. A fifth member
    /// is not merely unneeded here: the code itself has no member to travel in, and the absence is the
    /// rule.
    /// <para>
    /// <b>Reached through <c>RegistrationRequest.Codes</c> rather than named, which is what makes it a
    /// claim about the route instead of about a type.</b> <see cref="RecoveryCodeSubmission" /> is
    /// <c>public</c>, so <c>typeof(...)</c> would compile — and would go on compiling beside a
    /// <c>RecoveryCodeSubmissionV2</c> that the registration body had been repointed at, binding a record
    /// no route reads. The element type of the member the request actually carries cannot be an orphan.
    /// Asserted beside the row assertion rather than instead of it, so a reader sees which type the
    /// traversal arrived at.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RecoveryCodeSubmission_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.Members("recoveryCodeSubmission");
        Type request = NestedRecord(typeof(RegistrationEndpoints), "RegistrationRequest");

        // Act
        Type row = ElementOf(request, "Codes");
        IReadOnlyList<string> bound = WireMemberNames(row);

        // Assert
        await Assert.That(row).IsEqualTo(typeof(RecoveryCodeSubmission));
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// <c>GET /api/me/account-keys</c>'s body binds exactly the three members the artifact names.
    /// </summary>
    /// <remarks>
    /// The wrapper that gave the account's own facts — the manifest and the generation it is in — a
    /// place to live. It is also what keeps <c>accountKeyEntry</c> at three members rather than
    /// permission to widen it, so the two cases are read together.
    /// </remarks>
    [Test]
    public async Task AccountKeysResponse_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.Members("accountKeysResponse");
        Type record = NestedRecord(typeof(AccountKeyEndpoints), "AccountKeysResponse");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(record);

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// One factor's row on the wire binds three members and no fourth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case the artifact says a subset comparison would not notice.</b> The row is
    /// specified never to grow a fourth member — no per-row public key, no <c>credentialId</c>, no
    /// <c>userId</c>, no <c>createdAtUtc</c> — and a one-directional check passes on a side that has
    /// grown one. The direction that catches it is server-minus-contract.
    /// </para>
    /// <para>
    /// <b>Reached through <c>AccountKeysResponse.Factors</c> rather than found by name, and the
    /// difference is the whole of what this case is worth.</b> A lookup for a nested type called
    /// <c>AccountKeyEntry</c> passes against an <em>orphan</em>: add an <c>AccountKeyEntryV2</c>, point
    /// the response's <c>Factors</c> at it, leave the old record where it is, and the case stays green
    /// having bound a record no route returns. Binding the element type of the member the route actually
    /// serves cannot do that — whatever the response carries is what is measured, whatever it is called.
    /// <see cref="AccountKeyEndpoints_DeclaresExactlyTheTwoWireRecordsTheContractNames" /> closes the
    /// other half: the abandoned record left sitting beside the new one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeyEntry_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.Members("accountKeyEntry");
        Type response = NestedRecord(typeof(AccountKeyEndpoints), "AccountKeysResponse");

        // Act
        Type row = ElementOf(response, "Factors");
        IReadOnlyList<string> bound = WireMemberNames(row);

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// <c>AccountKeyEndpoints</c> declares exactly the two wire records the artifact names, and no third.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The half a traversal cannot reach: a record that still exists after nothing points at it.</b>
    /// The two cases above now arrive at their subjects through
    /// <c>AccountKeysResponse.Factors</c> rather than by name, so a repointed member takes them with it
    /// — but an <c>AccountKeyEntryV2</c> added beside the record it replaced leaves the old declaration
    /// standing, described by the artifact, read by nobody, and inherited by whoever next reaches for
    /// that name. This is the case that reddens on the extra declaration.
    /// </para>
    /// <para>
    /// <b>Transcribed, never derived.</b> The expectation is two written strings rather than
    /// <c>nameof</c>: a pin spelled with <c>nameof</c> does not survive the deletion it exists to notice
    /// — it becomes a compile error saying "a symbol is missing" rather than a failure saying "a record
    /// this wire contract describes is gone". It is the rule <c>EnvelopeSuiteCensusTests</c> keeps for
    /// its own pin table.
    /// </para>
    /// <para>
    /// <b>Only this owner, and the limit is stated rather than papered over.</b>
    /// <c>RegistrationEndpoints</c> gets no equivalent: it declares three response records the artifact
    /// deliberately does not carry — the wire contract binds what a <em>client</em> composes — so a
    /// whole-set census there would have to name types the artifact says nothing about, which is a second
    /// list able to drift. What holds the registration root instead is
    /// <see cref="RegistrationRequest_BindsExactlyTheMembersTheContractNames" /> plus the browser reading
    /// the same file; reaching that record through the route's own delegate would need an
    /// <c>EndpointDataSource</c>, and therefore a host, for a question with nothing to do with one.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeyEndpoints_DeclaresExactlyTheTwoWireRecordsTheContractNames()
    {
        // Arrange
        string[] expected = ["AccountKeyEntry", "AccountKeysResponse"];

        // Act
        string[] declared =
        [
            .. typeof(AccountKeyEndpoints)
                .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(nested => nested.GetMethod("<Clone>$") is not null)
                .Select(nested => nested.Name)
                .Order(StringComparer.Ordinal),
        ];

        // Assert — two explicit differences rather than IsEquivalentTo, for the reason
        // AssertSetsAgreeAsync gives: a failure that prints the offending name on its own.
        await AssertSetsAgreeAsync(declared, expected);
    }

    /// <summary>
    /// An exact width in the artifact is the width the server constant holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the half the browser cannot do.</b> On the client those widths are read by nothing —
    /// it mints values of whatever size its own code produces — so binding them to the constants that
    /// actually refuse a value is what stops the two artifacts drifting apart while both suites stay
    /// green.
    /// </para>
    /// <para>
    /// The constants arrive as <c>[Arguments]</c> because each is a <c>const int</c>, so a rename of one
    /// is a compile error here and a change of value is this case going red — the two failures a reader
    /// wants told apart. <c>encapsulatedAccountKeys</c> is <em>smaller</em> than
    /// <c>wrappedPrivateKey</c> despite carrying a 65-byte ephemeral point, which is worth noticing
    /// before somebody "corrects" one of the two.
    /// </para>
    /// <para>
    /// <b><c>factorPublicKey</c> is held against a <em>borrowed</em> constant, and the two are different
    /// concepts that happen to be the same number.</b>
    /// <see cref="EncapsulatedValueEnvelope.EphemeralPublicKeyBytes" /> is the width of the
    /// <em>ephemeral</em> point a value of that framing carries inline; the artifact's
    /// <c>factorPublicKey</c> is the width of the point a <em>factor</em> owns, which never crosses this
    /// wire on its own and lives only inside a sealed manifest. Both are an uncompressed SEC1 point on
    /// P-256, so both are 65 today and will stay 65 for as long as the curve does — but if the framing
    /// ever carried a compressed ephemeral point while factors kept uncompressed ones, this row would
    /// silently follow the wrong one. It is borrowed rather than restated because a second <c>65</c>
    /// written out here would be a number able to disagree with nothing; the day the two concepts part,
    /// what is owed is a constant of its own on the manifest's side, not a literal in a test.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("wrappedPrivateKey", WrappedAccountKeys.WrappedPrivateKeyLength)]
    [Arguments("encapsulatedAccountKeys", WrappedAccountKeys.EncapsulatedAccountKeysLength)]
    [Arguments("factorPublicKey", EncapsulatedValueEnvelope.EphemeralPublicKeyBytes)]
    public async Task ExactWidth_IsWhatTheServerConstantHolds(string member, int serverWidth)
    {
        // Arrange
        int frozen = WireContract.Width(member, "exactBytes");

        // Act
        int actual = serverWidth;

        // Assert
        await Assert.That(actual).IsEqualTo(frozen);
    }

    /// <summary>
    /// The 64-byte plaintext inside <c>encapsulatedAccountKeys</c> is what the encapsulation framing
    /// leaves between its own floor and the width the column is fixed at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This row was once declared unpinnable, and that was false.</b>
    /// <c>WrappedAccountKeys.AccountKeysPlaintextBytes</c> is <c>private</c> — but
    /// <see cref="WrappedAccountKeys.EncapsulatedAccountKeysLength" /> is
    /// <see cref="EncapsulatedValueEnvelope.MinimumLength" /> plus that constant, so the plaintext's
    /// width is the difference between two values this suite already holds. Reading it that way needs
    /// nothing widened, and the false row invited exactly the repair it should have refused: making a
    /// private constant <c>public</c> so that a test could see it.
    /// </para>
    /// <para>
    /// <b>Both ends are held by literals that do not follow an edit, which is the whole of why the
    /// subtraction means something.</b> The floor is pinned as a hand-written <c>94</c> in
    /// <c>EncapsulatedValueEnvelopeTests.MinimumLength_IsAVersionAPointANonceAndATag</c>, and the total
    /// is pinned against the artifact's <c>158</c> by
    /// <see cref="ExactWidth_IsWhatTheServerConstantHolds" />. A subtraction over two constants that
    /// both tracked an edit would agree with whatever the code became; these two cannot.
    /// </para>
    /// <para>
    /// <b>Measured rather than reasoned.</b> Setting the private constant to <c>2 * AccountKeyBytes + 1</c>
    /// in a scratch copy of the tree reddens
    /// <see cref="ExactWidth_IsWhatTheServerConstantHolds" />'s <c>encapsulatedAccountKeys</c> argument
    /// and this case together — 159 against the artifact's 158, and 65 against its 64 — which is the
    /// evidence that the width was never unheld.
    /// </para>
    /// <para>
    /// <b>What it still does not hold is the order of the two halves</b>, and nothing on this side ever
    /// can. See <see cref="NamedWidths" />.
    /// </para>
    /// </remarks>
    [Test]
    public async Task AccountKeysPlaintextWidth_IsWhatTheTwoPinnedConstantsLeaveBetweenThem()
    {
        // Arrange
        int frozen = WireContract.Width("accountKeysPlaintext", "exactBytes");

        // Act
        int plaintext =
            WrappedAccountKeys.EncapsulatedAccountKeysLength - EncapsulatedValueEnvelope.MinimumLength;

        // Assert
        await Assert.That(plaintext).IsEqualTo(frozen);
    }

    /// <summary>
    /// The manifest's band is the band the server's two constants hold.
    /// </summary>
    /// <remarks>
    /// A band rather than a width because a manifest's plaintext grows with the factors it names. The
    /// floor is the AEAD framing's own — version, nonce and tag with no ciphertext, which is a manifest
    /// naming nobody — and it lives on <see cref="CiphertextEnvelope" /> rather than on
    /// <see cref="FactorManifest" />, which refuses emptiness and the ceiling and leaves the framing to
    /// the decoder at the edge. Two constants from two types, therefore, and the case says so rather
    /// than leaving a reader to look for a floor on the entity.
    /// </remarks>
    [Test]
    public async Task ManifestBand_IsWhatTheServerConstantsHold()
    {
        // Arrange
        int frozenFloor = WireContract.Width("manifest", "minimumBytes");
        int frozenCeiling = WireContract.Width("manifest", "maximumBytes");

        // Act
        int floor = CiphertextEnvelope.MinimumLength;
        int ceiling = FactorManifest.MaximumBytes;

        // Assert
        await Assert.That(floor).IsEqualTo(frozenFloor);
        await Assert.That(ceiling).IsEqualTo(frozenCeiling);
    }

    /// <summary>
    /// The artifact names exactly the four messages this file binds, in both directions.
    /// </summary>
    /// <remarks>
    /// <b>The same both-directions rule one level up, and it is not decoration.</b> A fifth message
    /// added to the artifact with no case beneath it would be a document nobody reads while looking
    /// exactly like coverage, and a message deleted from it would take its case's expectation with it
    /// silently if the case were the only reader. Compared as a set, which is what
    /// <c>CollectionOrdering.Any</c> would give — but written as two explicit differences so the
    /// failure names the message rather than reporting two counts.
    /// </remarks>
    [Test]
    public async Task Messages_AreExactlyTheSetThisFileBinds()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.MessageNames();

        // Act
        string[] unbound = [.. contract.Except(BoundMessages, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] missing = [.. BoundMessages.Except(contract, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // Assert
        await Assert.That(unbound).IsEmpty();
        await Assert.That(missing).IsEmpty();
    }

    /// <summary>
    /// The artifact names exactly the widths this file accounts for, in both directions.
    /// </summary>
    /// <remarks>
    /// <b>Five names, and all five are bound.</b> Three are held against a constant by
    /// <see cref="ExactWidth_IsWhatTheServerConstantHolds" />, the manifest's band by
    /// <see cref="ManifestBand_IsWhatTheServerConstantsHold" />, and <c>accountKeysPlaintext</c> by the
    /// subtraction in
    /// <see cref="AccountKeysPlaintextWidth_IsWhatTheTwoPinnedConstantsLeaveBetweenThem" /> — see
    /// <see cref="NamedWidths" /> for why counting six here is the easy mistake. A sixth <em>name</em>
    /// arriving in the artifact reddens this case, which forces the choice between binding it and
    /// declaring why it cannot be bound, rather than letting it sit in the file asserting nothing.
    /// </remarks>
    [Test]
    public async Task Widths_AreExactlyTheSetThisFileAccountsFor()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.WidthNames();

        // Act
        string[] unaccounted = [.. contract.Except(NamedWidths, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] missing = [.. NamedWidths.Except(contract, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // Assert
        await Assert.That(unaccounted).IsEmpty();
        await Assert.That(missing).IsEmpty();
    }

    /// <summary>
    /// Both differences between a record's bound members and the artifact's list, asserted as empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two differences and never one comparison.</b> A subset check passes on a side that has grown
    /// a member the other does not bind, which is the live failure mode here rather than a tidiness
    /// rule. It is also not <c>IsEquivalentTo</c>: that would be the right ordering semantics — a set
    /// comparison is exactly what <c>CollectionOrdering.Any</c> gives — but its failure reports two
    /// collections and leaves a reader to diff them, where an empty-difference assertion prints the
    /// offending member on its own.
    /// </para>
    /// <para>
    /// The server-minus-contract direction is asserted first on purpose: a rename produces one entry in
    /// each direction, and the more useful sentence is the spelling the server has just started
    /// emitting.
    /// </para>
    /// </remarks>
    private static async Task AssertSetsAgreeAsync(
        IReadOnlyList<string> bound,
        IReadOnlyList<string> contract)
    {
        string[] boundButNotInTheContract =
            [.. bound.Except(contract, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] inTheContractButNotBound =
            [.. contract.Except(bound, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        await Assert.That(boundButNotInTheContract).IsEmpty();
        await Assert.That(inTheContractButNotBound).IsEmpty();
    }

    /// <summary>
    /// The wire spellings a record serializes to, in declaration order.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonTypeInfo.Properties" /> is the metadata the serializer writes a body from, so this
    /// is the actual wire spelling rather than a camel-casing applied here — and a member hidden with
    /// <c>[JsonIgnore]</c> is absent from it exactly as it is absent from the wire.
    /// </remarks>
    private static IReadOnlyList<string> WireMemberNames(Type record) =>
        [.. WireOptions.GetTypeInfo(record).Properties.Select(property => property.Name)];

    /// <summary>
    /// The nested type <paramref name="name" /> of <paramref name="owner" />, or a throw naming it.
    /// </summary>
    /// <remarks>
    /// <b>Non-public, because all three endpoint records are <c>private</c>.</b> Reflection's discovery
    /// is not access-checked, so the declaration stays private — a wire record is the endpoint's own
    /// business and nothing outside it may construct one — while a test can still read its shape. The
    /// throw is what makes renaming the record itself loud: a null here would otherwise be a
    /// <see cref="NullReferenceException" /> from inside a serializer call two frames away.
    /// </remarks>
    /// <summary>
    /// The element type of <paramref name="owner" />'s <paramref name="member" />, or a throw naming it.
    /// </summary>
    /// <remarks>
    /// <b>What turns a lookup by name into a claim about a route.</b> A record found by name can be an
    /// orphan — declared, described, and pointed at by nothing — while the member that actually crosses
    /// the wire carries some other type entirely. Read off the member, the subject is whatever the route
    /// really serves. The single generic argument is taken rather than the interface walked: every list
    /// member in these records is declared <c>IReadOnlyList&lt;T&gt;</c>, and a member that stopped being
    /// one is a wire change a reader should meet as a throw here rather than as a silently different
    /// element type.
    /// </remarks>
    private static Type ElementOf(Type owner, string member)
    {
        PropertyInfo property = owner.GetProperty(member)
            ?? throw new InvalidOperationException(
                $"'{owner.FullName}' declares no member named '{member}'. The wire contract binds the "
                + "type this member carries, and a case that cannot find the member has bound nothing.");

        Type[] arguments = property.PropertyType.GetGenericArguments();

        return arguments.Length == 1
            ? arguments[0]
            : throw new InvalidOperationException(
                $"'{owner.FullName}.{member}' is '{property.PropertyType}', which carries "
                + $"{arguments.Length} type arguments rather than one. The wire contract expects a list "
                + "of one row type.");
    }

    private static Type NestedRecord(Type owner, string name) =>
        Array.Find(owner.GetNestedTypes(BindingFlags.NonPublic), nested => nested.Name == name)
        ?? throw new InvalidOperationException(
            $"'{owner.FullName}' declares no nested type named '{name}'. The wire contract names its "
            + "members, and a case that cannot find the record has bound nothing.");

    private static JsonSerializerOptions BuildWireOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new OptionalJsonConverterFactory());

        // What the runtime does on first use, done here so GetTypeInfo can be called without one.
        // Without it the call throws NotSupportedException about a null TypeInfoResolver, which reads
        // as a source-generation problem and is not one.
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }

}
