using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace UnitTests;

/// <summary>
/// The CON-005 gate: no envelope-budgeting computation reads a narrative column.
/// </summary>
/// <remarks>
/// <para>
/// CON-005 says it in the requirement's own words — "assignments, activity, available and 'to
/// allocate' are arithmetic over amounts, dates and identifiers only. This holds today; the
/// constraint exists so the envelope layer cannot break it later." The constraint is therefore a
/// guard placed <i>before</i> its subject exists, and that is the single most important thing to
/// understand about this file.
/// </para>
/// <para>
/// <b>There is no envelope-budgeting code in this repository, so the gate is over the empty set and
/// is green forever.</b> That is stated here, asserted next door by
/// <see cref="EnvelopeBudgeting_DeclaresNoTypesYet" />, and it is the reason the eleven synthetic
/// cases this file ships exist — ten of them above that pin and the eleventh, the escape scan,
/// below it. A gate that cannot object to anything is indistinguishable from a gate that
/// cannot object at all, and this repository has one convention for that: permanent negative
/// controls, shipped beside the guard, proving the detector can fail against subjects that are not
/// production's. <c>OwnershipKeyImmutabilityTests</c> and <c>DataInventoryCoverageTests</c> make the
/// same call and argue it at their own declarations. Ten of them prove the <i>detector</i>;
/// <see cref="EscapeScan_UnderAMistypedPrefix_ReportsTheBudgetingFixturesItWouldOtherwiseCover" /> is
/// the eleventh and proves something else — that the gate is still pointed at its subject — which is
/// a different way for a guard over an empty set to be worthless and is argued at
/// <see cref="BudgetingSegment" />.
/// </para>
/// <para>
/// <b>The subject is a namespace convention, deliberately, and not an opt-in marker.</b> Envelope
/// computations live under <c>Application.Budgeting</c> and <c>Domain.Budgeting</c>. A marker
/// interface or attribute would read better and fail worse: a forgotten marker is silent, and
/// <c>CLAUDE.md</c> already argues that polarity where it matters most — "a forgotten opt-in here
/// would hand budget content to a locked session with nothing going red". A namespace is not
/// forgettable in the same way, because a file has to be filed somewhere and the folder is where a
/// reviewer looks. The cost is a real blind spot, made visible by
/// <see cref="Detector_IsBlindToATypeOutsideTheNamespace" /> rather than left for somebody to
/// discover.
/// </para>
/// <para>
/// <b>Three things were measured before this was written, and each rules out a simpler design.</b>
/// A signature-only census does not work: a computation that <i>reads</i> a narrative property
/// declares nothing — it touches one in a method body, through a projection or a predicate. A naive
/// IL body scan does not work either, and it fails <i>silently</i>: <c>row.Note</c> compiles to a
/// call to <c>BudgetRow::get_Note</c>, whose declaring type is <c>BudgetRow</c>, so the target type
/// appears only in the member's <i>signature</i> and never as a token in the body.
/// </para>
/// <para>
/// <b>What that mutation costs is measured here rather than described, because the honest number is
/// not zero and an earlier draft of this paragraph said it was.</b> The first measurement was over a
/// throwaway probe assembly holding property-read shapes only, where the report really was the empty
/// string; the number then attached itself to the wrong noun. Re-measured against <i>this</i> fixture
/// assembly, by calling <see cref="NarrativeReaders.In" /> over <c>UnitTests.dll</c> under the
/// fixture prefix: as shipped it reports 17 touches over 6 computations. Delete the signature arm
/// from <see cref="NarrativeReaders.NarrativeTouchOf" /> and it reports 11 over 4, reddening
/// <b>four</b> cases —
/// <see cref="Detector_ReportsAComputationThatTouchesANarrativeFieldInAMethodBody" />,
/// <see cref="Detector_ReportsATouchExpressedOnlyAsAnExpressionTree" />,
/// <see cref="Detector_ReportsACrossAssemblyReadWhoseSignatureNamesTheType" /> and the first
/// assertion of <see cref="Detector_IsBlindToATypeOutsideTheNamespace" />, which files the same
/// body-read violation one namespace over. Take signature decoding away from the declaration half
/// too — a body-token scan reading declaring types and nothing else — and it reports 7 touches over
/// <b>3 of the 6</b>, not none: what survives is exactly the three shapes whose token owner either is
/// the narrative type or names it structurally — the cross-assembly read of
/// <c>NarrativeField::get_Envelope</c>, the <c>List&lt;NarrativeField&gt;</c> reached as a
/// <c>TypeSpecification</c>, and the anonymous type the projection builds. The property read is the
/// shape that vanishes, which is the whole of what the signature half buys and is why it is
/// load-bearing without being the only half.
/// </para>
/// <para>
/// What works is body tokens <b>plus</b> decoding each referenced member's signature, and the
/// declaration half kept beside it for the members no IL reads. Both halves are load-bearing and
/// each has a case that reddens when it is removed: deleting the two declaration loops in
/// <see cref="NarrativeReaders.In" /> reddens
/// <see cref="Detector_ReportsAComputationDeclaringANarrativeFieldMember" /> and nothing else —
/// measured, one failure in a run of 932.
/// </para>
/// <para>
/// <b>The same read is a different token depending on which assembly it crosses, and the fixtures
/// have to reach both.</b> Inside one assembly a property read is a <c>MethodDefinition</c> token;
/// across an assembly boundary it is a <c>MemberReference</c>, decoded by a different arm. Every
/// fixture here lives beside the detector, so for a while the whole suite exercised the first arm
/// only — while the real gate runs on the second, because <c>Application</c> reads <c>Domain</c>.
/// Measured: a detector with signature decoding removed from the <c>MemberReference</c> arm
/// <i>alone</i> passed all eight cases that existed then.
/// <see cref="Detector_ReportsACrossAssemblyReadWhoseSignatureNamesTheType" /> closes it by reading
/// <c>Domain.Accounts.Account.Name</c>, and the same mutation now reddens that case and nothing
/// else — the asymmetry is the evidence, because a mutation that reddens everything says nothing
/// about which arm anything covered.
/// </para>
/// <para>
/// <b>That was a class of gap rather than a missing case, so the other paths only a cross-assembly
/// or encoded reference can reach are covered too.</b> A member reference names the target either in
/// its signature or as its <i>owner</i> — <c>Account::get_Name</c> is the first and
/// <c>NarrativeField::get_Envelope</c> the second, two lines apart in <c>AccountDto.FromAccount</c>,
/// and a case each. A generic instantiation names it inside an encoded <c>TypeSpecification</c> and
/// nowhere else, which is a third handle kind and
/// <see cref="Detector_ReportsANarrativeTypeReachedOnlyThroughAGenericInstantiation" />. And the
/// comparison is ordinal equality on the full name rather than a substring test, held by
/// <see cref="Detector_AcceptsAComputationTouchingATypeWhoseNameMerelyStartsWithTheTarget" /> —
/// <c>Domain.Security.NarrativeFieldLimits</c> is a real type containing the target's whole name.
/// </para>
/// <para>
/// <b><see cref="NarrativeReaders.In" /> takes its assembly, its prefix and its target type as
/// parameters and must never reach for the production assemblies itself.</b> The call
/// <see cref="Infrastructure.Persistence.Inventory.DataInventoryCoverage.Compare" /> already made,
/// for the reason it states: a detector hardwired to production answers every control with
/// production's verdict, and production's verdict here is "clean" — so every control would pass
/// while checking nothing. The ten synthetic cases scan <c>UnitTests.dll</c> under a fixture prefix;
/// a hardwired implementation reddens on all ten at once.
/// </para>
/// <para>
/// <b>It lives in the test assembly rather than in <c>Infrastructure</c>, and the limits of that are
/// worth stating.</b> One test reads it, it knows nothing about the data inventory, and
/// <c>UnitTests</c> does not reference <c>Api</c> — a pinned row in
/// <c>ProjectReferenceGraphTests</c> — so a computation that ever appeared in a route delegate would
/// be outside this gate's reach as well as outside its namespace. Nothing puts one there today, and
/// <c>CompositionBoundaryTests</c> is what keeps persistence out of that layer; neither is a
/// substitute for this gate, and this gate is not a substitute for them.
/// </para>
/// <para>
/// <b>What it cannot see</b>, beyond the namespace limit: a narrative value reached through
/// <see langword="dynamic" /> or reflection, where no signature names the type; a
/// <c>calli</c> whose standalone signature carries it; and the <i>meaning</i> of a read — a
/// computation that touches a narrative field to pass it straight through to a response is reported
/// exactly like one that branches on it. The last is deliberate. CON-005 is about dependency, and a
/// judgement about which touches are innocent is one no scan can make, so the report names the touch
/// and a human decides.
/// </para>
/// <para>
/// <b>Three more, each measured against the shipped detector over a probe assembly built for the
/// purpose, and the first of the three is a decision rather than a gap.</b> <b>Delegation is not
/// followed</b>: a computation under the prefix that calls a helper filed elsewhere, where the
/// helper reads the narrative field and hands back a <see langword="decimal" />, is reported clean —
/// the scan is single-hop and builds no transitive closure. Formally the computation does read no
/// narrative field, and following the edge means a call graph plus a judgement about which member in
/// the chain <i>is</i> the computation, which is the judgement the paragraph above says no scan can
/// make. It is recorded here so the next reader meets it as a choice and not as a surprise; note the
/// near miss, measured beside it, that a helper whose own <i>signature</i> names the type <b>is</b>
/// reported at the call site, so only the shape that launders the type behind a row parameter
/// escapes. <b>An implemented interface's type arguments are not walked</b>: the declaration half
/// reads <c>GetFields()</c> and <c>GetMethods()</c> only, so <c>: INotes&lt;NarrativeField&gt;</c>
/// leaves neither a body token nor a member signature and is reported clean. A generic <i>base</i>
/// type is caught, and only incidentally — measured, the evidence is the base constructor call the
/// derived type's own <c>.ctor</c> emits, which an interface has no counterpart to.
/// <b>An attribute's type arguments are not walked</b> either.
/// </para>
/// <para>
/// <b>Two shapes a reader will assume are blind spots and which are measured working</b>, so nobody
/// spends an afternoon on them: an <see langword="async" /> body and a local function are both
/// reported. The async one is hoisted into a state-machine field, so the touch arrives as a
/// <c>FieldDefinition</c> on a compiler-generated type and the attribution walk files it under the
/// method's own type; the local function compiles to an ordinary method of the declaring type and is
/// walked with the rest. What the async case does <i>not</i> prove is that a local's type is visible
/// — a local variable lives in a standalone signature this scan never reads, so what is seen is
/// always the member read that filled it.
/// </para>
/// <para>
/// <b>Offenders are asserted as collections and written to the console as text.</b> TUnit's string
/// assertions truncate — measured in this repository — so a census reporting through a single
/// <c>string.Join</c> comparison names the first offender and hides the rest.
/// </para>
/// </remarks>
public sealed class EnvelopeBudgetingIsolationTests
{
    private const string FixturePrefix = "UnitTests.Fixtures.Budgeting";

    /// <summary>
    /// The namespace segment an envelope-budgeting namespace carries, written out on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not derived from <see cref="EnvelopeHomes" />, and that is the whole of what
    /// makes the escape check able to object to a mistyped prefix.</b> The floor this file shipped
    /// with was <c>TypesIn(assembly, prefix.Split('.')[0])</c> — computed from the very string whose
    /// spelling it was standing in for — so a prefix typed <c>Application.Bugdeting</c> beside a real
    /// computation filed under <c>Application.Budgeting</c> left the whole suite green. Measured. A
    /// floor that reads its own subject's spelling cannot object to that spelling being wrong, which
    /// is a shape rather than a slip: writing <c>prefix.Split('.')[^1]</c> here would reintroduce it
    /// exactly.
    /// </para>
    /// <para>
    /// <b>The limit, said plainly: a mutation that misspells this word <i>and</i> the prefix, the
    /// same way, is invisible.</b> Nothing inside one file can catch two edits that agree with each
    /// other, and this is a second independent statement rather than a proof. What it buys is that
    /// the single-edit version — the one somebody actually makes — is loud, and
    /// <see cref="EscapeScan_UnderAMistypedPrefix_ReportsTheBudgetingFixturesItWouldOtherwiseCover" />
    /// keeps the scan itself proved against a subject that is not production's.
    /// </para>
    /// <para>
    /// Compared as a whole dot-separated segment and never as a substring, which is why
    /// <c>UnitTests.Fixtures.OutsideBudgeting</c> — a namespace this file files a violation in on
    /// purpose — is not swept in by it. The control asserts that too.
    /// </para>
    /// </remarks>
    private const string BudgetingSegment = "Budgeting";

    private static string NarrativeTypeName => typeof(Domain.Security.NarrativeField).FullName!;

    private static string FixtureAssembly => typeof(EnvelopeBudgetingIsolationTests).Assembly.Location;

    /// <summary>
    /// Where an envelope-budgeting computation is allowed to live, one entry per assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subject of CON-005, stated as a namespace convention. Two entries and not three:
    /// <c>Infrastructure</c> is persistence and <c>Api</c> is transport, and neither computes what a
    /// person may spend — and <c>UnitTests</c> may not reference <c>Api</c> at all, which is a pinned
    /// row in <c>ProjectReferenceGraphTests</c>. A gate that grew a third home would be stated here,
    /// with the reason the layer acquired one.
    /// </para>
    /// <para>
    /// <b>What that exclusion is worth is a measurement, and it belongs beside the argument rather
    /// than under it.</b> Pointed at <c>Infrastructure.dll</c> under
    /// <c>Infrastructure.ReadServices</c>, the same detector reports <b>6</b> computations touching
    /// <see cref="Domain.Security.NarrativeField" /> today, over 11 types scanned — and that ring is
    /// where an envelope-balance query written to the existing repository pattern would most
    /// plausibly land. <c>Api</c> answers <b>0</b> over 38 types scanned. The argument above stands:
    /// persistence carries these values because it stores and returns them, which is not computing
    /// what a person may spend, and a gate over that ring would report six things nobody wants
    /// reported. But a reader deciding later whether to add a third home should meet the number, not
    /// discover it.
    /// </para>
    /// <para>
    /// <b>The assembly is named by a <see cref="Type" /> the compiler resolves rather than by a
    /// location string, so the floor that proves the file was read owes nothing to the prefix beside
    /// it.</b> Each anchor supplies three things at once: the path to hand a <see cref="PEReader" />,
    /// a namespace the walk must find types under, and a full name the walk must report — all three
    /// checked by the compiler, none of them a string this file could misspell. The earlier shape
    /// derived its floor from <c>Prefix</c>, which is the defect argued at
    /// <see cref="BudgetingSegment" />.
    /// </para>
    /// <para>
    /// The path is read from <see cref="System.Reflection.Assembly.Location" />, which is populated
    /// under this runner — measured, because the whole approach needs a file to hand to a
    /// <see cref="PEReader" /> and a single-file publish would return an empty string. If that ever
    /// changes, this file stops working loudly: <c>In</c> throws rather than reporting nothing.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<(Type Anchor, string Prefix)> EnvelopeHomes =>
    [
        (typeof(Application.DependencyInjection), "Application.Budgeting"),
        (typeof(Domain.Security.NarrativeField), "Domain.Budgeting"),
    ];

    /// <summary>
    /// Every type in the two rings whose namespace carries a <see cref="BudgetingSegment" /> segment
    /// and which does not live under the prefix this file pins for its ring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The anchor the two gate cases below were missing, and it reads the assemblies rather than
    /// the constant.</b> While the subject is empty neither gate can tell "there is no envelope
    /// budgeting code yet" from "the prefix points at nothing", and that distinction is the entire
    /// value of a guard shipped before its subject. This member answers it from the other side: a
    /// real <c>Application.Budgeting</c> type is visible here however <c>Prefix</c> is spelled, so a
    /// typo stops hiding a violation and starts causing one.
    /// </para>
    /// <para>
    /// Judged per home rather than against the union of both prefixes, which is the stronger of the
    /// two readings: a type declaring <c>Domain.Budgeting</c> inside <c>Application.dll</c> is a
    /// namespace saying something about its assembly that is not true, and this reports it. Nothing
    /// declares one today.
    /// </para>
    /// <para>
    /// A full name is compared against <c>prefix + "."</c> rather than a namespace against the prefix,
    /// because the walk already folds a compiler-generated type into its outermost declaring type and
    /// a full name renders the two together — a type sitting directly in the prefix and one nested
    /// three namespaces deeper both start with it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> BudgetingCodeOutsideItsPinnedPrefix()
    {
        List<string> escaped = [];

        foreach ((Type anchor, string prefix) in EnvelopeHomes)
        {
            escaped.AddRange(
                NarrativeReaders
                    .TypesWithNamespaceSegment(anchor.Assembly.Location, BudgetingSegment)
                    .Where(name => !name.StartsWith(prefix + ".", StringComparison.Ordinal)));
        }

        return [.. escaped.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The case that holds the signature-decoding half, and the first one written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subject declares no narrative member anywhere: a method taking rows and returning an
    /// <see langword="int" />, and a lambda inside it that reads one column. Against a body scan that
    /// reads only the declaring type of each token <i>this fixture</i> is reported not at all,
    /// because the target type appears only in <c>BudgetRow::get_Note</c>'s signature. Remove the
    /// signature decoding from <see cref="NarrativeReaders.NarrativeTouchOf" /> and this case
    /// reddens.
    /// </para>
    /// <para>
    /// <b>Not alone, and the count matters because an earlier draft said "the case that reddens" and
    /// meant the whole file.</b> Measured, that mutation reddens four: this one,
    /// <see cref="Detector_ReportsATouchExpressedOnlyAsAnExpressionTree" />,
    /// <see cref="Detector_ReportsACrossAssemblyReadWhoseSignatureNamesTheType" /> and the first
    /// assertion of <see cref="Detector_IsBlindToATypeOutsideTheNamespace" />, which is this same
    /// violation filed one namespace over. Nor does the report go empty: the scan still names 3 of
    /// the 6 fixture computations, none of them this one. The class remarks carry the numbers and how
    /// they were taken.
    /// </para>
    /// <para>
    /// Asserted through <c>Contains</c> over the whole fixture prefix rather than over a scan of one
    /// namespace, so it also states that the offender is named among its clean and its outside-the-
    /// gate neighbours rather than in isolation.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Detector_ReportsAComputationThatTouchesANarrativeFieldInAMethodBody()
    {
        // Arrange
        const string offender = "UnitTests.Fixtures.Budgeting.BodyRead.AvailableFromNotes";

        // Act
        IReadOnlyList<NarrativeTouch> touches =
            NarrativeReaders.In(FixtureAssembly, FixturePrefix, NarrativeTypeName);

        Console.WriteLine($"Touches: {string.Join(" | ", touches)}");

        // Assert
        await Assert.That(touches.Select(touch => touch.Computation)).Contains(offender);
    }

    /// <summary>
    /// The second shape, and the case that holds the declaration half.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The complement of the case above: a public field nothing reads and a method whose body is
    /// <c>ldnull; ret</c>. Neither leaves a token for the body walk to decode, so a detector built
    /// from body tokens alone reports this type clean while it holds the column outright. Delete the
    /// two declaration loops in <see cref="NarrativeReaders.In" /> and this is the only case that
    /// reddens.
    /// </para>
    /// <para>
    /// A field and not an auto-property, for the reason the fixture states at its own declaration: an
    /// auto-property's accessors carry <c>ldfld</c> against a backing field whose signature names the
    /// type, so the body half would catch it and this case would look load-bearing without being so.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Detector_ReportsAComputationDeclaringANarrativeFieldMember()
    {
        // Arrange
        const string offender = "UnitTests.Fixtures.Budgeting.Declared.CarriesANote";

        // Act
        IReadOnlyList<NarrativeTouch> touches =
            NarrativeReaders.In(FixtureAssembly, FixturePrefix, NarrativeTypeName);

        Console.WriteLine($"Touches: {string.Join(" | ", touches)}");

        // Assert
        await Assert.That(touches.Select(touch => touch.Computation)).Contains(offender);
    }

    [Test]
    public async Task Detector_AcceptsAComputationOverAmountsAndIdentifiersOnly()
    {
        // Arrange — the fixture namespace holding nothing but the clean computation.
        const string cleanPrefix = FixturePrefix + ".Clean";

        // Act
        IReadOnlyList<NarrativeTouch> touches =
            NarrativeReaders.In(FixtureAssembly, cleanPrefix, NarrativeTypeName);
        IReadOnlyList<string> scanned = NarrativeReaders.TypesIn(FixtureAssembly, cleanPrefix);

        Console.WriteLine($"Scanned: {string.Join(", ", scanned)}");
        Console.WriteLine($"Touches: {string.Join(" | ", touches)}");

        // Assert — the subject is non-empty first, because an empty scan produces the same empty
        // report a clean computation does, and green is what both look like.
        await Assert.That(scanned).Contains("UnitTests.Fixtures.Budgeting.Clean.ToAllocate");
        await Assert.That(touches).IsEmpty();
    }

    /// <summary>
    /// The namespace limit, written as an executable sentence rather than as coverage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Count this case as documentation and not as a case, and the file should say so rather than
    /// letting a reader tally it beside the ten that catch something.</b> Its first assertion is
    /// byte-identical in effect to
    /// <see cref="Detector_ReportsAComputationThatTouchesANarrativeFieldInAMethodBody" /> — the same
    /// violation, reported through the same arm, differing only in which prefix the scan was handed —
    /// and its second, that the outsider is absent under the gate's own prefix, reddens under no
    /// mutation that leaves the first green. So it adds no reachable failure to the file. What it
    /// adds is a fixture pair a reader can run: the type really is a violation, and the only thing
    /// hiding it is where it was filed.
    /// </para>
    /// <para>
    /// It is kept for the reason
    /// <c>NarrativeEncryptionCoverageTests.Compare_AgainstColumnsCarryingTheOtherFieldClassesCap_AcceptsBoth</c>
    /// is kept and argues in the same terms: a limit that lives only in a remark is a limit the next
    /// reader discovers from a green gate over a computation nobody scanned. The remedy when it bites
    /// is to move the computation under the prefix, or to state a new prefix in
    /// <see cref="EnvelopeHomes" /> with the reason the layer grew a third home — never to widen the
    /// prefix until it matches wherever the code drifted to.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Detector_IsBlindToATypeOutsideTheNamespace()
    {
        // Arrange — byte-for-byte the body-read violation, one namespace over.
        const string outsidePrefix = "UnitTests.Fixtures.OutsideBudgeting";
        const string outsider = outsidePrefix + ".AvailableFromNotes";

        // Act — the same detector twice, differing only in the prefix it was handed.
        IReadOnlyList<NarrativeTouch> underTheGate =
            NarrativeReaders.In(FixtureAssembly, FixturePrefix, NarrativeTypeName);
        IReadOnlyList<NarrativeTouch> pointedAtIt =
            NarrativeReaders.In(FixtureAssembly, outsidePrefix, NarrativeTypeName);

        Console.WriteLine($"Under the gate: {string.Join(" | ", underTheGate)}");
        Console.WriteLine($"Pointed at it: {string.Join(" | ", pointedAtIt)}");

        // Assert — pointed at it, the detector reports it, so the type really is a violation and the
        // only thing hiding it is where it was filed. Then, under the gate's own prefix, it is not
        // reported. This is a written-down limit made visible rather than a defect: the remedy is to
        // move the computation under the prefix, never to widen the prefix until it matches wherever
        // the code drifted to. See the fixture's own remarks for why an opt-in marker is worse.
        await Assert.That(pointedAtIt.Select(touch => touch.Computation)).Contains(outsider);
        await Assert.That(underTheGate.Select(touch => touch.Computation)).DoesNotContain(outsider);
    }

    /// <summary>
    /// A touch made inside a lambda is found, and reported against the type somebody wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The least obvious property of the whole detector, and the one a reader is most likely to
    /// break while tidying.</b> A projection over rows puts its column reads in a compiler-generated
    /// display class nested inside the computation, and a nested type carries an <i>empty</i>
    /// namespace in metadata. So the prefix filter cannot read each type definition's own namespace:
    /// it has to walk <c>GetDeclaringType()</c> to the outermost type and judge there. The same walk
    /// then supplies the reported name, which is why the two are one decision.
    /// </para>
    /// <para>
    /// <b>The assertion is on the lambda's own touch and not merely on the computation's name</b>,
    /// which was measured to matter. <c>Activity()</c> also references the display-class method by
    /// token, and that reference's signature names the narrative type, so a detector with the
    /// attribution walk removed still reports <c>ProjectsANote</c> — from the outer method — and an
    /// assertion about names alone stays green while every lambda in the subject has gone unscanned.
    /// Naming <c>&lt;Activity&gt;b__</c> is what makes the display class itself the subject.
    /// </para>
    /// <para>
    /// <b>A second line asserting that no reported computation carries a <c>&lt;</c> was here and was
    /// deleted, because no implementation could make it fail.</b> A compiler-generated name reaches
    /// the report only through a type definition whose own namespace is empty, and an empty namespace
    /// is under no prefix — so removing the attribution walk drops the display class out of the scan
    /// entirely and reddens the assertion above rather than that one, while leaving it means the
    /// second line is checking a shape C# cannot produce under the prefix. Measured. A line that
    /// cannot fail reads as coverage and is counted as coverage, which is the only harm it does and
    /// is enough.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Detector_AttributesACompilerGeneratedDisplayClassToItsDeclaringType()
    {
        // Arrange
        const string projectingPrefix = FixturePrefix + ".Projecting";
        const string computation = "UnitTests.Fixtures.Budgeting.Projecting.ProjectsANote";

        // Act
        IReadOnlyList<NarrativeTouch> touches =
            NarrativeReaders.In(FixtureAssembly, projectingPrefix, NarrativeTypeName);

        Console.WriteLine($"Touches: {string.Join(" | ", touches)}");

        // Assert — the touch the lambda itself makes, filed under the type that declares the lambda.
        // Both halves in one predicate: the display class was walked at all, and it was attributed
        // outward rather than reported as `ProjectsANote+<>c`, a name no reader can find in a file.
        await Assert.That(touches.Where(touch =>
                touch.Computation == computation
                && touch.Touch.StartsWith("<Activity>b__", StringComparison.Ordinal)))
            .IsNotEmpty();
    }

    /// <summary>
    /// A touch that survives only as an expression tree is found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>IQueryable</c> shape, and the one an envelope computation reading the database would
    /// take. "An expression tree is not IL" is the reasonable-sounding thing a reader concludes, and
    /// it is wrong: the property read survives as an <c>ldtoken</c> of <c>BudgetRow::get_Note</c>
    /// handed to <c>Expression.Property</c>. Drop <c>ldtoken</c> from
    /// <see cref="NarrativeReaders.TakesToken" /> and this is the only case that reddens — which is
    /// the whole reason it is a case and not a sentence.
    /// </para>
    /// <para>
    /// Separate from the display-class case beside it rather than folded in. They are two mechanisms
    /// with two mutations, and one test asserting both would report a single verdict for whichever
    /// broke.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Detector_ReportsATouchExpressedOnlyAsAnExpressionTree()
    {
        // Arrange
        const string projectingPrefix = FixturePrefix + ".Projecting";
        const string computation = "UnitTests.Fixtures.Budgeting.Projecting.QueriesANote";

        // Act
        IReadOnlyList<NarrativeTouch> touches =
            NarrativeReaders.In(FixtureAssembly, projectingPrefix, NarrativeTypeName);

        Console.WriteLine($"Touches: {string.Join(" | ", touches)}");

        // Assert — the predicate declares no narrative member and runs no lambda body of its own, so
        // the only evidence is the token it hands to the expression builder.
        await Assert.That(touches.Select(touch => touch.Computation)).Contains(computation);
    }

    /// <summary>
    /// A read across an assembly boundary is reported, on the strength of the referenced member's
    /// signature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case that covers the branch the real gate runs on.</b> A property read inside one
    /// assembly is a <c>MethodDefinition</c> token; across a boundary it is a
    /// <c>MemberReference</c>, and the two are decoded by different arms of
    /// <see cref="NarrativeReaders.NarrativeTouchOf" />. Every fixture in this file but one lives
    /// beside the detector, so before this case existed <i>no</i> assertion reached the second arm —
    /// and a detector with signature decoding removed from the <c>MemberReference</c> branch alone
    /// passed all eight of them while being blind exactly where <c>Application</c> reads
    /// <c>Domain</c>. Measured, and re-measured as the asymmetric mutation that reddens this case and
    /// leaves the others green.
    /// </para>
    /// <para>
    /// Asserted on the rendered touch and not on the computation's name. The same fixture also makes
    /// the owner-arm touch the next case is about, so a name-only assertion would be satisfied by
    /// either arm and would tell nobody which one worked.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Detector_ReportsACrossAssemblyReadWhoseSignatureNamesTheType()
    {
        // Arrange — the touch AccountDto.FromAccount makes today: the owner is Account, and the
        // narrative type appears only in what get_Name returns.
        const string crossAssemblyPrefix = FixturePrefix + ".CrossAssembly";
        const string expected =
            "Domain.Accounts.Account::get_Name : Domain.Security.NarrativeField()";

        // Act
        IReadOnlyList<NarrativeTouch> touches =
            NarrativeReaders.In(FixtureAssembly, crossAssemblyPrefix, NarrativeTypeName);

        Console.WriteLine($"Touches: {string.Join(" | ", touches)}");

        // Assert
        await Assert.That(touches.Where(touch => touch.Touch.Contains(expected, StringComparison.Ordinal)))
            .IsNotEmpty();
    }

    /// <summary>
    /// A read across an assembly boundary is reported when the narrative type is the referenced
    /// member's <i>owner</i> and appears nowhere in its signature.
    /// </summary>
    /// <remarks>
    /// The second arm of the same branch, and not covered by the case above: <c>Envelope</c> returns
    /// <c>ReadOnlyMemory&lt;byte&gt;</c>, so a detector reading signatures and never owners misses it
    /// while passing its neighbour. Both shapes sit two lines apart in <c>AccountDto.FromAccount</c>,
    /// which is why neither is hypothetical.
    /// </remarks>
    [Test]
    public async Task Detector_ReportsACrossAssemblyReadThroughTheNarrativeTypeItself()
    {
        // Arrange
        const string crossAssemblyPrefix = FixturePrefix + ".CrossAssembly";
        const string expected = "Domain.Security.NarrativeField::get_Envelope";

        // Act
        IReadOnlyList<NarrativeTouch> touches =
            NarrativeReaders.In(FixtureAssembly, crossAssemblyPrefix, NarrativeTypeName);

        Console.WriteLine($"Touches: {string.Join(" | ", touches)}");

        // Assert
        await Assert.That(touches.Where(touch => touch.Touch.Contains(expected, StringComparison.Ordinal)))
            .IsNotEmpty();
    }

    /// <summary>
    /// A type whose name merely starts with the target's is not a narrative touch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The control that holds the comparison ordinal-equal.</b>
    /// <c>Domain.Security.NarrativeFieldLimits</c> contains <c>Domain.Security.NarrativeField</c> in
    /// full, so a detector matching by <c>Contains</c> reports this fixture — and reported nothing
    /// wrong in any of the eight cases that came before it, which is exactly why it passed measurement
    /// as a wrong implementation. Every future type named in that family inherits this control.
    /// </para>
    /// <para>
    /// Rendered names are compared, not <see cref="Type" /> objects, because the detector never loads
    /// the assembly it reads — so "ordinal equality on the full name" is the strongest form the
    /// comparison can take here, and the weakest form that is still correct is the one this case
    /// forbids.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Detector_AcceptsAComputationTouchingATypeWhoseNameMerelyStartsWithTheTarget()
    {
        // Arrange
        const string nearMissPrefix = FixturePrefix + ".NearMiss";

        // Act
        IReadOnlyList<NarrativeTouch> touches =
            NarrativeReaders.In(FixtureAssembly, nearMissPrefix, NarrativeTypeName);
        IReadOnlyList<string> scanned = NarrativeReaders.TypesIn(FixtureAssembly, nearMissPrefix);

        Console.WriteLine($"Scanned: {string.Join(", ", scanned)}");
        Console.WriteLine($"Touches: {string.Join(" | ", touches)}");

        // Assert — the subject is non-empty first, because an empty scan reports clean too.
        await Assert.That(scanned).Contains("UnitTests.Fixtures.Budgeting.NearMiss.CapsFromLimits");
        await Assert.That(touches).IsEmpty();
    }

    /// <summary>
    /// The narrative type is found when it appears only inside a generic instantiation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third handle kind. <c>new List&lt;NarrativeField&gt;()</c> is a member reference whose
    /// parent is a <c>TypeSpecification</c> — an encoded signature rather than a name — and whose own
    /// signature is <c>Void()</c>. Neither the member signature nor a type-reference name carries the
    /// target, so decoding the parent's specification is the only thing that finds it.
    /// </para>
    /// <para>
    /// It was flagged as uncovered rather than as a defect, and that reading proved right: the
    /// detector already routes <c>TypeSpecification</c> through the same provider, so this case
    /// passed the moment it was written. It is kept as the control that proves it, which is the whole
    /// difference between a property that holds and a property nobody has checked — delete the
    /// specification arm of <see cref="NarrativeReaders.UseOf" /> and this is the only case that
    /// reddens.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Detector_ReportsANarrativeTypeReachedOnlyThroughAGenericInstantiation()
    {
        // Arrange
        const string genericPrefix = FixturePrefix + ".Generic";
        const string computation = "UnitTests.Fixtures.Budgeting.Generic.CollectsNotes";

        // Act
        IReadOnlyList<NarrativeTouch> touches =
            NarrativeReaders.In(FixtureAssembly, genericPrefix, NarrativeTypeName);

        Console.WriteLine($"Touches: {string.Join(" | ", touches)}");

        // Assert
        await Assert.That(touches.Select(touch => touch.Computation)).Contains(computation);
    }

    /// <summary>
    /// The CON-005 gate: no envelope-budgeting computation touches a narrative column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This gate is over the empty set today, and the case beneath it is what says so.</b> Read the
    /// two together or this one reads as coverage it does not have. Nothing under
    /// <c>Application.Budgeting</c> or <c>Domain.Budgeting</c> exists yet, so the assertion below
    /// cannot object to anything — which is exactly the state CON-005 describes: "this holds today;
    /// the constraint exists so the envelope layer cannot break it later". What makes it worth
    /// shipping now rather than with the first assignment is that the detector arrives proved: the
    /// eleven synthetic controls this file ships — the ten above this case, and the escape scan below
    /// the pin beneath it — run against subjects in this assembly, so the day a computation lands
    /// under one of these prefixes the guard is already known to be able to fail.
    /// </para>
    /// <para>
    /// The non-emptiness floor here is deliberately about the <i>assemblies</i> and not the prefixes.
    /// Asserting that <c>Application</c> declares types proves the scan opened a real file and walked
    /// a real metadata table; asserting that <c>Application.Budgeting</c> declares any would fail
    /// today, and is the neighbouring case's job to state as a fact rather than a defect.
    /// </para>
    /// <para>
    /// <b>That floor is anchored to a type the compiler resolves and to
    /// <see cref="BudgetingCodeOutsideItsPinnedPrefix" />, and both changes were forced by a measured
    /// hole.</b> The floor shipped as <c>TypesIn(assembly, prefix.Split('.')[0])</c>, derived from the
    /// same string it was standing in for, so misspelling <c>Application.Budgeting</c> as
    /// <c>Application.Bugdeting</c> <i>and</i> adding a real computation under the correct namespace
    /// left this case, its neighbour and the whole suite green — the gate silently stopped pointing at
    /// anything, which is the one failure a gate over the empty set has. Naming the assembly by
    /// <see cref="Type" /> makes the floor a compiler-checked claim, and the escape scan makes the
    /// prefix's spelling answerable to the assemblies rather than to itself.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EnvelopeBudgeting_ReadsNoNarrativeField()
    {
        // Arrange
        List<NarrativeTouch> touches = [];
        List<string> scanned = [];
        List<string> anchors = [];

        // Act
        foreach ((Type anchor, string prefix) in EnvelopeHomes)
        {
            string assembly = anchor.Assembly.Location;
            touches.AddRange(NarrativeReaders.In(assembly, prefix, NarrativeTypeName));
            scanned.AddRange(NarrativeReaders.TypesIn(assembly, prefix));

            // The floor, from the anchor rather than from the prefix: a real type in this assembly,
            // named by the compiler, that the walk has to come back holding.
            anchors.AddRange(
                NarrativeReaders
                    .TypesIn(assembly, anchor.Namespace!)
                    .Where(name => string.Equals(name, anchor.FullName, StringComparison.Ordinal)));
        }

        IReadOnlyList<string> escaped = BudgetingCodeOutsideItsPinnedPrefix();

        // Reported in full, and to the console rather than through the assertion: TUnit's string
        // assertions truncate, so a census reporting through one string.Join names the first offender
        // and hides the rest.
        Console.WriteLine($"Envelope computations scanned: {string.Join(", ", scanned)}");
        Console.WriteLine($"Anchors found: {string.Join(", ", anchors)}");
        Console.WriteLine($"Budgeting code outside its prefix: {string.Join(", ", escaped)}");
        Console.WriteLine($"Narrative touches: {string.Join(" | ", touches)}");

        // Assert — the assemblies were really read first, one anchor apiece. The difference below is
        // over a list that an unopened file, a mistyped path or a metadata walk that found nothing
        // would leave empty, and green is what all of those look like.
        await Assert.That(anchors.Count).IsEqualTo(EnvelopeHomes.Count);

        // And the prefixes really point at the budgeting code, which the line above cannot say: a
        // type whose namespace carries a Budgeting segment and which is not under this ring's pinned
        // prefix is either a misspelled prefix or a computation filed somewhere this gate cannot
        // reach, and both leave the assertion beneath them empty for the wrong reason. Read
        // BudgetingSegment before making this green again — the remedy is a prefix that matches the
        // code or a code that matches the prefix, never a widened segment.
        await Assert.That(escaped).IsEmpty();

        // The gate. Not "few" and not "none in Application": a narrative column read by anything that
        // decides an amount is the whole of what CON-005 forbids.
        await Assert.That(touches).IsEmpty();
    }

    /// <summary>
    /// The emptiness pin: there is no envelope-budgeting code yet, and the gate above is therefore
    /// vacuous.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>When this case goes red, it has done its job and must be replaced rather than deleted.</b>
    /// A red here means somebody wrote the first envelope-budgeting computation, so the gate above has
    /// stopped being a gate over nothing and started being a gate. The replacement is a non-emptiness
    /// floor over the same census — <c>await Assert.That(scanned).IsNotEmpty()</c>, and better a
    /// named type — moved into <c>EnvelopeBudgeting_ReadsNoNarrativeField</c> beside the assembly
    /// floor it already carries, so that from that day on an empty touch list means the computations
    /// were read and found clean rather than that there were none.
    /// </para>
    /// <para>
    /// Deleting it instead leaves the suite with no statement about whether the subject exists, which
    /// is the one thing a coverage gate cannot tell you about itself. Widening it — "at most a
    /// handful of types" — is worse: it would be green both before and after the change it exists to
    /// announce.
    /// </para>
    /// <para>
    /// It is a pin on the <i>types</i> and not on the namespace's existence, because a namespace with
    /// no types in it leaves no trace in metadata at all. A folder can be created, a file can be
    /// added with nothing but usings in it, and neither moves this line — which is right: neither is
    /// a computation.
    /// </para>
    /// <para>
    /// <b>Two empty lists are asserted and only one of them is this case's own claim.</b> "Nothing is
    /// declared under the prefix" and "nothing carrying a <see cref="BudgetingSegment" /> segment is
    /// declared outside it" are the two halves of "there is no envelope-budgeting code yet", and the
    /// first alone cannot distinguish that from a prefix pointing at nothing — measured, as the
    /// double mutation argued at <see cref="BudgetingCodeOutsideItsPinnedPrefix" />. The day the first
    /// computation lands, both lines go red together and both are answered by the same edit.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EnvelopeBudgeting_DeclaresNoTypesYet()
    {
        // Arrange
        List<string> scanned = [];

        // Act
        foreach ((Type anchor, string prefix) in EnvelopeHomes)
        {
            scanned.AddRange(NarrativeReaders.TypesIn(anchor.Assembly.Location, prefix));
        }

        IReadOnlyList<string> escaped = BudgetingCodeOutsideItsPinnedPrefix();

        Console.WriteLine($"Envelope computations declared: {string.Join(", ", scanned)}");
        Console.WriteLine($"Budgeting code outside its prefix: {string.Join(", ", escaped)}");

        // Assert — read the remarks before making this green again. The remedy is not to delete the
        // case; it is to move a non-emptiness floor into the gate above and retire this one.
        await Assert.That(scanned).IsEmpty();

        // The other half: nothing anywhere in the two rings carries a Budgeting namespace segment
        // outside its pinned prefix. Without this line "no envelope code exists" and "the prefix is
        // misspelled" produce the same empty list and the same green.
        await Assert.That(escaped).IsEmpty();
    }

    /// <summary>
    /// The escape scan finds budgeting code a misspelled prefix would hide, and finds nothing when
    /// the prefix is right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The attack, shipped as a permanent control.</b> The escape check the two gate cases now
    /// carry is over the empty set in production exactly as the gate itself is, so on its own it is
    /// another guard that cannot be told from a guard that cannot object. This case points the same
    /// scan at <c>UnitTests.dll</c>, where nine fixtures sit under a real <c>Budgeting</c> namespace,
    /// and mistypes the prefix the way somebody mistypes a prefix — one transposition, no other
    /// change. The fixtures are reported, which is the whole property: a prefix that has stopped
    /// naming the code does not silence the scan, it fills it.
    /// </para>
    /// <para>
    /// <b>Three assertions and each fails to a different mistake.</b> Pointed at the correct prefix
    /// the same scan is silent, so what the first line demonstrates is a misspelling rather than a
    /// scan that reports whatever it walks. And <c>UnitTests.Fixtures.OutsideBudgeting</c> — the
    /// namespace this file deliberately files a violation in — is <i>not</i> carried by the segment
    /// scan, which holds the comparison to a whole dot-separated segment: a substring test would
    /// sweep it in, and the escape check would then report a type whose being out of reach is a
    /// written-down limit rather than a defect. That is the same near-miss discipline
    /// <see cref="Detector_AcceptsAComputationTouchingATypeWhoseNameMerelyStartsWithTheTarget" />
    /// holds over the type comparison, one layer out.
    /// </para>
    /// </remarks>
    [Test]
    public async Task EscapeScan_UnderAMistypedPrefix_ReportsTheBudgetingFixturesItWouldOtherwiseCover()
    {
        // Arrange — one transposition in the leaf segment, the fixtures left where they are.
        const string mistyped = "UnitTests.Fixtures.Bugdeting";
        const string fixture = "UnitTests.Fixtures.Budgeting.BodyRead.AvailableFromNotes";
        const string outsider = "UnitTests.Fixtures.OutsideBudgeting.AvailableFromNotes";

        // Act — one scan, filtered twice, so the only difference between the two answers is the
        // spelling of the prefix each was compared against.
        IReadOnlyList<string> carriesTheSegment =
            NarrativeReaders.TypesWithNamespaceSegment(FixtureAssembly, BudgetingSegment);
        string[] escapedTheMistypedPrefix =
        [
            .. carriesTheSegment.Where(name =>
                !name.StartsWith(mistyped + ".", StringComparison.Ordinal)),
        ];
        string[] escapedTheRealPrefix =
        [
            .. carriesTheSegment.Where(name =>
                !name.StartsWith(FixturePrefix + ".", StringComparison.Ordinal)),
        ];

        Console.WriteLine($"Carries the segment: {string.Join(", ", carriesTheSegment)}");
        Console.WriteLine($"Escaped the mistyped prefix: {string.Join(", ", escapedTheMistypedPrefix)}");
        Console.WriteLine($"Escaped the real prefix: {string.Join(", ", escapedTheRealPrefix)}");

        // Assert — the misspelling is loud rather than silent.
        await Assert.That(escapedTheMistypedPrefix).Contains(fixture);

        // Pointed at the prefix that does name them, the scan reports none of them, which is the
        // state the two gate cases are in today and the reason their empty list means something.
        await Assert.That(escapedTheRealPrefix).IsEmpty();

        // And a whole segment, never a substring: OutsideBudgeting is out of this gate's reach by
        // construction and must not be dragged into it by a looser comparison.
        await Assert.That(carriesTheSegment).DoesNotContain(outsider);
    }

    internal readonly record struct NarrativeTouch(string Computation, string Touch)
    {
        public override string ToString() => $"{Computation} — {Touch}";
    }

    private static class NarrativeReaders
    {
        internal static IReadOnlyList<NarrativeTouch> In(
            string assemblyPath,
            string namespacePrefix,
            string narrativeTypeFullName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(namespacePrefix);
            ArgumentException.ThrowIfNullOrWhiteSpace(narrativeTypeFullName);

            using FileStream stream = File.OpenRead(assemblyPath);
            using PEReader pe = new(stream);
            MetadataReader md = pe.GetMetadataReader();
            MentionProvider provider = new(narrativeTypeFullName);

            List<NarrativeTouch> touches = [];
            foreach (TypeDefinitionHandle handle in md.TypeDefinitions)
            {
                TypeDefinition type = md.GetTypeDefinition(handle);
                TypeDefinition outermost = Outermost(md, type);
                if (!Under(md.GetString(outermost.Namespace), namespacePrefix))
                {
                    continue;
                }

                string computation = FullNameOf(md, outermost);

                // The declaration half. A member typed for a narrative column is a touch whether or
                // not any IL in this assembly reads it: an unread field and a body-less method leave
                // the body walk below nothing at all to find.
                foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
                {
                    FieldDefinition field = md.GetFieldDefinition(fieldHandle);
                    TypeUse use = field.DecodeSignature(provider, null);
                    if (use.Mentions)
                    {
                        touches.Add(new NarrativeTouch(
                            computation, $"declares {md.GetString(field.Name)} : {use.Display}"));
                    }
                }

                foreach (MethodDefinitionHandle declaredHandle in type.GetMethods())
                {
                    MethodDefinition declared = md.GetMethodDefinition(declaredHandle);
                    (string rendered, bool mentions) = Rendered(declared.DecodeSignature(provider, null));
                    if (mentions)
                    {
                        touches.Add(new NarrativeTouch(
                            computation, $"declares {md.GetString(declared.Name)} : {rendered}"));
                    }
                }

                foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                {
                    MethodDefinition method = md.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }

                    string methodName = md.GetString(method.Name);
                    byte[] il = [.. pe.GetMethodBody(method.RelativeVirtualAddress).GetILContent()];
                    foreach (int token in TokensIn(il))
                    {
                        EntityHandle referenced = MetadataTokens.EntityHandle(token);
                        if (referenced.IsNil)
                        {
                            continue;
                        }

                        string? touched = NarrativeTouchOf(md, referenced, provider);
                        if (touched is null)
                        {
                            continue;
                        }

                        touches.Add(new NarrativeTouch(computation, $"{methodName}() reads {touched}"));
                    }
                }
            }

            return [.. touches.Distinct().OrderBy(touch => touch.ToString(), StringComparer.Ordinal)];
        }

        /// <summary>
        /// Names every source-visible type the assembly declares under the prefix.
        /// </summary>
        /// <remarks>
        /// The subject census, and the only thing that can tell "nothing was found" from "nothing was
        /// looked at". Compiler-generated types are folded into their outermost declaring type by the
        /// same walk the touch report uses, so this counts the types somebody wrote.
        /// </remarks>
        internal static IReadOnlyList<string> TypesIn(string assemblyPath, string namespacePrefix)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(namespacePrefix);

            using FileStream stream = File.OpenRead(assemblyPath);
            using PEReader pe = new(stream);
            MetadataReader md = pe.GetMetadataReader();

            return
            [
                .. md.TypeDefinitions
                    .Select(handle => Outermost(md, md.GetTypeDefinition(handle)))
                    .Where(type => Under(md.GetString(type.Namespace), namespacePrefix))
                    .Select(type => FullNameOf(md, type))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];
        }

        /// <summary>
        /// Names every source-visible type the assembly declares whose namespace carries
        /// <paramref name="segment" /> as a whole dot-separated segment.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The one reader in this file that is not handed a prefix</b>, which is the point of it:
        /// it answers "where is the budgeting code" from the assembly, so a caller comparing its
        /// answer against a prefix is comparing two independent statements rather than one statement
        /// with itself. <see cref="TypesIn" /> cannot be reused for that — it filters on the prefix,
        /// so a misspelled prefix makes it answer nothing, which is exactly the silence being
        /// guarded against.
        /// </para>
        /// <para>
        /// A whole segment and never a substring, so <c>OutsideBudgeting</c> and a future
        /// <c>BudgetingReports</c> are both outside it. The same walk to the outermost declaring type
        /// as everything else here, so a display class is reported under the type somebody wrote.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<string> TypesWithNamespaceSegment(
            string assemblyPath, string segment)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(segment);

            using FileStream stream = File.OpenRead(assemblyPath);
            using PEReader pe = new(stream);
            MetadataReader md = pe.GetMetadataReader();

            return
            [
                .. md.TypeDefinitions
                    .Select(handle => Outermost(md, md.GetTypeDefinition(handle)))
                    .Where(type => Carries(md.GetString(type.Namespace), segment))
                    .Select(type => FullNameOf(md, type))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];
        }

        /// <summary>
        /// Renders the narrative touch a referenced member makes, or <see langword="null" />.
        /// </summary>
        private static string? NarrativeTouchOf(
            MetadataReader md, EntityHandle handle, MentionProvider provider)
        {
            switch (handle.Kind)
            {
                case HandleKind.MemberReference:
                    {
                        MemberReference member = md.GetMemberReference((MemberReferenceHandle)handle);
                        TypeUse owner = UseOf(md, member.Parent, provider);
                        (string rendered, bool mentions) = member.GetKind() == MemberReferenceKind.Field
                            ? Rendered(member.DecodeFieldSignature(provider, null))
                            : Rendered(member.DecodeMethodSignature(provider, null));
                        return owner.Mentions || mentions
                            ? $"{owner.Display}::{md.GetString(member.Name)} : {rendered}"
                            : null;
                    }

                case HandleKind.MethodDefinition:
                    {
                        MethodDefinition target = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                        TypeUse owner = UseOf(md, target.GetDeclaringType(), provider);
                        (string rendered, bool mentions) = Rendered(target.DecodeSignature(provider, null));
                        return owner.Mentions || mentions
                            ? $"{owner.Display}::{md.GetString(target.Name)} : {rendered}"
                            : null;
                    }

                case HandleKind.FieldDefinition:
                    {
                        FieldDefinition target = md.GetFieldDefinition((FieldDefinitionHandle)handle);
                        TypeUse owner = UseOf(md, target.GetDeclaringType(), provider);
                        TypeUse use = target.DecodeSignature(provider, null);
                        return owner.Mentions || use.Mentions
                            ? $"{owner.Display}::{md.GetString(target.Name)} : {use.Display}"
                            : null;
                    }

                case HandleKind.MethodSpecification:
                    {
                        MethodSpecification target =
                            md.GetMethodSpecification((MethodSpecificationHandle)handle);
                        string? method = NarrativeTouchOf(md, target.Method, provider);
                        ImmutableArray<TypeUse> arguments = target.DecodeSignature(provider, null);
                        if (method is not null)
                        {
                            return method;
                        }

                        return arguments.Any(argument => argument.Mentions)
                            ? $"<generic instantiation> : {string.Join(", ", arguments.Select(a => a.Display))}"
                            : null;
                    }

                case HandleKind.TypeDefinition:
                case HandleKind.TypeReference:
                case HandleKind.TypeSpecification:
                    {
                        TypeUse use = UseOf(md, handle, provider);
                        return use.Mentions ? use.Display : null;
                    }

                default:
                    return null;
            }
        }

        private static (string Rendered, bool Mentions) Rendered(TypeUse use) =>
            (use.Display, use.Mentions);

        private static (string Rendered, bool Mentions) Rendered(MethodSignature<TypeUse> signature) =>
            ($"{signature.ReturnType.Display}({string.Join(", ", signature.ParameterTypes.Select(p => p.Display))})",
                signature.ReturnType.Mentions || signature.ParameterTypes.Any(p => p.Mentions));

        private static TypeUse UseOf(
            MetadataReader md, EntityHandle handle, MentionProvider provider) => handle.Kind switch
            {
                HandleKind.TypeDefinition => provider.GetTypeFromDefinition(
                    md, (TypeDefinitionHandle)handle, 0),
                HandleKind.TypeReference => provider.GetTypeFromReference(
                    md, (TypeReferenceHandle)handle, 0),
                HandleKind.TypeSpecification => provider.GetTypeFromSpecification(
                    md, null, (TypeSpecificationHandle)handle, 0),
                _ => new TypeUse("<unknown>", false),
            };

        private static string FullNameOf(MetadataReader md, TypeDefinition type)
        {
            string ns = md.GetString(type.Namespace);
            string name = md.GetString(type.Name);
            TypeDefinitionHandle declaring = type.GetDeclaringType();
            return declaring.IsNil
                ? ns.Length == 0 ? name : $"{ns}.{name}"
                : $"{FullNameOf(md, md.GetTypeDefinition(declaring))}+{name}";
        }

        private static string FullNameOf(MetadataReader md, TypeReference type)
        {
            string ns = md.GetString(type.Namespace);
            string name = md.GetString(type.Name);
            return ns.Length == 0 ? name : $"{ns}.{name}";
        }

        private static TypeDefinition Outermost(MetadataReader md, TypeDefinition type)
        {
            TypeDefinitionHandle declaring = type.GetDeclaringType();
            return declaring.IsNil ? type : Outermost(md, md.GetTypeDefinition(declaring));
        }

        private static bool Under(string ns, string prefix) =>
            string.Equals(ns, prefix, StringComparison.Ordinal)
            || ns.StartsWith(prefix + ".", StringComparison.Ordinal);

        /// <summary>Whether <paramref name="ns" /> has <paramref name="segment" /> as a segment.</summary>
        /// <remarks>
        /// Split and compared whole rather than matched as a substring, for the reason
        /// <see cref="TypesWithNamespaceSegment" /> gives: <c>OutsideBudgeting</c> contains the word
        /// and is not it.
        /// </remarks>
        private static bool Carries(string ns, string segment) =>
            ns.Split('.').Any(part => string.Equals(part, segment, StringComparison.Ordinal));

        private static IEnumerable<int> TokensIn(byte[] il)
        {
            List<int> tokens = [];
            int at = 0;
            while (at < il.Length)
            {
                int op = il[at];
                int opcodeSize = 1;
                if (op == 0xFE)
                {
                    if (at + 1 >= il.Length)
                    {
                        break;
                    }

                    op = 0xFE00 | il[at + 1];
                    opcodeSize = 2;
                }

                if (op == 0x45)
                {
                    if (at + opcodeSize + 4 > il.Length)
                    {
                        break;
                    }

                    int cases = BitConverter.ToInt32(il, at + opcodeSize);
                    at += opcodeSize + 4 + (cases * 4);
                    continue;
                }

                int operandSize = OperandSize(op);
                if (TakesToken(op) && at + opcodeSize + 4 <= il.Length)
                {
                    tokens.Add(BitConverter.ToInt32(il, at + opcodeSize));
                }

                at += opcodeSize + operandSize;
            }

            return tokens;
        }

        private static bool TakesToken(int op) => op is 0x27 or 0x28 or 0x6F or 0x70 or 0x71 or 0x73
            or 0x74 or 0x75 or 0x79 or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x81 or 0x8C
            or 0x8D or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0
            or 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C;

        private static int OperandSize(int op)
        {
            if (op is 0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 or 0x1F or 0xDE
                or 0xFE12 or 0xFE19)
            {
                return 1;
            }

            if (op is >= 0x2B and <= 0x37)
            {
                return 1;
            }

            if (op is >= 0xFE09 and <= 0xFE0E)
            {
                return 2;
            }

            if (op is 0x21 or 0x23)
            {
                return 8;
            }

            if (op is >= 0x38 and <= 0x44)
            {
                return 4;
            }

            if (op is 0x20 or 0x22 or 0x27 or 0x28 or 0x29 or 0x6F or 0x70 or 0x71 or 0x72 or 0x73
                or 0x74 or 0x75 or 0x79 or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x81
                or 0x8C or 0x8D or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 or 0xDD
                or 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C)
            {
                return 4;
            }

            return 0;
        }

        /// <summary>One type as a signature uses it: how to print it, and whether it is the target.</summary>
        internal readonly record struct TypeUse(string Display, bool Mentions);

        /// <summary>
        /// Decodes a metadata signature into printable text while carrying, alongside it, whether the
        /// narrative type appears anywhere inside it.
        /// </summary>
        private sealed class MentionProvider : ISignatureTypeProvider<TypeUse, object?>
        {
            private readonly string _target;

            internal MentionProvider(string target) => _target = target;

            public TypeUse GetArrayType(TypeUse elementType, ArrayShape shape) =>
                elementType with { Display = elementType.Display + "[]" };

            public TypeUse GetByReferenceType(TypeUse elementType) =>
                elementType with { Display = elementType.Display + "&" };

            public TypeUse GetFunctionPointerType(MethodSignature<TypeUse> signature) =>
                new(
                    "method*",
                    signature.ReturnType.Mentions || signature.ParameterTypes.Any(p => p.Mentions));

            public TypeUse GetGenericInstantiation(
                TypeUse genericType, ImmutableArray<TypeUse> typeArguments) =>
                new(
                    $"{genericType.Display}<{string.Join(",", typeArguments.Select(a => a.Display))}>",
                    genericType.Mentions || typeArguments.Any(a => a.Mentions));

            public TypeUse GetGenericMethodParameter(object? genericContext, int index) =>
                new($"!!{index}", false);

            public TypeUse GetGenericTypeParameter(object? genericContext, int index) =>
                new($"!{index}", false);

            public TypeUse GetModifiedType(TypeUse modifier, TypeUse unmodifiedType, bool isRequired) =>
                unmodifiedType with { Mentions = unmodifiedType.Mentions || modifier.Mentions };

            public TypeUse GetPinnedType(TypeUse elementType) => elementType;

            public TypeUse GetPointerType(TypeUse elementType) =>
                elementType with { Display = elementType.Display + "*" };

            public TypeUse GetPrimitiveType(PrimitiveTypeCode typeCode) =>
                new(typeCode.ToString(), false);

            public TypeUse GetSZArrayType(TypeUse elementType) =>
                elementType with { Display = elementType.Display + "[]" };

            public TypeUse GetTypeFromDefinition(
                MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
                Named(FullNameOf(reader, reader.GetTypeDefinition(handle)));

            public TypeUse GetTypeFromReference(
                MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
                Named(FullNameOf(reader, reader.GetTypeReference(handle)));

            public TypeUse GetTypeFromSpecification(
                MetadataReader reader,
                object? genericContext,
                TypeSpecificationHandle handle,
                byte rawTypeKind) =>
                reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

            private TypeUse Named(string fullName) =>
                new(fullName, string.Equals(fullName, _target, StringComparison.Ordinal));
        }
    }
}
