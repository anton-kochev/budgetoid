using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Api.Endpoints;
using Api.Infrastructure;
using Application.KeyRotations.BeginKeyRotation;

namespace IntegrationTests;

/// <summary>
/// The server's half of <c>docs/business-logic/vectors/key-rotation-wire-v1.json</c>: the fourteen
/// messages the four key-rotation routes carry, compared member for member against the one file both
/// suites read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sibling of <see cref="AccountKeyWireContractTests" /> and it exists for the same reason.</b>
/// Nothing else in this repository binds the two sides: there is no OpenAPI artifact, no generated
/// client and no captured fixture, so the shape of a request is asserted twice — once by a record here
/// and once by an interface in the browser — and until an artifact exists the two assertions never
/// meet. That is not theoretical. The backend moved a factor from its own copy of the account's keys to
/// an ECDH keypair, both suites stayed green throughout, and the client went on sending members no
/// route bound. Each side was checking itself.
/// </para>
/// <para>
/// <b>Until this file landed the rotation vector had one reader, which pins one side and leaves the
/// other free to rename.</b> <c>key-rotation-api.service.spec.ts</c> compares the browser's own types
/// against those lists; nothing compared the records below to them. One reader alone is exactly the
/// arrangement that let the last change through, so neither may be deleted on the grounds that the
/// other covers the contract.
/// </para>
/// <para>
/// <b>The design property is that this file can only be greened by editing a file the other suite also
/// reads.</b> A contract test whose expectation lives in the suite that produces the value can be
/// satisfied by pasting the actual over the expected, and the paste is invisible in review because the
/// diff reads as a test being updated beside its code. Here the same paste moves the browser's
/// expectation too, so renaming a member on one side reddens the other — which is the whole point.
/// </para>
/// <para>
/// <b>A rotation is the widest surface in the product for that failure to arrive on.</b> Fourteen
/// messages across four routes, five of them arms of one body differing from each other by one member;
/// the client that drives it holds the only copy of a generation half the account is already sealed
/// under; and the run is long enough that a skew is discovered in the middle of one rather than at the
/// start.
/// </para>
/// <para>
/// <b>Every case is a pin and will be green on arrival.</b> A manufactured red is the only evidence any
/// line of it works, and what each one catches is named in its own remarks.
/// </para>
/// <para>
/// <b>NO WIDTHS, AND THE ABSENCE IS A CASE RATHER THAN A COMMENT.</b> Every binary member below is one
/// of two values whose width is already frozen in <c>account-keys-wire-v1.json</c> and held against a
/// server constant by <see cref="AccountKeyWireContractTests.ExactWidth_IsWhatTheServerConstantHolds" />.
/// A second copy of 158 in the rotation vector would be one fact able to disagree with itself, and the
/// disagreement would be silent, because each file would go on agreeing with the suite that reads it.
/// <see cref="TheKeyRotationVector_CarriesNoWidths" /> is what stops one arriving.
/// </para>
/// <para>
/// <b>Why the wire spelling comes from the serializer rather than from a transform written here.</b>
/// The records are PascalCase and the wire is camelCase; a hand-rolled conversion would agree with
/// itself while disagreeing with what ships. <see cref="WireOptions" /> is
/// <see cref="JsonSerializerDefaults.Web" /> plus exactly the two converters <c>Program.cs</c> adds,
/// and the names are read off the resulting <see cref="JsonTypeInfo" /> — the same metadata the
/// serializer writes a body from, so a <c>[JsonPropertyName]</c> or a <c>[JsonIgnore]</c> on any member
/// is reflected here without this file knowing those attributes exist. The gap that leaves is the one
/// <see cref="AccountKeyWireContractTests" /> states: a naming policy added to <c>Program.cs</c>
/// tomorrow would change the wire and not this file, and what closes it is the browser reading the same
/// artifact.
/// </para>
/// <para>
/// <b>Why this assembly and not the unit tier.</b> Twelve of the fourteen records are nested inside
/// <see cref="KeyRotationEndpoints" /> and <c>UnitTests.csproj</c> deliberately holds no reference to
/// <c>Api</c> — an absence that is a pinned row in <c>ProjectReferenceGraphTests</c>, so adding one
/// there reddens a test rather than merely contradicting a comment. Nothing below touches PostgreSQL;
/// it lives here for the same reason <see cref="ClientKeyCustodyTests" /> does.
/// </para>
/// <para>
/// <b>Private nested records are reachable, and it was checked rather than assumed.</b> All twelve
/// endpoint records are <c>private sealed record</c> inside a public static class. Reflection's
/// discovery is not access-checked: <c>GetNestedTypes(BindingFlags.NonPublic)</c> returns them, and
/// <see cref="JsonSerializerOptions.GetTypeInfo" /> resolves properties on them, provided the options
/// carry a resolver — which <see cref="JsonSerializerOptions.MakeReadOnly(bool)" /> supplies, exactly
/// as the runtime does on first use.
/// </para>
/// </remarks>
public sealed class KeyRotationWireContractTests
{
    /// <summary>The message names this file binds, and the whole set the artifact may carry.</summary>
    /// <remarks>
    /// <para>
    /// Kept beside the cases rather than inside them so
    /// <see cref="Messages_AreExactlyTheSetThisFileBinds" /> can compare against the same fourteen names
    /// the cases below use, instead of a second list able to drift from them.
    /// </para>
    /// <para>
    /// <b>The chunk's five arms are five entries and not one, which is the artifact's own rule.</b> Two
    /// pairs of them carry identical member sets today, and what they have in common is an accident of
    /// which columns each table holds — <c>accounts</c> and <c>payees</c> carry a required name,
    /// <c>categories</c> and <c>category_groups</c> carry a note beside it, <c>transactions</c> carries
    /// only the note. Folded into one entry, a column added to one table would either widen all five or
    /// redden a comparison against four tables it says nothing about. They share a traversal below
    /// because the traversal really is one walk; they do not share a list.
    /// </para>
    /// </remarks>
    private static readonly string[] BoundMessages =
    [
        "beginRotationRequest",
        "rotationSealRequest",
        "keyRotationBegunResponse",
        "rotationInventory",
        "resealChunkRequest",
        "resealedAccountEntry",
        "resealedPayeeEntry",
        "resealedCategoryGroupEntry",
        "resealedCategoryEntry",
        "resealedTransactionEntry",
        "completeRotationRequest",
        "keyRotationStateResponse",
        "stagedRotationResponse",
        "stagedSealResponse",
    ];

    /// <summary>
    /// The serializer configuration the API serves these messages under.
    /// </summary>
    /// <remarks>
    /// <b>Reconstructed rather than read off a running host</b>, because standing the API up costs a
    /// PostgreSQL database for a question that has nothing to do with one. <c>Program.cs</c> calls
    /// <c>ConfigureHttpJsonOptions</c> and adds a <see cref="JsonStringEnumConverter" /> and an
    /// <see cref="OptionalJsonConverterFactory" /> and nothing else — in particular no
    /// <c>PropertyNamingPolicy</c>, so the camelCase below is <see cref="JsonSerializerDefaults.Web" />'s
    /// own. Both converters are added here even though neither can affect a property name: a converter
    /// that later did would then be visible to this file instead of invisible to it. It is rebuilt here
    /// rather than borrowed from <see cref="AccountKeyWireContractTests" />, whose copy is
    /// <c>private</c>, because widening that field to share it would make one test class's internals
    /// another's dependency for two lines of construction.
    /// </remarks>
    private static readonly JsonSerializerOptions WireOptions = BuildWireOptions();

    /// <summary>
    /// <c>POST /api/me/key-rotation</c>'s body binds exactly the nine members the artifact names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Five of the nine are the re-authentication gate's and are byte-identical to the erasure's, the
    /// revocation's and the code regeneration's, members and all.</b> A caller comparing the four gates
    /// must learn nothing from a difference between them, so a renamed member here — or a
    /// <see langword="null" /> user handle coerced to an empty string on the way past — would make one
    /// gate answer differently from the other three for a caller holding a stolen handle. One spelling on
    /// each side is what holds it, and this case plus the browser reading the same list is that spelling.
    /// </para>
    /// <para>
    /// <b>The direction that matters here is server-minus-contract.</b> There is no <c>userId</c> and no
    /// account of any spelling on this body and neither may ever be added: the only identity a rotation
    /// acts on is the session's, and an account a caller could name would be somebody else's keys moved
    /// by a request that authenticated as a different person — over the one write in the product that
    /// nothing can put back. A subset check would pass on the day one arrived.
    /// </para>
    /// </remarks>
    [Test]
    public async Task BeginRotationRequest_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members("beginRotationRequest");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(BeginRequest());

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// One seal, as the wire carries it in, binds two members and no third.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reached through <c>BeginRotationRequest.Seals</c> rather than found by name, and the difference
    /// is the whole of what this case is worth.</b> A lookup for a nested type called
    /// <c>SealRequest</c> passes against an <em>orphan</em>: add a <c>SealRequestV2</c>, point the begin's
    /// <c>Seals</c> at it, leave the old record where it is, and the case stays green having bound a
    /// record no route reads. The element type of the member the route actually binds cannot do that.
    /// <see cref="KeyRotationEndpoints_DeclaresExactlyTheWireRecordsTheContractNames" /> closes the other
    /// half — the abandoned record left sitting beside the new one.
    /// </para>
    /// <para>
    /// <b>The pair is the whole of what a seal honestly is: which factor, and the value encapsulated to
    /// it.</b> A <c>wrappedPrivateKey</c> beside them is refused — a rotation moves the ACCOUNT's two keys
    /// and never a factor's own private half, which stays wrapped under the key-encryption key that
    /// factor derives and belongs to the route that serves factors. A public key is refused for the
    /// reason <c>accountKeyEntry</c> refuses one: what must be unforgeable is the SET, named in one
    /// authenticated blob.
    /// </para>
    /// <para>
    /// <b>What the traversal cannot see is that <c>seals</c> is an ARRAY, and that is a rule.</b> A JSON
    /// object keyed on the factor makes a repeated factor id unconstructible on the wire — the binder
    /// drops the repeat, last wins — so a begin naming twelve factors in thirteen seals would arrive as
    /// twelve, satisfy the handler's set comparison against the account's twelve live factors perfectly,
    /// answer 200, and leave the account one seal short of what its client believed it sent. Nothing
    /// about a member name says so; <c>ElementOf</c>'s single-generic-argument requirement is the nearest
    /// this file comes, and it is a throw rather than an assertion.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RotationSealRequest_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members("rotationSealRequest");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(ElementOf(BeginRequest(), "Seals"));

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// The whole answer of the begin binds exactly the two members the artifact names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An Application record served straight out, which is why it is reached through the handler
    /// rather than named.</b> The route does <c>TypedResults.Ok(begun)</c> over whatever
    /// <see cref="BeginKeyRotationHandler" /> returned, so the return type of that method is what a
    /// client receives; <c>typeof(KeyRotationBegun)</c> would compile and would go on compiling beside a
    /// <c>KeyRotationBegunV2</c> the handler had been repointed at. Reaching the route's own delegate
    /// would need an <c>EndpointDataSource</c>, and therefore a host, for a question with nothing to do
    /// with one — the handler's signature is the closest thing to the route that costs nothing.
    /// </para>
    /// <para>
    /// <b>It echoes neither the run nor the manifest, and a reader will try to add one for symmetry with
    /// <c>stagedRotationResponse</c>.</b> The caller minted the first and sent the other two a moment ago,
    /// so repeating them adds nothing and invites a client to read the echo as confirmation that the
    /// server agreed — which on this path it would, because the values are stored verbatim or the request
    /// was refused. There is also no status member: a begin that returned is a begin that staged, so a
    /// flag saying so is one nothing could ever set to false.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationBegunResponse_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members("keyRotationBegunResponse");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(BegunResponse());

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// The denominator a client divides its progress by binds six counts and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one place on this path where somebody reaches for "and the names of the payees, so
    /// the screen can say which one it is on".</b> Every narrative column in the product is a sealed
    /// envelope the server cannot open, so the useful version of that member cannot exist and the useless
    /// version would ship ciphertext to a progress bar. <c>BeginKeyRotationHandlerTests</c> holds the same
    /// closure against the type; this case holds it against the <em>wire</em>, which is the half that
    /// would otherwise be greened by the browser quietly learning to ignore a seventh member.
    /// </para>
    /// <para>
    /// <b>Six named members rather than a dictionary keyed on a table name.</b> A map lets a table go
    /// missing with nothing failing, and the client reads each of these as its own progress bar — so a
    /// member the response forgot is a rotation that reports itself finished with a table still to go.
    /// Reached off <c>KeyRotationBegun.Inventory</c> for the orphan reason the seal case gives.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RotationInventory_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members("rotationInventory");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(TypeOf(BegunResponse(), "Inventory"));

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// <c>POST /api/me/key-rotation/chunks</c>'s body binds the run and exactly five arms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Five arms, and the absent sixth is a decision rather than a gap.</b> A budget arm would need
    /// <c>rotation_id</c> on the <c>budgets</c> <c>GRANT UPDATE</c> column list, where the requirement is
    /// <c>name</c> and no other column; <c>Budget.ResealName</c> is <see langword="internal" />, so on
    /// this side a sixth arm is a compile error rather than a runtime <c>42501</c>. On the browser's side
    /// the absence is held by nothing but this list, which is why it is a list.
    /// </para>
    /// <para>
    /// <b>No count and no account, in the direction a subset check would miss.</b> There is no "rows in
    /// this chunk" and no "rows remaining" — the begin published a per-table inventory and a chunk budget,
    /// and a count here would be a second denominator able to disagree with it, which on a screen is a
    /// progress bar that never reaches the end. No account is named and none may be: the rows a chunk may
    /// reach are the ambient budget's, resolved from the session.
    /// </para>
    /// <para>
    /// <b>The set compared is the set the client SENDS, which is all five, and not the set any one
    /// request carries.</b> A missing array binds as an empty arm rather than being refused — most chunks
    /// of a real rotation carry one or two — so inferring the member set from one request's contents would
    /// bind a different list every time.
    /// </para>
    /// </remarks>
    [Test]
    public async Task ResealChunkRequest_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members("resealChunkRequest");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(ChunkRequest());

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// Each of the chunk's five arms carries entries binding exactly the members its own list names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Five rows and never one case over a folded list, which is the artifact's own rule restated as
    /// arguments.</b> Two pairs of these carry identical member sets today and the identity is an accident
    /// of which columns each table holds. Folded into one comparison, a column added to
    /// <c>categories</c> alone would either widen the expectation for four tables it says nothing about or
    /// redden against them. Five <c>[Arguments]</c> rows keep five messages five while sharing the one
    /// traversal that really is shared — the walk from the chunk body down into an arm.
    /// </para>
    /// <para>
    /// <b>The arm's member name is passed rather than the record's, which is what makes each row a claim
    /// about the route.</b> A row naming <c>ResealedCategoryRequest</c> would pass against an orphan the
    /// body no longer points at; a row naming <c>Categories</c> measures whatever that arm really carries,
    /// whatever it is called.
    /// </para>
    /// <para>
    /// <b>What the five lists hold that no record shape does.</b> <c>id</c> is the identifier this server
    /// rendered rather than base64url text — the one place a chunk parts company with the create bodies it
    /// otherwise resembles, because a chunk names a row that already exists and nothing is sealed against
    /// what this body says about it. <c>nameKey</c> rides beside <c>name</c> because a rotation replaces
    /// the INDEX key as well as the content key, so a client that re-sealed the envelope and carried the
    /// old index would write rows that are perfectly readable and findable by nothing. And the transaction
    /// arm's absent <c>name</c> is pinned here rather than left to its record, because a <c>name</c> added
    /// to that record would bind nowhere and redden nothing on this server.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("resealedAccountEntry", "Accounts")]
    [Arguments("resealedPayeeEntry", "Payees")]
    [Arguments("resealedCategoryGroupEntry", "CategoryGroups")]
    [Arguments("resealedCategoryEntry", "Categories")]
    [Arguments("resealedTransactionEntry", "Transactions")]
    public async Task ResealedEntry_BindsExactlyTheMembersTheContractNames(string message, string arm)
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members(message);

        // Act
        IReadOnlyList<string> bound = WireMemberNames(ElementOf(ChunkRequest(), arm));

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// <c>POST /api/me/key-rotation/completion</c>'s body binds one member and no second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case the artifact says a subset comparison would not notice.</b> The body is
    /// specified never to grow a second member, and every other candidate is refused: the staged manifest,
    /// the generation it is filed at and the value each factor adopts were all fixed by the begin and are
    /// on file already. Carried again here they would be a second statement of the same values, able to
    /// disagree with the staged one at the one moment a disagreement cannot be undone.
    /// </para>
    /// <para>
    /// <b>The reason the shape is pinned rather than inferred from the handler.</b> A <c>manifest</c>, a
    /// <c>rotationEpoch</c> or a <c>seals</c> array added beside <c>rotationId</c> would BIND on this
    /// server, be forwarded nowhere, and redden nothing — the direction that catches it is
    /// server-minus-contract, which is why it is asserted first.
    /// </para>
    /// <para>
    /// <b>There is no completion RESPONSE message and its absence is outside this file's reach.</b> The
    /// route answers 204 carrying nothing — not a body member, not an <c>ETag</c>, not a
    /// <c>Location</c>, not a header of its own — because a promoted generation returned from here is a
    /// number a client could advance its rotation-epoch record from having judged nothing.
    /// <c>KeyRotationCompletionEndpointTests</c> is what sweeps for one; a member-set comparison over a
    /// record that does not exist cannot.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CompleteRotationRequest_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members("completeRotationRequest");

        // Act
        IReadOnlyList<string> bound =
            WireMemberNames(NestedRecord(typeof(KeyRotationEndpoints), "CompleteRotationRequest"));

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// <c>GET /api/me/key-rotation</c>'s body binds one member, and the member is the point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A wrapper rather than the staged run served bare.</b> A bare body would have to spell "nothing
    /// in flight" as a literal <c>null</c> document — which a client has to guard before it may read
    /// anything — or as <c>{}</c>, which is indistinguishable from a run whose members all went missing. A
    /// named member that is present and <see langword="null" /> says both halves: the shape did not
    /// change, and there is no run.
    /// </para>
    /// <para>
    /// <b>No second member saying whether a run is in flight.</b> The presence of <c>rotation</c> IS that
    /// fact, and a flag beside it would be one statement able to disagree with the other — on the one read
    /// a client consults when it has already lost track of what it was doing. That absence is
    /// server-minus-contract again, and nothing but this direction would catch it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationStateResponse_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members("keyRotationStateResponse");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(StateResponse());

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// One staged run, as a resuming client needs it back, binds exactly seven members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It carries the epoch and the manifest where the begun response deliberately carries neither,
    /// and the asymmetry is the whole difference between the two answers.</b> A begin's caller sent those
    /// values a moment ago, so repeating them invites it to read the echo as agreement. THIS caller sent
    /// nothing: it is a client that lost the run to a reload, and these are the values it no longer has.
    /// Two cases asserting opposite absences over the same three names is what keeps the asymmetry from
    /// being tidied into symmetry.
    /// </para>
    /// <para>
    /// <b>No member may carry progress</b> — not "rows remaining", not a percentage, not a list of stamped
    /// row ids. <c>inventory</c> is the denominator and the client holds the numerator; a count computed
    /// here would be a progress bar able to disagree with the completeness gate, which is the one number
    /// that decides whether the run may be completed. <c>maxChunkBytes</c> is republished rather than left
    /// out, because a resuming client needs it exactly as the client that began the run did.
    /// </para>
    /// <para>
    /// Reached off <c>KeyRotationStateResponse.Rotation</c> rather than by name, for the orphan reason the
    /// seal case gives. The member is declared nullable, which is a reference-type annotation and not a
    /// different <see cref="Type" />, so the property type is the record itself.
    /// </para>
    /// </remarks>
    [Test]
    public async Task StagedRotationResponse_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members("stagedRotationResponse");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(StagedRotation());

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// One staged seal, as the wire carries it back, binds two members and no third.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mirror of <c>rotationSealRequest</c>, member for member, and the two lists are kept separate
    /// all the same.</b> The two RECORDS are separate on this server and a reflection test binds records,
    /// so a rename on one of them reddens against its own list and leaves the other standing — which is
    /// the report a reader needs. A single case over a shared list would say "a seal changed" and leave
    /// the direction to be worked out.
    /// </para>
    /// <para>
    /// <b>No third member.</b> A <c>credentialId</c> is the join <c>account-keys.md</c> deliberately
    /// withholds. A <c>wrappedPrivateKey</c> belongs to the factor rather than to a run and is on the
    /// route that serves factors. A per-entry stamp or instant would be a second statement of what
    /// <c>stagedRotationResponse.startedAtUtc</c> already says once.
    /// </para>
    /// <para>
    /// <b>What this case does not hold is which factors appear.</b> The entry set is what THIS RUN staged
    /// a value for, which is not always the set of factors the account holds now, and a factor with no
    /// staged seal is left out rather than carried with a <see langword="null" /> value or filled in from
    /// its live row. That is a fact about the set; this file pins names.
    /// </para>
    /// </remarks>
    [Test]
    public async Task StagedSealResponse_BindsExactlyTheMembersTheContractNames()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.Members("stagedSealResponse");

        // Act
        IReadOnlyList<string> bound = WireMemberNames(ElementOf(StagedRotation(), "Seals"));

        // Assert
        await AssertSetsAgreeAsync(bound, contract);
    }

    /// <summary>
    /// <see cref="KeyRotationEndpoints" /> declares exactly the twelve wire records the artifact names,
    /// and no thirteenth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The half a traversal cannot reach: a record that still exists after nothing points at it.</b>
    /// Every case above arrives at its subject through the member that carries it rather than by name, so
    /// a repointed member takes the case with it — but a <c>StagedSealResponseV2</c> added beside the
    /// record it replaced leaves the old declaration standing, described by the artifact, read by nobody,
    /// and inherited by whoever next reaches for that name. This is the case that reddens on the extra
    /// declaration.
    /// </para>
    /// <para>
    /// <b>Twelve and not fourteen, and the two missing ones are the reason this census is possible at
    /// all.</b> <c>KeyRotationBegun</c> and <c>RotationInventory</c> are Application records served
    /// straight out — six counts restated at the edge would be six numbers able to disagree with the six
    /// <see cref="RotationInventory" /> declares — so they are declared elsewhere and are bound above
    /// through the handler instead. <c>AccountKeyEndpoints</c> gets a census of two for the same shape of
    /// reason and <c>RegistrationEndpoints</c> gets none, because it declares response records the
    /// artifact deliberately does not carry.
    /// </para>
    /// <para>
    /// <b>Transcribed, never derived.</b> The expectation is twelve written strings rather than
    /// <c>nameof</c>: a pin spelled with <c>nameof</c> does not survive the deletion it exists to notice —
    /// it becomes a compile error saying "a symbol is missing" rather than a failure saying "a record this
    /// wire contract describes is gone". It is the rule <c>EnvelopeSuiteCensusTests</c> keeps for its own
    /// pin table.
    /// </para>
    /// </remarks>
    [Test]
    public async Task KeyRotationEndpoints_DeclaresExactlyTheWireRecordsTheContractNames()
    {
        // Arrange
        string[] expected =
        [
            "BeginRotationRequest",
            "CompleteRotationRequest",
            "KeyRotationStateResponse",
            "ResealChunkRequest",
            "ResealedAccountRequest",
            "ResealedCategoryGroupRequest",
            "ResealedCategoryRequest",
            "ResealedPayeeRequest",
            "ResealedTransactionRequest",
            "SealRequest",
            "StagedRotationResponse",
            "StagedSealResponse",
        ];

        // Act
        string[] declared =
        [
            .. typeof(KeyRotationEndpoints)
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
    /// The artifact names exactly the fourteen messages this file binds, in both directions.
    /// </summary>
    /// <remarks>
    /// <b>The same both-directions rule one level up, and it is not decoration.</b> A fifteenth message
    /// added to the artifact with no case beneath it would be a document nobody reads while looking
    /// exactly like coverage, and a message deleted from it would take its case's expectation with it
    /// silently if the case were the only reader. Compared as a set, which is what
    /// <c>CollectionOrdering.Any</c> would give — but written as two explicit differences so the failure
    /// names the message rather than reporting two counts.
    /// </remarks>
    [Test]
    public async Task Messages_AreExactlyTheSetThisFileBinds()
    {
        // Arrange
        IReadOnlyList<string> contract = WireContract.KeyRotation.MessageNames();

        // Act
        string[] unbound =
            [.. contract.Except(BoundMessages, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] missing =
            [.. BoundMessages.Except(contract, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // Assert
        await Assert.That(unbound).IsEmpty();
        await Assert.That(missing).IsEmpty();
    }

    /// <summary>
    /// The rotation artifact carries no widths, and the account-keys artifact still does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An absence asserted rather than described, because the number that would arrive is one this
    /// suite would go on agreeing with.</b> Every binary member the rotation vector names is one of two
    /// values already frozen next door — <c>encapsulatedAccountKeys</c> at exactly 158 bytes, and the
    /// manifest's 29-to-4096 band, which the rotation carries under two names. A second copy of either
    /// number added here would be one fact able to disagree with itself, and the disagreement would be
    /// silent: each file would go on agreeing with the suite that reads it, and nothing in either suite
    /// compares the two files to each other.
    /// </para>
    /// <para>
    /// <b>The sibling half is asserted beside it rather than trusted.</b> Without it this case passes just
    /// as well on the day somebody deletes the account-keys artifact's widths — which is the change that
    /// would leave 158 pinned to no server constant at all, since
    /// <see cref="AccountKeyWireContractTests.ExactWidth_IsWhatTheServerConstantHolds" /> reads its
    /// expectation from there.
    /// </para>
    /// <para>
    /// <b><c>CarriesWidths</c> and not <c>WidthNames</c> throwing.</b> The throw says "this file has no
    /// widths" identically for a file specified to have none and for a file whose widths were deleted, and
    /// those are opposite reports.
    /// </para>
    /// </remarks>
    [Test]
    public async Task TheKeyRotationVector_CarriesNoWidths()
    {
        // Arrange
        WireContract rotation = WireContract.KeyRotation;
        WireContract accountKeys = WireContract.AccountKeys;

        // Act
        bool rotationCarriesWidths = rotation.CarriesWidths();
        bool accountKeysCarriesWidths = accountKeys.CarriesWidths();

        // Assert
        await Assert.That(rotationCarriesWidths).IsFalse();
        await Assert.That(accountKeysCarriesWidths).IsTrue();
    }

    /// <summary>
    /// Both differences between a record's bound members and the artifact's list, asserted as empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two differences and never one comparison.</b> A subset check passes on a side that has grown a
    /// member the other does not bind, which is the live failure mode here rather than a tidiness rule:
    /// <c>completeRotationRequest</c> is specified never to grow a second member and
    /// <c>stagedSealResponse</c> never to grow a third. It is also not <c>IsEquivalentTo</c>: that would
    /// be the right ordering semantics — a set comparison is exactly what <c>CollectionOrdering.Any</c>
    /// gives — but its failure reports two collections and leaves a reader to diff them, where an
    /// empty-difference assertion prints the offending member on its own.
    /// </para>
    /// <para>
    /// The server-minus-contract direction is asserted first on purpose: a rename produces one entry in
    /// each direction, and the more useful sentence is the spelling the server has just started emitting.
    /// </para>
    /// <para>
    /// Deliberately a second copy of <see cref="AccountKeyWireContractTests" />' helper of the same name
    /// rather than a shared one. It is six lines, and a helper lifted into a file both test classes
    /// reference makes one class's failure message another class's dependency — the artifact is what the
    /// two suites are supposed to share, not their assertion plumbing.
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

    /// <summary>The begin's request record, as the route binds it.</summary>
    private static Type BeginRequest() =>
        NestedRecord(typeof(KeyRotationEndpoints), "BeginRotationRequest");

    /// <summary>The chunk's request record, as the route binds it.</summary>
    private static Type ChunkRequest() =>
        NestedRecord(typeof(KeyRotationEndpoints), "ResealChunkRequest");

    /// <summary>The resume read's response record, as the route returns it.</summary>
    private static Type StateResponse() =>
        NestedRecord(typeof(KeyRotationEndpoints), "KeyRotationStateResponse");

    /// <summary>The staged run inside that response, reached off the member that carries it.</summary>
    private static Type StagedRotation() => TypeOf(StateResponse(), "Rotation");

    /// <summary>
    /// What a begin answers, read off the handler whose return value the route hands straight out.
    /// </summary>
    /// <remarks>
    /// <b>The one subject in this file reached through a method rather than a property</b>, because the
    /// begin's answer is an Application record and the route's own delegate is unreachable without an
    /// <c>EndpointDataSource</c>. <c>Task&lt;T&gt;</c>'s single generic argument is taken rather than the
    /// awaitable pattern walked: a handler that stopped returning a <c>Task&lt;T&gt;</c> is a change a
    /// reader should meet as a throw here rather than as a silently different subject.
    /// </remarks>
    private static Type BegunResponse()
    {
        MethodInfo handle = typeof(BeginKeyRotationHandler).GetMethod("HandleAsync")
            ?? throw new InvalidOperationException(
                "'BeginKeyRotationHandler' declares no 'HandleAsync'. The wire contract binds what that "
                + "method returns, because the route hands it straight out; a case that cannot find the "
                + "method has bound nothing.");

        Type[] arguments = handle.ReturnType.GetGenericArguments();

        return arguments.Length == 1
            ? arguments[0]
            : throw new InvalidOperationException(
                $"'BeginKeyRotationHandler.HandleAsync' returns '{handle.ReturnType}', which carries "
                + $"{arguments.Length} type arguments rather than one. The wire contract expects the "
                + "record the begin answers with.");
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
    /// The element type of <paramref name="owner" />'s <paramref name="member" />, or a throw naming it.
    /// </summary>
    /// <remarks>
    /// <b>What turns a lookup by name into a claim about a route.</b> A record found by name can be an
    /// orphan — declared, described, and pointed at by nothing — while the member that actually crosses
    /// the wire carries some other type entirely. Read off the member, the subject is whatever the route
    /// really serves. The single generic argument is taken rather than the interface walked: every list
    /// member in these records is declared <c>IReadOnlyList&lt;T&gt;</c>, and a member that stopped being
    /// one — an object keyed on the row, which is the shape these arms exist not to be — is a wire change
    /// a reader should meet as a throw here rather than as a silently different element type.
    /// </remarks>
    private static Type ElementOf(Type owner, string member)
    {
        PropertyInfo property = Member(owner, member);
        Type[] arguments = property.PropertyType.GetGenericArguments();

        return arguments.Length == 1
            ? arguments[0]
            : throw new InvalidOperationException(
                $"'{owner.FullName}.{member}' is '{property.PropertyType}', which carries "
                + $"{arguments.Length} type arguments rather than one. The wire contract expects a list "
                + "of one row type.");
    }

    /// <summary>
    /// The declared type of <paramref name="owner" />'s <paramref name="member" />.
    /// </summary>
    /// <remarks>
    /// <see cref="ElementOf" /> for a member carrying one row type; this for a member carrying one
    /// record. The nullable annotation on <c>KeyRotationStateResponse.Rotation</c> is not part of the
    /// <see cref="Type" />, so the record arrives here whether the member may be
    /// <see langword="null" /> or not.
    /// </remarks>
    private static Type TypeOf(Type owner, string member) => Member(owner, member).PropertyType;

    private static PropertyInfo Member(Type owner, string member) =>
        owner.GetProperty(member)
        ?? throw new InvalidOperationException(
            $"'{owner.FullName}' declares no member named '{member}'. The wire contract binds the type "
            + "this member carries, and a case that cannot find the member has bound nothing.");

    /// <summary>
    /// The nested type <paramref name="name" /> of <paramref name="owner" />, or a throw naming it.
    /// </summary>
    /// <remarks>
    /// <b>Non-public, because all twelve endpoint records are <c>private</c>.</b> Reflection's discovery
    /// is not access-checked, so the declarations stay private — a wire record is the endpoint's own
    /// business and nothing outside it may construct one — while a test can still read their shape. The
    /// throw is what makes renaming a record itself loud: a null here would otherwise be a
    /// <see cref="NullReferenceException" /> from inside a serializer call two frames away.
    /// </remarks>
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
