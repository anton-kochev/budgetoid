using System.Reflection;
using Api.Endpoints;
using Application.Abstractions;
using Domain.Accounts;
using Infrastructure.Persistence.Provisioning;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The four censuses that carry FR-063 and FR-064: nothing the client sends can hold an unwrapped
/// key, a key-encryption key, a PRF output or a recovery code, and nothing the server stores is a
/// value by which a wrapped key could be unwrapped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both requirements are verified by Inspection in the specification, and these tests are a
/// deliberate strengthening of that rather than a restatement of it.</b> An Inspection is a person
/// reading the request surface and the schema and stating that neither carries key material. What an
/// executable census buys is the half a person cannot repeat on every commit — a request member or a
/// binary column arriving between one reading and the next. What it does not buy is the half that
/// matters most, which is what a value <i>is</i> rather than what it is called. <b>A green run here
/// is not the requirement being met</b>; it is the requirement not having been broken in the two
/// ways a machine can see. The Inspection stands, and
/// <c>docs/business-logic/account-keys.md</c> is what it is performed against.
/// </para>
/// <para>
/// The four divide the work by what they read, and none of them subsumes another.
/// <see cref="RequestSurface_CarriesNoMemberThatCouldHoldUnwrappedKeyMaterial" /> reads names on the
/// way <i>in</i> and is the whole of AC 5.
/// <see cref="Schema_HoldsNoColumnNamedForUnwrappedKeyMaterial" /> reads names at <i>rest</i>. Both
/// are name checks, and <see cref="UnwrappedKeyMaterialVocabulary" /> says plainly that a
/// <c>bytea</c> column called <c>payload</c> walks past every rule it owns.
/// <see cref="Schema_ClassifiesEveryBinaryColumn" /> closes that gap from the other side, by refusing
/// to let a binary column exist without a written argument for why holding it unwraps nothing — so a
/// badly named column cannot slip past it.
/// <see cref="RequestSurface_ArguesForEveryMemberThatCanCarryText" /> is that same argument on the way
/// <i>in</i>, and it is the newest: for a long time the schema had a fail-closed leg and the request
/// surface had only a deny-list, so <c>string? Code</c> and <c>string? Passphrase</c> could be added
/// to a request record and refused by nothing while <c>string? RecoveryCode</c> was caught. The two
/// name censuses stay because a name that trips a rule should be reported as <em>that</em> rule, with
/// the argument a reviewer has to answer, rather than as an unargued member.
/// </para>
/// <para>
/// Every one of the three ships a permanent negative control that grows the offence on a throwaway
/// database and demands the scan name it. A census whose query stopped reaching the catalog, or
/// whose reflection predicate stopped matching types, reports an empty offender set — which is
/// indistinguishable from the rule holding. The controls are what make the difference visible.
/// </para>
/// </remarks>
public sealed class KeyMaterialSecrecyTests
{
    [Test]
    public async Task RequestSurface_CarriesNoMemberThatCouldHoldUnwrappedKeyMaterial()
    {
        // Arrange — the request surface is DERIVED rather than listed, and that is the whole point of
        // the test. A written-down set of DTOs stays green on the day a new endpoint adds a
        // key-accepting member to a type the list never heard of, which is precisely the commit this
        // census exists to catch.
        IReadOnlyList<SurfaceMember> surface = await RequestSurfaceAsync();

        // Act — every member of every reached type, through the shared vocabulary.
        string[] offenders =
        [
            .. surface
                .Select(member => (member, rule: UnwrappedKeyMaterialVocabulary.Classify(member.Member)))
                .Where(candidate => candidate.rule is not null)
                .Select(candidate =>
                    $"{candidate.member.Owner}.{candidate.member.Member} "
                    + $"— {candidate.rule!.Category}: {candidate.rule.Reason}")
                .Order(StringComparer.Ordinal),
        ];

        // Assert — joined rather than counted, so a failure hands the reviewer the member, the secret
        // it names and the argument for refusing it in one sentence instead of a number.
        await Assert.That(string.Join(Environment.NewLine, offenders)).IsEqualTo(string.Empty);

        // Non-vacuity, three ways, because a reflection predicate that silently matches nothing is how
        // this kind of test dies: it goes green over an empty set and reads exactly like the rule
        // holding. Nothing below is a claim about key material — each one asks whether the scan above
        // examined anything at all.
        string[] reached = [.. surface.Select(member => member.Owner).Distinct().Order(StringComparer.Ordinal)];
        Console.WriteLine(
            $"Request surface: {reached.Length} types, {surface.Count} members. "
            + string.Join(", ", reached));

        await Assert.That(surface.Count).IsGreaterThan(50);
        await Assert.That(reached.Length).IsGreaterThan(15);

        // Named by hand, and by string because every one of them is a private nested record this
        // project cannot name in a typeof. Four request bodies that would each be a place to put a
        // key, one command bound straight from the body rather than through a nested record — which
        // the nested-type sweep alone would never have seen — and the two-deep PRF pair below.
        await Assert.That(reached).Contains("PasskeyEndpoints.RegistrationRequest");
        await Assert.That(reached).Contains("RecoveryCodeEndpoints.RecoveryCodeGenerationRequest");
        await Assert.That(reached).Contains("AccountErasureEndpoints.ErasureRequest");
        await Assert.That(reached).Contains("CredentialEndpoints.RevocationRequest");
        await Assert.That(reached).Contains("CreateTransactionCommand");

        // The recursion's own control. PasskeyPrfResults is two hops down — RegistrationRequest holds
        // a PasskeyClientExtensionResults, which holds it — and it is the single most interesting
        // shape on this surface: `prf.enabled` is a legal member and `prf.results` would not be, so
        // the difference between them can only be judged by a census that REACHES the nested type. A
        // walk that stopped at the top level would report green having never looked.
        await Assert.That(reached).Contains("PasskeyClientExtensionResults");
        await Assert.That(reached).Contains("PasskeyPrfResults");
    }

    /// <summary>
    /// Every member of the request surface that can carry text is argued for by name, and every argument
    /// names a member that is still there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mirror of <see cref="Schema_ClassifiesEveryBinaryColumn" />, and the leg the request surface
    /// did not have.</b> The name census next door is a <b>deny-list</b>: it refuses the tokens
    /// <see cref="UnwrappedKeyMaterialVocabulary" /> recognises and lets everything else through, and that
    /// vocabulary's own remarks say plainly that a member called <c>payload</c> walks past every rule it
    /// owns. It is true on this side of the wire too — <c>string? Code</c> and <c>string? Passphrase</c>
    /// added to a registration request are refused by nothing, while <c>string? RecoveryCode</c> is
    /// caught, and the difference is a word rather than a capability.
    /// </para>
    /// <para>
    /// <b>So this leg fails closed: a text member with no written argument is a red.</b> It is more
    /// expensive than a deny-list, one line per member and a sentence to write when a route grows one,
    /// and it is the only shape whose verdict does not depend on what somebody chose to call a field.
    /// </para>
    /// <para>
    /// <b>Both directions, and each fails for its own reason.</b> A member nobody argued for is a place
    /// key material could arrive under a name no vocabulary can judge. An argument for a member that is
    /// gone is the same defect running backwards: the list rots into a claim about a surface that has
    /// moved, and every surviving entry still passes, so the file goes on reading like a complete account
    /// of something it has stopped describing.
    /// </para>
    /// <para>
    /// <b>Text, not <see cref="string" /> exactly.</b> A member typed <c>string[]</c>,
    /// <c>IReadOnlyList&lt;string&gt;</c> or <c>Optional&lt;string&gt;</c> holds text just as well as a
    /// bare one, and a census that matched the bare type only would be one generic away from silence.
    /// </para>
    /// <para>
    /// Responses are in here with requests, because the surface walk deliberately does not tell them
    /// apart — see <see cref="RequestSurfaceAsync" /> for why a suffix filter is the wrong instrument.
    /// Arguing for a response member costs a line and buys the same fail-closed property.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RequestSurface_ArguesForEveryMemberThatCanCarryText()
    {
        // Arrange — the same derived surface the deny-list census reads, so the two legs cannot disagree
        // about what the request surface is.
        IReadOnlyList<SurfaceMember> surface = await RequestSurfaceAsync();

        // Act
        (string[] unargued, string[] stale) = CompareToTextArguments(surface);

        // Assert — the fail-closed direction first: this is the one the leg exists for.
        await Assert.That(string.Join(Environment.NewLine, unargued)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(Environment.NewLine, stale)).IsEqualTo(string.Empty);

        // Non-vacuity. Both assertions above are satisfied by a walk that reached nothing and by an
        // argument list nobody wrote, which are the two ways this kind of census dies quietly.
        await Assert.That(TextMemberArguments).IsNotEmpty();
        await Assert.That(surface.Count(member => CarriesText(member.MemberType))).IsGreaterThan(30);

        // And the members closest to the line are reached BY THE WALK rather than named in a literal
        // handed to the comparison: the two envelopes a client really does send, and the verifier that
        // is a sibling branch of the key which must never be sent.
        string[] text =
        [
            .. surface
                .Where(member => CarriesText(member.MemberType))
                .Select(member => member.Qualified)
                .Distinct(StringComparer.Ordinal),
        ];
        await Assert.That(text).Contains("RegistrationEndpoints.RegistrationRequest.WrappedContentKey");
        await Assert.That(text).Contains("RecoveryCodeSubmission.Verifier");
    }

    /// <summary>
    /// The fail-closed control: a text member nobody argued for is reported, beside real ones that are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The probe is walked together with the live surface rather than on its own</b>, and that is what
    /// makes one run answer both halves. A comparison that reported everything satisfies the first
    /// assertion perfectly; a comparison that reported nothing satisfies none of them. Naming the probe's
    /// three members and then insisting a real, argued member is <em>absent</em> from the same array is
    /// the pair of verdicts this control exists to produce.
    /// </para>
    /// <para>
    /// The stale direction gets the same treatment from the opposite end: over the probe alone, every
    /// argument in the list is an argument for a member that is not there, and one of them is named.
    /// </para>
    /// <para>
    /// <see cref="ProbeRequest.WrappedContentKey" /> is deliberately among the reported three. It is the
    /// legal spelling the deny-list census lets through, which is exactly the point: this leg does not
    /// judge names at all, so a member being innocently named buys it nothing here.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RequestSurface_ArguesForEveryMemberThatCanCarryText_ReportsOneNobodyArguedFor()
    {
        // Arrange
        IReadOnlyList<SurfaceMember> probe = MembersOf(typeof(ProbeRequest), NameOf(typeof(ProbeRequest)));
        IReadOnlyList<SurfaceMember> surface = await RequestSurfaceAsync();

        // Act — the same comparison the census makes, over the surface with the probe alongside it, and
        // then over the probe alone.
        (string[] unargued, _) = CompareToTextArguments([.. probe, .. surface]);
        (_, string[] stale) = CompareToTextArguments(probe);

        // Assert — the three text members of the probe are named, whatever they are called.
        await Assert.That(unargued).Contains("KeyMaterialSecrecyTests.ProbeRequest.ContentKey");
        await Assert.That(unargued).Contains("KeyMaterialSecrecyTests.ProbeRequest.WrappedContentKey");
        await Assert.That(unargued).Contains("KeyMaterialSecrecyTests.ProbeNestedResults.PrfOutput");

        // And a real member that IS argued for is not, in the same call, so this cannot pass by a
        // comparison that reports every member it sees.
        await Assert.That(unargued)
            .DoesNotContain("RegistrationEndpoints.RegistrationRequest.WrappedContentKey");

        // The other direction, proven rather than assumed: over a surface holding only the probe, an
        // argument for a member of the real surface is an argument for a member that is gone.
        await Assert.That(stale).Contains("RegistrationEndpoints.RegistrationRequest.WrappedContentKey");
    }

    [Test]
    public async Task RequestSurface_ReportsAMemberThatCouldHoldUnwrappedKeyMaterial()
    {
        // Arrange — the control for the census above, and it is a probe rather than a mutation of a
        // real endpoint for the reason the schema probes are throwaway tables: a permanent control
        // fails on the day the mechanism breaks, whereas a mutation somebody ran once proves only
        // that it worked once. The probe is walked by the same recursion, one hop down, so it
        // exercises the nesting as well as the classification.
        IReadOnlyList<SurfaceMember> probe =
            MembersOf(typeof(ProbeRequest), NameOf(typeof(ProbeRequest)));

        // Act — the same call the census makes, over the same shape.
        string[] offenders =
        [
            .. probe
                .Where(member => UnwrappedKeyMaterialVocabulary.Classify(member.Member) is not null)
                .Select(member => $"{member.Owner}.{member.Member}")
                .Order(StringComparer.Ordinal),
        ];

        // Assert — the nested member is named, which is the half that proves the recursion is load
        // bearing rather than the classification alone. Both are qualified by this class because
        // NameOf qualifies a nested type by its declaring one, and the strings are written out as the
        // census would print them rather than assembled from nameof: a report nobody can grep for is
        // the failure mode this file is trying to avoid everywhere else.
        await Assert.That(offenders).Contains("KeyMaterialSecrecyTests.ProbeRequest.ContentKey");
        await Assert.That(offenders)
            .Contains("KeyMaterialSecrecyTests.ProbeNestedResults.PrfOutput");

        // And the deliberately innocent members of the same two records are NOT reported, so the
        // control cannot pass by a rule that fires on everything.
        await Assert.That(offenders)
            .DoesNotContain("KeyMaterialSecrecyTests.ProbeRequest.WrappedContentKey");
        await Assert.That(offenders)
            .DoesNotContain("KeyMaterialSecrecyTests.ProbeNestedResults.Enabled");
    }

    [Test]
    public async Task Schema_ClassifiesEveryBinaryColumn()
    {
        // Arrange — the live catalog on the container superuser connection, so row-level security
        // cannot decide what a catalog query is allowed to see. Deliberately not EF's model: the
        // point is to hold the schema to account, and a model that quietly failed to map a new
        // column would otherwise let this test agree with itself about a column that exists.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        IReadOnlyList<string> binaryColumns = await ReadBinaryColumnsAsync(admin);
        (string[] unclassified, string[] stale) = CompareToClassifications(binaryColumns);

        // Assert — SET EQUALITY, BOTH DIRECTIONS, and the two directions fail for different reasons.
        //
        // A discovered column nobody classified is the red this test exists for, and it FAILS CLOSED:
        // a new bytea column is exactly the shape key material arrives in, and it arrives under a name
        // no vocabulary can judge. Requiring a written argument is the only check that survives the
        // column being called `payload`.
        await Assert.That(string.Join(", ", unclassified)).IsEqualTo(string.Empty);

        // A classification naming a column that no longer exists is the same defect running the other
        // way. Without this direction the list rots into a claim about a schema that has moved, and
        // the rot is invisible — every remaining entry still passes, so the file keeps reading like a
        // complete account of a schema it has stopped describing.
        await Assert.That(string.Join(", ", stale)).IsEqualTo(string.Empty);

        // Non-vacuity. Both assertions above are satisfied by a query that returned nothing, which is
        // also what a broken join looks like. Naming the story's own two columns proves the scan
        // reached the catalog and reached this table in particular.
        Console.WriteLine($"Binary columns: {string.Join(", ", binaryColumns)}");
        await Assert.That(binaryColumns).Contains("wrapped_account_keys.wrapped_content_key");
        await Assert.That(binaryColumns).Contains("wrapped_account_keys.wrapped_index_key");
    }

    [Test]
    public async Task Schema_ClassifiesEveryBinaryColumn_ReportsAColumnNobodyClassified()
    {
        // Arrange — a throwaway relation carrying one binary column under a deliberately INNOCENT
        // name. `payload` trips no rule in the vocabulary and is exactly the gap that vocabulary
        // names in its own remarks, so this probe is the shape the name censuses provably cannot
        // catch. It is created on the admin connection and never dropped: every host owns a database
        // of its own, cloned from the migrated template and dropped with the host, so the probe is
        // invisible to every other test in the assembly. The prefix says what it is if the name ever
        // leaks into a shared database anyway.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(
            admin,
            $"create table {BinaryProbeTable} (id uuid primary key, payload bytea not null)");

        // Act — the same reader and the same comparison the census runs, knowing nothing about the
        // probe. A control that exercised a second, separately written query would prove that query
        // can fail and say nothing about the one that ships.
        IReadOnlyList<string> binaryColumns = await ReadBinaryColumnsAsync(admin);
        (string[] unclassified, _) = CompareToClassifications(binaryColumns);

        // Assert — fail-closed proven: an unargued binary column is reported by name.
        await Assert.That(unclassified).Contains($"{BinaryProbeTable}.payload");

        // And the probe's own `id` is not, so the control cannot be passing because the comparison
        // reports every column it sees.
        await Assert.That(unclassified).DoesNotContain($"{BinaryProbeTable}.id");
    }

    [Test]
    public async Task Schema_ClassifiesEveryBinaryColumn_ReportsAClassificationOfAColumnThatIsGone()
    {
        // Arrange — the second direction, proven over the LIVE CATALOG rather than over a literal set
        // handed to the comparison. A column this file classifies is dropped on this host's own
        // database, which is the real event the direction exists to catch — a migration removing a
        // column and leaving its argument behind, reading like a complete account of a schema that
        // has moved. `cascade` because the width and version checks are defined on this column and go
        // with it. The drop reaches nothing else: the database is cloned per host and dropped with it.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(
            admin,
            "alter table wrapped_account_keys drop column wrapped_index_key cascade");

        // Act
        IReadOnlyList<string> binaryColumns = await ReadBinaryColumnsAsync(admin);
        (_, string[] stale) = CompareToClassifications(binaryColumns);

        // Assert — the orphaned classification is named.
        await Assert.That(stale).Contains("wrapped_account_keys.wrapped_index_key");

        // And its surviving sibling is not, so this cannot be passing because the comparison reports
        // the whole classification list whenever anything moves.
        await Assert.That(stale).DoesNotContain("wrapped_account_keys.wrapped_content_key");
    }

    /// <summary>
    /// Every classification says what its column holds and why holding it unwraps nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The set comparison above compares <see cref="BinaryColumnClassification.Qualified" /> and
    /// nothing else</b>, so both prose members are read by no assertion at all: a new <c>bytea</c> column
    /// entered as <c>new("t", "c", "", "")</c> satisfies it completely. That is the whole requirement
    /// gone — the record's own remarks say <see cref="BinaryColumnClassification.UnwrapsNothingBecause" />
    /// is the member carrying FR-064, because the requirement is not that these columns are binary but
    /// that no value the server holds is one by which a wrapped key can be unwrapped, and only prose can
    /// make that claim per column. A list nobody has to argue on is a list that agrees with whatever
    /// arrives next.
    /// </para>
    /// <para>
    /// A length floor rather than a judgement of the words, which is what
    /// <c>UnwrappedKeyMaterialVocabularyTests.Vocabulary_StatesAReasonForEveryRule</c> does next door for
    /// the same reason: no assertion can tell a real argument from a fluent one, and the cheapest way to
    /// write nothing is to write nothing. The floors differ because the two members answer different
    /// questions — <see cref="BinaryColumnClassification.Holds" /> is a fact in one clause, the reason is
    /// an argument the next reader has to be able to disagree with, and "not a key" is four words that
    /// restate the verdict.
    /// </para>
    /// <para>
    /// No database, deliberately: the list is the requirement and the catalog is only what stops it
    /// lying, so this half of it is checkable with nothing running.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Classifications_StateWhatEachColumnHoldsAndWhyItUnwrapsNothing()
    {
        // Arrange
        IReadOnlyList<BinaryColumnClassification> classifications = Classifications;

        // Act — reported by column rather than counted, so a failure names the entry to argue about.
        string[] unstated =
        [
            .. classifications
                .Where(entry => string.IsNullOrWhiteSpace(entry.Holds)
                                || entry.Holds.Length < MinimumHoldsLength)
                .Select(entry => $"{entry.Qualified} holds: <unstated>")
                .Order(StringComparer.Ordinal),
        ];

        string[] unargued =
        [
            .. classifications
                .Where(entry => string.IsNullOrWhiteSpace(entry.UnwrapsNothingBecause)
                                || entry.UnwrapsNothingBecause.Length < MinimumReasonLength)
                .Select(entry => $"{entry.Qualified} unwraps nothing because: <unargued>")
                .Order(StringComparer.Ordinal),
        ];

        // Assert — the non-empty check first: an empty list has no unargued entry either, and would pass
        // both assertions below with nothing in it. That is not hypothetical here, since the list is
        // hand-written and its only other reader is a set comparison that an empty schema also satisfies.
        await Assert.That(classifications).IsNotEmpty();
        await Assert.That(string.Join(", ", unstated)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", unargued)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Schema_HoldsNoColumnNamedForUnwrappedKeyMaterial()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — every row-bearing relation in `public` and every column it carries, both put through
        // the same vocabulary the request-surface census reads. A relation name is classified by the
        // same call as a column name because it is the same question: this refusal arrives at both
        // grains, and `account_keys` would be a table where `content_key` would be a column.
        SchemaIdentifiers identifiers = await ReadPublicIdentifiersAsync(admin);
        IReadOnlyList<SchemaOffender> offenders = KeyMaterialOffenders(identifiers);

        // Assert — the shipped schema stores wrapped keys and stores nothing that opens one.
        await Assert.That(Describe(offenders)).IsEqualTo(string.Empty);

        // The story's own two names, proven clean BY THE SCAN rather than by a literal handed to the
        // classifier. This is the half that would otherwise be vacuous: an empty offender set is what
        // a scan that never reached these columns also produces, and these two are the names in the
        // schema closest to the line — `content_key` and `index_key` are refused outright, and it is
        // one adjacent word that makes each of them legal. If the qualifier mechanism ever breaks,
        // this test reds on the two columns the story just shipped rather than going quiet.
        await Assert.That(identifiers.Columns).Contains("wrapped_account_keys.wrapped_content_key");
        await Assert.That(identifiers.Columns).Contains("wrapped_account_keys.wrapped_index_key");

        // The relation grain likewise: `wrapped_account_keys` reaches the `account_key` rule only in
        // its plural form and is let go only by the qualifier's singular, so it is the one name that
        // needs both halves of the vocabulary's pluralisation to be right.
        await Assert.That(identifiers.Relations).Contains("wrapped_account_keys");
        await Assert.That(identifiers.Relations).Contains("recovery_code_hashes");
    }

    [Test]
    public async Task Schema_HoldsNoColumnNamedForUnwrappedKeyMaterial_ReportsATableThatGrowsSuchAColumn()
    {
        // Arrange — a throwaway relation whose own name is deliberately innocent, carrying the one
        // column that is not. `content_key` is the sharpest available probe: it differs from the
        // shipped, legal `wrapped_content_key` by exactly the qualifier, so a control that reports it
        // proves the vocabulary distinguishes the two rather than waving the pair through together.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(
            admin,
            $"create table {ColumnProbeTable} (id uuid primary key, content_key text not null)");

        // Act — the same scan, unchanged and knowing nothing about the probe. The probe column is
        // `text` rather than `bytea` on purpose: the offence has to be the NAME alone, or this
        // control would also be passing for the schema census's reason.
        SchemaIdentifiers identifiers = await ReadPublicIdentifiersAsync(admin);
        string[] offenders = Identifiers(KeyMaterialOffenders(identifiers));

        // Assert — reported as table.column, so the failure it produces in anger says which row grew
        // the column. The probe's own name matches nothing, so the column is the only thing this can
        // be seeing.
        await Assert.That(offenders).Contains($"{ColumnProbeTable}.content_key");
        await Assert.That(offenders).DoesNotContain(ColumnProbeTable);

        // And the legal spelling in the shipped schema is still not reported, in the same scan that
        // just refused the bare one. Two facts on one database is what makes this a statement about
        // the qualifier rather than two statements about two vocabularies.
        await Assert.That(offenders)
            .DoesNotContain("wrapped_account_keys.wrapped_content_key");
    }

    [Test]
    public async Task Schema_HoldsNoColumnNamedForUnwrappedKeyMaterial_ReportsATableWhoseOwnNameIsOne()
    {
        // Arrange — the relation half. Its columns are deliberately ordinary and its own name is not,
        // so the relation path is the only thing its assertion can be seeing. Its own host, for the
        // reason its sibling has one: a database carrying both probes would let each assertion pass
        // on the other's offence.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(
            admin,
            $"create table {RelationProbeTable} (id uuid primary key, user_id uuid not null)");

        // Act
        SchemaIdentifiers identifiers = await ReadPublicIdentifiersAsync(admin);
        string[] offenders = Identifiers(KeyMaterialOffenders(identifiers));

        // Assert — bare, with no dot: the offence is the relation itself rather than anything it
        // carries, and reporting it as a column would name a column that does not exist.
        await Assert.That(offenders).Contains(RelationProbeTable);
        await Assert.That(offenders).DoesNotContain($"{RelationProbeTable}.user_id");
    }

    /// <summary>
    /// What one <c>bytea</c> column holds, and the argument that a server holding it can unwrap
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="UnwrapsNothingBecause" /> is the member that carries FR-064. The requirement is
    /// not "these columns are binary" — it is that <b>no value the server holds is one by which a
    /// wrapped key can be unwrapped</b>, and that is a claim about each value in turn which only prose
    /// can make. A reason reading "not a key" restates the verdict; the reason has to say what stands
    /// between the stored bytes and the key-encryption key, so that the next reader can disagree with
    /// it.
    /// </para>
    /// <para>
    /// <paramref name="Holds" /> is separate from the reason because the two answer different
    /// questions and a single field would collapse them: what the bytes are is a fact, and whether
    /// holding them is safe is an argument. A column whose <c>Holds</c> nobody can write in one clause
    /// is already the defect.
    /// </para>
    /// </remarks>
    /// <param name="Table">The relation, as PostgreSQL spells it.</param>
    /// <param name="Column">The column, as PostgreSQL spells it.</param>
    /// <param name="Holds">What the bytes are, in one clause.</param>
    /// <param name="UnwrapsNothingBecause">
    /// What stands between these bytes and a key that opens an envelope.
    /// </param>
    private sealed record BinaryColumnClassification(
        string Table,
        string Column,
        string Holds,
        string UnwrapsNothingBecause)
    {
        /// <summary>The key both directions of the set comparison are made on.</summary>
        public string Qualified => $"{Table}.{Column}";
    }

    /// <summary>
    /// Every <c>bytea</c> column the schema is allowed to carry, each with what it holds and why
    /// holding it unwraps nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This list is the requirement, and the query is only what stops it lying.</b> Written down
    /// rather than derived, deliberately and in the opposite direction from the request surface above:
    /// a derived list of binary columns would be a restatement of the catalog and could never
    /// disagree with it. What has to be authored is the <i>argument</i>, one per column, and the set
    /// comparison exists to make authoring one unavoidable — a column with no entry is a red, and an
    /// entry with no column is a red.
    /// </para>
    /// <para>
    /// The eight divide into four kinds, and the kinds are worth seeing. Two are the envelopes
    /// themselves — the only key-shaped thing this design lets cross the wire, and safe because the
    /// server holds nothing that opens them. Two are WebAuthn's own material, a handle that selects a
    /// credential and a <i>public</i> key published by design. Three are one-way values, two hashes and
    /// a nonce, from which nothing is derived. No fifth kind exists, and a ninth column would have to
    /// argue itself into one of the four or invent a fifth in writing.
    /// </para>
    /// <para>
    /// <b>The fourth kind is the newest and it arrived exactly as this list said one would</b> — the
    /// paragraph above used to end at three, and <c>budgets.name</c> is the eighth column that had to
    /// invent a kind in writing rather than squeeze into an existing one. It is <i>content</i>: an AEAD
    /// envelope over a person's own words, sealed under the account's content key. That inverts the
    /// envelope kind rather than joining it. Those two columns are the key and are safe because nothing
    /// on this server opens them; this one is safe <i>because one of them is the thing that opens it</i>,
    /// so the two arguments hold each other up and neither can be pasted over the other. It is also the
    /// first entry whose argument has to concede something — AES-GCM leaks the plaintext's length, and
    /// the column's own length already does, so the concession costs nothing and is written down rather
    /// than left for a reader to notice. Every narrative column sealed after this one is a member of this
    /// kind and owes the same two sentences.
    /// </para>
    /// <para>
    /// <c>session_tokens.token_hash</c> is the newest of the one-way three and the one whose argument is
    /// least like its neighbour's, which is why the two are written out separately rather than pointed at
    /// each other. <c>recovery_code_hashes.verifier_hash</c> has to argue that a <i>sibling branch of the
    /// same secret</i> is safe to hold; this one has to argue only that its input stands in no relation to
    /// the key hierarchy at all.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<BinaryColumnClassification> Classifications { get; } =
    [
        new(
            "budgets",
            "name",
            "a budget's name sealed as a narrative field — an AEAD envelope of version, nonce, "
            + "ciphertext and tag, produced in the browser under the account's content key",
            "the content key that seals it is generated in the browser and reaches this server only "
            + "as the wrapped_content_key envelopes next door, each sealed under a key-encryption key "
            + "derived from a recovery factor the operator never holds — so the row and everything "
            + "that could open it are separated by a step that happens on somebody's device. This is "
            + "the reverse of the wrapped-key argument rather than a copy of it: those columns are the "
            + "key and are safe because nothing here opens them, this one is CONTENT and is safe "
            + "because the thing that opens it is one of those. What AES-GCM leaks without the key is "
            + "the plaintext's length, and the column's own length already gives that away, so sealing "
            + "buys nothing against a length oracle and was never claimed to. The tag is the other "
            + "half: the associated data is rebuilt from where the ciphertext was found, so an "
            + "operator who moved one budget's name onto another row would produce a value that "
            + "refuses to open rather than one that opens as somebody else's"),
        new(
            "wrapped_account_keys",
            "wrapped_content_key",
            "the account's content key sealed under one factor's key-encryption key — the 61-byte "
            + "envelope of version, nonce, ciphertext and tag",
            "the key-encryption key it is sealed under is derived in the browser from a recovery "
            + "factor and imported non-extractable, so it exists nowhere this row can be read from. "
            + "AES-GCM without the key yields nothing but the fact that 32 bytes were sealed, which "
            + "the column's fixed width already says"),
        new(
            "wrapped_account_keys",
            "wrapped_index_key",
            "the account's index key in the same envelope, sealed under the same factor",
            "the same argument, and one more: the two envelopes carry DIFFERENT associated data, so "
            + "even an operator who obtained one factor's key-encryption key could not move a copy "
            + "between the two columns without the tag failing to verify"),
        new(
            "passkey_public_keys",
            "public_key_cose",
            "the COSE encoding of a passkey's public key",
            "it is a PUBLIC key, published by design. It verifies an assertion signature and decrypts "
            + "nothing, and it is not the counterpart of anything in this design's key hierarchy — "
            + "the key-encryption key is symmetric and derived from the authenticator's PRF output, "
            + "which no public key stands in any relation to"),
        new(
            "passkey_public_keys",
            "webauthn_credential_id",
            "the authenticator's opaque handle for one credential",
            "it SELECTS a key and is not one. The authenticator uses it to find the private key it "
            + "will sign with, and neither that private key nor the PRF output evaluated beside it "
            + "is derivable from the handle — an authenticator that leaked either on presentation of "
            + "a handle would have failed WebAuthn, not this design"),
        new(
            "recovery_code_hashes",
            "verifier_hash",
            "SHA-256 of a verifier the client derived from one recovery code",
            "two one-way steps and a different HKDF `info` stand between it and the key-encryption "
            + "key. The verifier and that key are sibling branches over the same code, so recovering "
            + "the key from this column means inverting SHA-256 to reach the verifier and then "
            + "inverting HKDF to reach the code. That is the whole reason a hash of a verifier is "
            + "storable where a code is not"),
        new(
            "session_tokens",
            "token_hash",
            "SHA-256 of the 32-byte session token a cookie presents, and the identity of the row",
            "it is one-way and it stands in no relation to the key hierarchy at all. The token it "
            + "digests is a uniform value this server mints to name a session row; it is an input to "
            + "no KDF, no PRF eval and no wrapping step, so inverting SHA-256 would yield a handle "
            + "that opens a session and still not one byte a wrapped envelope could be unsealed with. "
            + "That is a different argument from recovery_code_hashes' next door, which has to say "
            + "why a sibling branch of the same secret is safe to store"),
        new(
            "webauthn_challenges",
            "challenge",
            "the 32-byte nonce the server minted for one ceremony",
            "nothing is derived from it. The client signs over it and the server compares it and "
            + "expires it; it is an input to no KDF on either side, and the PRF eval input the "
            + "authenticator is asked for is a fixed domain string rather than this value"),
    ];

    /// <summary>
    /// The shortest a clause saying what a column holds may be.
    /// </summary>
    /// <remarks>
    /// Low, because the member is one clause of fact and a floor that forced padding would buy nothing.
    /// It is above every one-word placeholder — <c>bytes</c>, <c>a key</c>, <c>opaque</c> — which is the
    /// whole job: a column whose contents nobody can write in a clause is already the defect.
    /// </remarks>
    private const int MinimumHoldsLength = 24;

    /// <summary>
    /// The shortest an argument that a column unwraps nothing may be.
    /// </summary>
    /// <remarks>
    /// Higher than <see cref="MinimumHoldsLength" /> and higher than the vocabulary's floor next door,
    /// because this member carries FR-064 and the sentence it has to beat is a restatement of the
    /// verdict: <c>not a key</c>, <c>it is a hash</c>, <c>safe to store</c> all fit in a clause. Saying
    /// what stands between the stored bytes and a key-encryption key does not.
    /// </remarks>
    private const int MinimumReasonLength = 80;

    /// <summary>
    /// One member of the request surface that can carry text, and what a client puts in it.
    /// </summary>
    /// <param name="Owner">The declaring type, spelled the way the surface walk reports it.</param>
    /// <param name="Member">The member name, in the casing the CLR reports it.</param>
    /// <param name="Carries">
    /// What the value <b>is</b>, and — where the answer is not obvious from that — what keeps it from
    /// being something a wrapped key could be opened with. One member rather than the two
    /// <see cref="BinaryColumnClassification" /> carries, because most of this surface is a person's own
    /// typing and splitting a fact from an argument there would produce fifty restatements of "nobody's
    /// key". The members where the argument is real are the ones that say it.
    /// </param>
    private sealed record TextMemberArgument(string Owner, string Member, string Carries)
    {
        /// <summary>The key both directions of the set comparison are made on.</summary>
        public string Qualified => $"{Owner}.{Member}";
    }

    /// <summary>
    /// Every member of the request surface that can carry text, and what each one holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written down rather than derived, and in the opposite direction from the surface it is compared
    /// against.</b> A derived list would restate the reflection walk and could never disagree with it.
    /// What has to be authored is the sentence, one per member, and the set comparison is what makes
    /// authoring one unavoidable.
    /// </para>
    /// <para>
    /// The WebAuthn assertion's five members recur on four request records — erasure, revocation,
    /// sign-in, and issuing a card — and each copy is entered separately rather than pointed at a shared
    /// argument. That is deliberate in the same way the two hash columns above are: the day one of those
    /// routes takes a sixth member, the entry beside it is where a reader looks, and a shared argument
    /// would have to be widened in a place that answers for four routes at once.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<TextMemberArgument> TextMemberArguments { get; } =
    [
        new("AccountEndpoints.UpdateAccountRequest", "Name",
            "the name a person gave one of their accounts, as they typed it"),
        new("AccountErasureEndpoints.ErasureRequest", "AuthenticatorData",
            "base64url over the authenticator's signed bytes: a relying-party hash, flags and a counter"),
        new("AccountErasureEndpoints.ErasureRequest", "ClientDataJson",
            "base64url over the JSON the browser signed — type, challenge, origin — a public transcript"),
        new("AccountErasureEndpoints.ErasureRequest", "CredentialId",
            "base64url over the authenticator's opaque handle, which selects a key and is not one"),
        new("AccountErasureEndpoints.ErasureRequest", "Signature",
            "base64url over an assertion signature, verified with a published public key"),
        new("AccountErasureEndpoints.ErasureRequest", "UserHandle",
            "base64url over the sixteen bytes of the account id the authenticator kept"),
        new("AccountKeyEndpoints.AccountKeyEntry", "WrappedContentKey",
            "a response member: base64url over the 61-byte envelope holding the account's CONTENT key, "
            + "sealed under the key-encryption key of one factor of the credential that opened this "
            + "session. It is key material leaving the server, and that is this route rather than a leak: "
            + "the key that would open it is derived in the browser from a recovery factor — an "
            + "authenticator's prf output, or a recovery code — and neither of those ever reaches this "
            + "server, so the operator handing these bytes back cannot unseal them and never could. What "
            + "the census refuses is an UNSEALED key crossing the wire; a sealed one crossing is the "
            + "design"),
        new("AccountKeyEndpoints.AccountKeyEntry", "WrappedIndexKey",
            "a response member: base64url over the same envelope holding the account's INDEX key — the "
            + "one a blind index over a name is computed under — sealed under the SAME factor's "
            + "key-encryption key and bound to a different purpose in its associated data. Written out "
            + "rather than pointed at its neighbour because the two are the same width, carry the same "
            + "version and are indistinguishable to every check this server owns: one argument covering "
            + "both would be a single sentence answering for two values nothing here can tell apart, "
            + "which is the confusion that associated data exists to prevent"),
        new("CategoryEndpoints.UpdateCategoryRequest", "Description",
            "a person's own note about one of their categories"),
        new("CategoryEndpoints.UpdateCategoryRequest", "Name",
            "the name a person gave one of their categories, as they typed it"),
        new("CategoryGroupEndpoints.UpdateCategoryGroupRequest", "Description",
            "a person's own note about one of their category groups"),
        new("CategoryGroupEndpoints.UpdateCategoryGroupRequest", "Name",
            "the name a person gave one of their category groups, as they typed it"),
        new("CreateAccountCommand", "CurrencyCode",
            "an ISO 4217 code chosen from the currencies this product seeds"),
        new("CreateAccountCommand", "Name",
            "the name a person is giving a new account, as they typed it"),
        new("CreateCategoryCommand", "Description",
            "a person's own note about a category they are creating"),
        new("CreateCategoryCommand", "Name",
            "the name a person is giving a new category, as they typed it"),
        new("CreateCategoryGroupCommand", "Description",
            "a person's own note about a category group they are creating"),
        new("CreateCategoryGroupCommand", "Name",
            "the name a person is giving a new category group, as they typed it"),
        new("CreateTransactionCommand", "Description",
            "a person's own note about one transaction, as they typed it"),
        new("CreateTransactionCommand", "PayeeName",
            "who a person says they paid, as they typed it"),
        new("CredentialEndpoints.CredentialListEntry", "Type",
            "a response member: one credential's type, as CredentialTypeSpelling writes it"),
        new("CredentialEndpoints.RevocationRequest", "AuthenticatorData",
            "base64url over the authenticator's signed bytes: a relying-party hash, flags and a counter"),
        new("CredentialEndpoints.RevocationRequest", "ClientDataJson",
            "base64url over the JSON the browser signed — type, challenge, origin — a public transcript"),
        new("CredentialEndpoints.RevocationRequest", "CredentialId",
            "base64url over the authenticator's opaque handle, which selects a key and is not one"),
        new("CredentialEndpoints.RevocationRequest", "Signature",
            "base64url over an assertion signature, verified with a published public key"),
        new("CredentialEndpoints.RevocationRequest", "UserHandle",
            "base64url over the sixteen bytes of the account id the authenticator kept"),
        new("Optional`1", "Value",
            "the payload of the wrapper a partial update uses; whatever the member holding it carries, "
            + "argued at that member"),
        new("PasskeyEndpoints.AssertionRequest", "AuthenticatorData",
            "base64url over the authenticator's signed bytes: a relying-party hash, flags and a counter"),
        new("PasskeyEndpoints.AssertionRequest", "ClientDataJson",
            "base64url over the JSON the browser signed — type, challenge, origin — a public transcript"),
        new("PasskeyEndpoints.AssertionRequest", "CredentialId",
            "base64url over the authenticator's opaque handle, which selects a key and is not one"),
        new("PasskeyEndpoints.AssertionRequest", "Signature",
            "base64url over an assertion signature, verified with a published public key"),
        new("PasskeyEndpoints.AssertionRequest", "UserHandle",
            "base64url over the sixteen bytes of the account id the authenticator kept"),
        new("PasskeyEndpoints.AssertionResponse", "Kind",
            "a response member: the kind of session this sign-in established, as SessionKind spells it"),
        new("PasskeyEndpoints.RegistrationRequest", "AttestationObject",
            "base64url over the CBOR attestation: authenticator data and a public key, both publishable"),
        new("PasskeyEndpoints.RegistrationRequest", "ClientDataJson",
            "base64url over the JSON the browser signed — type, challenge, origin — a public transcript"),
        new("PasskeyEndpoints.RegistrationRequest", "FactorId",
            "the client-minted identifier the two envelopes below are bound to as associated data. It is "
            + "written back to every caller that asks, so it is a name rather than a secret"),
        new("PasskeyEndpoints.RegistrationRequest", "WrappedContentKey",
            "base64url over a 61-byte envelope. The key-encryption key that sealed it is derived in the "
            + "browser from the authenticator's prf output and imported non-extractable, so it never "
            + "reaches this member or any other"),
        new("PasskeyEndpoints.RegistrationRequest", "WrappedIndexKey",
            "base64url over the same envelope for the index key, sealed under the same non-extractable "
            + "key and bound to a different purpose"),
        new("PayeeEndpoints.RenamePayeeRequest", "Name",
            "who a person says they paid, as they typed it"),
        new("RecoveryCodeEndpoints.RecoveryCodeGenerationRequest", "AuthenticatorData",
            "base64url over the authenticator's signed bytes: a relying-party hash, flags and a counter"),
        new("RecoveryCodeEndpoints.RecoveryCodeGenerationRequest", "ClientDataJson",
            "base64url over the JSON the browser signed — type, challenge, origin — a public transcript"),
        new("RecoveryCodeEndpoints.RecoveryCodeGenerationRequest", "CredentialId",
            "base64url over the authenticator's opaque handle, which selects a key and is not one"),
        new("RecoveryCodeEndpoints.RecoveryCodeGenerationRequest", "Signature",
            "base64url over an assertion signature, verified with a published public key"),
        new("RecoveryCodeEndpoints.RecoveryCodeGenerationRequest", "UserHandle",
            "base64url over the sixteen bytes of the account id the authenticator kept"),
        new("RecoveryCodeEndpoints.RedemptionRequest", "Verifier",
            "base64url over one HKDF branch of a recovery code, and the code itself never crosses the "
            + "wire. The key-encryption key is a sibling branch over the same code under a different "
            + "`info`, so holding this one yields nothing about that one"),
        new("RecoveryCodeEndpoints.RedemptionResponse", "Kind",
            "a response member: the kind of session a redemption established, as SessionKind spells it"),
        new("RecoveryCodeEndpoints.ReestablishedSessionResponse", "Kind",
            "a response member: the kind of session an issue re-established, as SessionKind spells it"),
        new("RecoveryCodeSubmission", "FactorId",
            "the client-minted identifier one code's two envelopes are bound to as associated data, and "
            + "a name rather than a secret"),
        new("RecoveryCodeSubmission", "Verifier",
            "base64url over one HKDF branch of one recovery code, the same value a redemption presents "
            + "and a sibling of the key branch that stays in the browser"),
        new("RecoveryCodeSubmission", "WrappedContentKey",
            "base64url over a 61-byte envelope sealed under the key that code derives, which is imported "
            + "non-extractable and never leaves the browser"),
        new("RecoveryCodeSubmission", "WrappedIndexKey",
            "base64url over the same envelope for the index key, sealed under the same key and bound to "
            + "a different purpose"),
        new("RegistrationEndpoints.EstablishedSessionResponse", "Kind",
            "a response member: the kind of session registration established, as SessionKind spells it"),
        new("RegistrationEndpoints.RegistrationRequest", "AttestationObject",
            "base64url over the CBOR attestation: authenticator data and a public key, both publishable"),
        new("RegistrationEndpoints.RegistrationRequest", "ClientDataJson",
            "base64url over the JSON the browser signed — type, challenge, origin — a public transcript"),
        new("RegistrationEndpoints.RegistrationRequest", "FactorId",
            "the client-minted identifier the passkey factor's two envelopes are bound to as associated "
            + "data, and a name rather than a secret"),
        new("RegistrationEndpoints.RegistrationRequest", "WrappedContentKey",
            "base64url over a 61-byte envelope sealed under the passkey factor's key-encryption key, "
            + "which is derived from prf output in the browser and imported non-extractable"),
        new("RegistrationEndpoints.RegistrationRequest", "WrappedIndexKey",
            "base64url over the same envelope for the index key, sealed under the same key and bound to "
            + "a different purpose"),
        new("TransactionEndpoints.UpdateTransactionRequest", "Description",
            "a person's own note about one transaction, present or absent, as they typed it"),
        new("TransactionEndpoints.UpdateTransactionRequest", "PayeeName",
            "who a person says they paid, present or absent, as they typed it"),
    ];

    /// <summary>
    /// The shortest a sentence saying what a text member carries may be.
    /// </summary>
    /// <remarks>
    /// A floor rather than a judgement of the words, for the reason
    /// <see cref="MinimumReasonLength" /> is one: no assertion can tell a real argument from a fluent
    /// one, and the cheapest way to write nothing is to write nothing. Set above every one-word
    /// placeholder a reader would reach for — <c>text</c>, <c>a name</c>, <c>opaque</c>, <c>not a
    /// key</c> — and below the shortest honest entry in the list.
    /// </remarks>
    private const int MinimumCarriesLength = 40;

    /// <summary>
    /// The binary half of the fail-closed control: a relation carrying one <c>bytea</c> column under a
    /// name that trips <b>no</b> rule in the vocabulary. The innocence is the point — it makes the
    /// probe the exact shape the two name censuses cannot see, so a green from them and a red from
    /// this one is the pair of verdicts the schema census exists to produce.
    /// </summary>
    private const string BinaryProbeTable = "key_material_probe_binary_holder";

    /// <summary>
    /// The column half of the name-census probe pair: a relation whose <b>own name is deliberately
    /// innocent</b> — it tokenizes as <c>key / material / probe / holder</c>, and the bare token
    /// <c>key</c> is legal by argument — so the forbidden column it carries is the only thing its
    /// control's assertion can be seeing.
    /// </summary>
    private const string ColumnProbeTable = "key_material_probe_holder";

    /// <summary>
    /// The relation half of the pair: a name carrying the forbidden run <c>prf / output</c> over
    /// <b>deliberately innocent columns</b>, so the relation path is the only thing its control's
    /// assertion can be seeing. The two are created on separate hosts so neither appears in the
    /// other's scan.
    /// </summary>
    private const string RelationProbeTable = "key_material_probe_prf_output";

    /// <summary>One member of one type on the request surface.</summary>
    /// <param name="Owner">
    /// The declaring type, nested types qualified by the endpoint class that declares them — a
    /// private nested record has no name this project can write in a <c>typeof</c>, so this string is
    /// what the census's own guards name it by.
    /// </param>
    /// <param name="Member">The member name, in the casing the CLR reports it.</param>
    /// <param name="MemberType">
    /// The member's declared type, which the name censuses ignore and
    /// <see cref="RequestSurface_ArguesForEveryMemberThatCanCarryText" /> is entirely about.
    /// </param>
    private sealed record SurfaceMember(string Owner, string Member, Type MemberType)
    {
        /// <summary>The key both directions of the text-member comparison are made on.</summary>
        public string Qualified => $"{Owner}.{Member}";
    }

    /// <summary>Every relation name and every column name in <c>public</c>, read once.</summary>
    /// <param name="Relations">Bare relation names.</param>
    /// <param name="Columns">Columns as <c>table.column</c>.</param>
    private sealed record SchemaIdentifiers(
        IReadOnlyList<string> Relations,
        IReadOnlyList<string> Columns);

    /// <summary>One refused identifier and the rule that refused it.</summary>
    /// <param name="Identifier">A bare relation name, or a column as <c>table.column</c>.</param>
    /// <param name="Rule">The rule it trips, carrying the argument a reviewer has to answer.</param>
    private sealed record SchemaOffender(string Identifier, UnwrappedKeyMaterialRule Rule);

    /// <summary>
    /// Every member of every type reachable from the API's request surface, with the type it sits on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two root sets, unioned, because either alone has a hole the other closes.</b> The nested
    /// records of the <c>*Endpoints</c> classes are the obvious surface, and they miss every route
    /// that binds an Application command straight from the body — <c>POST /api/transactions</c> takes
    /// a <c>CreateTransactionCommand</c>, which is nested in nothing. The route table's own delegate
    /// parameters catch those, and would miss nothing except that reading them depends on the route
    /// table being buildable. Taking both means a hole has to open in two places at once.
    /// </para>
    /// <para>
    /// The route-parameter root set is filtered to <b>records</b>, which is a structural property
    /// rather than a naming convention: every one of the forty-one handlers is a plain class, so the
    /// filter excludes each of them without a rule about suffixes that the forty-second could be
    /// written to slip past. Below a root the recursion widens deliberately — see
    /// <see cref="MembersOf" />.
    /// </para>
    /// <para>
    /// Response records are swept in with the requests rather than filtered out, and the filter is
    /// what is being avoided: telling a request from a response means matching a name suffix, and a
    /// body record called <c>RegistrationBody</c> would walk straight past it. The extra verdicts cost
    /// nothing, since no response in this API carries a key-shaped member and the legal spelling of
    /// one would be legal in either direction.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<SurfaceMember>> RequestSurfaceAsync()
    {
        // A connection string that resolves to nothing, on the Production environment: the route
        // table is built from the app model and no database is touched to read it, which is the same
        // arrangement CompositionBoundaryTests makes for the same reason.
        await using ApiFactory factory = new(
            "Host=localhost;Port=5432;Database=unused;Username=postgres;Password=postgres",
            environment: "Production");

        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        IEnumerable<Type> nested = typeof(TransactionEndpoints).Assembly
            .GetTypes()
            .Where(type => type.Name.EndsWith("Endpoints", StringComparison.Ordinal))
            .SelectMany(type =>
                type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

        IEnumerable<Type> bound = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                endpoint.Metadata.GetMetadata<MethodInfo>()?.GetParameters() ?? [])
            .Select(parameter => parameter.ParameterType)
            .Where(IsRecord);

        List<SurfaceMember> members = [];
        HashSet<Type> visited = [];

        foreach (Type root in nested.Concat(bound).Where(IsOwned))
        {
            members.AddRange(MembersOf(root, NameOf(root), visited));
        }

        return members;
    }

    /// <summary>
    /// Every member of <paramref name="type" /> and of every owned type reachable through its members.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The recursion is the part a top-level census would get wrong.</b>
    /// <c>PasskeyClientExtensionResults</c> carries a <c>PasskeyPrfResults</c>, and the member on that
    /// second type — <c>Enabled</c>, legal — is one WebAuthn spelling away from the member that would
    /// not be. A verdict about it can only be reached by walking into it.
    /// </para>
    /// <para>
    /// <b>Member names are classified and type names deliberately are not.</b> The type is a container
    /// and the member is what holds bytes, and the difference is not academic here:
    /// <c>PasskeyPrfResults</c> tokenizes as <c>passkey / prf / results</c>, which contains the run
    /// the <c>prf_result</c> rule refuses — so a census that classified type names would red on the
    /// correctly designed record that carries a single boolean. A rule that fires on correct code is
    /// the shape of red that teaches a reviewer to stop believing the check.
    /// </para>
    /// <para>
    /// Below a root the recursion widens from records to <b>any owned non-enum type</b>, which is the
    /// fail-closed direction: <c>Optional&lt;T&gt;</c> is a plain readonly struct and a future request
    /// member could as easily hold a class. Nothing that is not a data shape is reachable from a data
    /// shape, so the widening costs no false ground. Enums are skipped because their members are
    /// values rather than places bytes can sit, and the member that names one is classified anyway.
    /// </para>
    /// <para>
    /// The visited set makes the walk terminate on a self-referential shape and also means each type
    /// is reported once however many members point at it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<SurfaceMember> MembersOf(
        Type type,
        string name,
        HashSet<Type>? visited = null)
    {
        visited ??= [];

        if (!visited.Add(type))
        {
            return [];
        }

        List<SurfaceMember> members = [];

        foreach (PropertyInfo property in
                 type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            members.Add(new SurfaceMember(name, property.Name, property.PropertyType));

            foreach (Type payload in PayloadTypes(property.PropertyType))
            {
                if (IsOwned(payload) && !payload.IsEnum && !payload.IsGenericTypeDefinition)
                {
                    members.AddRange(MembersOf(payload, NameOf(payload), visited));
                }
            }
        }

        return members;
    }

    /// <summary>
    /// The types a member of <paramref name="type" /> could carry a value of: the type itself, what a
    /// nullable wraps, what a sequence yields, and any generic argument.
    /// </summary>
    /// <remarks>
    /// Broad on purpose — a key-shaped member reached through a list, a dictionary value or a wrapper
    /// struct is the same member. <see cref="string" /> returns nothing rather than
    /// <see cref="char" />, which is the one case where following the sequence would walk into the
    /// framework for no verdict.
    /// </remarks>
    private static IEnumerable<Type> PayloadTypes(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            yield return underlying;
            yield break;
        }

        if (type == typeof(string))
        {
            yield break;
        }

        yield return type;

        if (type.IsArray && type.GetElementType() is { } element)
        {
            yield return element;
        }

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in type.GetGenericArguments())
        {
            yield return argument;
        }
    }

    /// <summary>
    /// Whether the type was declared by this solution, as opposed to the framework.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from anchor types rather than from assembly name strings, so a rename cannot silently
    /// empty the set — an anchor that stopped resolving is a compile error rather than a census that
    /// walks nothing. Infrastructure is absent because <c>CompositionBoundaryTests</c> already forbids
    /// a route delegate from taking a persistence port, so nothing from it is reachable from the
    /// request surface.
    /// </para>
    /// <para>
    /// <b>This assembly is the fourth anchor, and it buys the control rather than the census.</b>
    /// <see cref="ProbeRequest" /> is declared here, so without it the recursion would stop at the
    /// probe's top level and the control would silently prove half of what it claims. It widens the
    /// census by nothing: every root comes from the Api assembly, and no type the Api declares can
    /// reference one declared in a test project.
    /// </para>
    /// </remarks>
    private static bool IsOwned(Type type) =>
        type.Assembly == typeof(TransactionEndpoints).Assembly
        || type.Assembly == typeof(Optional<>).Assembly
        || type.Assembly == typeof(IAccountRepository).Assembly
        || type.Assembly == typeof(KeyMaterialSecrecyTests).Assembly;

    /// <summary>
    /// Whether the type is a <c>record</c>, by the compiler-generated clone member every record class
    /// carries.
    /// </summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod(
            "<Clone>$",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    /// <summary>
    /// A type's name for reporting, a nested one qualified by the endpoint class that declares it.
    /// </summary>
    private static string NameOf(Type type) =>
        type.DeclaringType is { } declaring ? $"{declaring.Name}.{type.Name}" : type.Name;

    /// <summary>
    /// Every relation in <c>public</c> and every column it carries, in one read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema and the relation kinds come from
    /// <see cref="RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate" /> rather than being
    /// spelled here, for the reason <c>DataMinimizationSchemaTests</c> gives: a copy that misses a
    /// widening reports green over exactly the relation kinds the widening was for.
    /// </para>
    /// <para>
    /// <c>attnum &gt; 0</c> drops the system columns, which belong to PostgreSQL rather than to
    /// anyone's argument about a table; <c>not attisdropped</c> drops the tombstones a dropped column
    /// leaves behind under a mangled name.
    /// </para>
    /// </remarks>
    private static async Task<SchemaIdentifiers> ReadPublicIdentifiersAsync(
        NpgsqlConnection connection)
    {
        const string sql =
            $"""
            select c.relname::text, a.attname::text
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where {RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate}
              and a.attnum > 0
              and not a.attisdropped
            order by c.relname, a.attname
            """;

        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> relations = [];
        List<string> columns = [];
        HashSet<string> seenRelations = new(StringComparer.Ordinal);

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);
            string column = reader.GetString(1);

            if (seenRelations.Add(table))
            {
                relations.Add(table);
            }

            columns.Add($"{table}.{column}");
        }

        return new SchemaIdentifiers(relations, columns);
    }

    /// <summary>
    /// Every relation and column name the vocabulary refuses — a relation as a bare name, a column as
    /// <c>table.column</c>.
    /// </summary>
    /// <remarks>
    /// A pure function over what was read, so the census and both of its controls run byte-identical
    /// classification over databases that differ only in what was created on them. A control that
    /// exercised a separately written classification would prove that one can fail.
    /// </remarks>
    private static IReadOnlyList<SchemaOffender> KeyMaterialOffenders(SchemaIdentifiers identifiers) =>
    [
        .. identifiers.Relations
            .Concat(identifiers.Columns)
            .Select(identifier =>
                (identifier, rule: UnwrappedKeyMaterialVocabulary.Classify(identifier.Split('.')[^1])))
            .Where(candidate => candidate.rule is not null)
            .Select(candidate => new SchemaOffender(candidate.identifier, candidate.rule!))
            .OrderBy(offender => offender.Identifier, StringComparer.Ordinal),
    ];

    /// <summary>
    /// The identifiers of <paramref name="offenders" />, without the argument for refusing them.
    /// </summary>
    /// <remarks>
    /// The controls assert over this rather than over the sentences <see cref="Describe" /> builds,
    /// and the split is not cosmetic: a <c>DoesNotContain</c> against a formatted sentence passes
    /// whenever the formatting changes, so a control written that way would agree with everything
    /// forever. The census asserts over the sentences because a failure there has a reviewer to
    /// convince.
    /// </remarks>
    private static string[] Identifiers(IReadOnlyList<SchemaOffender> offenders) =>
        [.. offenders.Select(offender => offender.Identifier)];

    /// <summary>
    /// The offenders as sentences naming what each is and why it is refused.
    /// </summary>
    private static string Describe(IReadOnlyList<SchemaOffender> offenders) =>
        string.Join(
            Environment.NewLine,
            offenders.Select(offender =>
                $"{offender.Identifier} — {offender.Rule.Category}: {offender.Rule.Reason}"));

    /// <summary>
    /// Every <c>bytea</c> column in <c>public</c>, as <c>table.column</c>.
    /// </summary>
    /// <remarks>
    /// <c>_bytea</c> is PostgreSQL's internal name for an array of <c>bytea</c> and is discovered
    /// beside it, because an array of envelopes is exactly as capable of holding a key as one
    /// envelope is and no argument for the scalar covers it. The relation predicate is the shared one
    /// again, so a materialized view holding binary rows is discovered rather than skipped for being
    /// the wrong <c>relkind</c>.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ReadBinaryColumnsAsync(
        NpgsqlConnection connection)
    {
        const string sql =
            $"""
            select c.relname::text, a.attname::text
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            join pg_type t on t.oid = a.atttypid
            where {RowLevelSecurityCoverage.RowBearingRelationInPublicPredicate}
              and a.attnum > 0
              and not a.attisdropped
              and t.typname in ('bytea', '_bytea')
            order by c.relname, a.attname
            """;

        await using NpgsqlCommand command = new(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> columns = [];

        while (await reader.ReadAsync())
        {
            columns.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        }

        return columns;
    }

    /// <summary>
    /// Both directions of the comparison between what the catalog holds and what this file argues
    /// for: binary columns nobody classified, and classifications of columns that are gone.
    /// </summary>
    private static (string[] Unclassified, string[] Stale) CompareToClassifications(
        IReadOnlyList<string> binaryColumns)
    {
        HashSet<string> classified =
            new(Classifications.Select(entry => entry.Qualified), StringComparer.Ordinal);
        HashSet<string> discovered = new(binaryColumns, StringComparer.Ordinal);

        return
        (
            [.. discovered.Except(classified, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. classified.Except(discovered, StringComparer.Ordinal).Order(StringComparer.Ordinal)]
        );
    }

    /// <summary>
    /// Whether a member of this declared type can hold text at all.
    /// </summary>
    /// <remarks>
    /// <b>Broad on purpose, and in the same direction <see cref="PayloadTypes" /> is broad.</b> A member
    /// typed <c>string[]</c>, <c>IReadOnlyList&lt;string&gt;</c>, <c>Optional&lt;string&gt;</c> or
    /// <c>Dictionary&lt;string, string&gt;</c> is a place a key-shaped value can be put exactly as a bare
    /// one is, so a predicate matching <see cref="string" /> alone would be one generic away from
    /// reporting nothing. The recursion terminates because every step strips a layer of type.
    /// </remarks>
    private static bool CarriesText(Type type)
    {
        if (type == typeof(string))
        {
            return true;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return CarriesText(underlying);
        }

        if (type.IsArray && type.GetElementType() is { } element && CarriesText(element))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(CarriesText);
    }

    /// <summary>
    /// Both directions of the comparison between the text members a surface carries and the arguments
    /// this file makes for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure function over what was walked, so the census and both halves of its control run
    /// byte-identical classification over surfaces that differ only in what was handed in. A control
    /// exercising a separately written comparison would prove that one can fail.
    /// </para>
    /// <para>
    /// <b>An entry saying too little is reported beside a member with no entry at all</b>, rather than
    /// through a test of its own. The two are the same defect — nobody argued for this member — and a
    /// list whose entries nobody has to write is a list that agrees with whatever arrives next.
    /// </para>
    /// </remarks>
    private static (string[] Unargued, string[] Stale) CompareToTextArguments(
        IReadOnlyList<SurfaceMember> surface)
    {
        HashSet<string> argued = new(
            TextMemberArguments
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Carries)
                                && entry.Carries.Length >= MinimumCarriesLength)
                .Select(entry => entry.Qualified),
            StringComparer.Ordinal);
        HashSet<string> carried = new(
            surface.Where(member => CarriesText(member.MemberType)).Select(member => member.Qualified),
            StringComparer.Ordinal);

        return
        (
            [.. carried.Except(argued, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. argued.Except(carried, StringComparer.Ordinal).Order(StringComparer.Ordinal)]
        );
    }

    /// <summary>Runs one DDL statement on the admin connection.</summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();

        return host;
    }

    /// <summary>
    /// The request-surface control's offending shape: two members that must be refused and two beside
    /// them that must not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A type declared in the test rather than a mutation of a real endpoint, so the control is
    /// permanent. It is nested one level deep on purpose — the offending member on
    /// <see cref="ProbeNestedResults" /> can only be reported by a walk that recurses, so this one
    /// probe proves the classification and the recursion at once.
    /// </para>
    /// <para>
    /// <see cref="WrappedContentKey" /> and <see cref="ProbeNestedResults.Enabled" /> are the
    /// innocent halves, and they are what stop the control passing for the wrong reason: a rule that
    /// fired on everything, or a vocabulary whose qualifier mechanism had collapsed, would report all
    /// four.
    /// </para>
    /// </remarks>
    private sealed record ProbeRequest(
        string ContentKey,
        string WrappedContentKey,
        ProbeNestedResults? Extensions);

    /// <summary>The nested half of the probe. See <see cref="ProbeRequest" />.</summary>
    private sealed record ProbeNestedResults(string PrfOutput, bool? Enabled);
}
