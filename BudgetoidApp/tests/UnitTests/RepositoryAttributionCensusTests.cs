using System.Reflection;
using Infrastructure.Repositories;

namespace UnitTests;

/// <summary>
/// Where a repository is named, this is the file that pins the narrowing beside the method, and the
/// one-line statement of what that file actually pins.
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="PinsWhat" /> is the member that stops this list from meaning less than it says. The
/// bucket is called "pinned elsewhere", and a reader will take that as parity with the repositories
/// <c>RepositoryConstraintAttributionTests</c> covers — a translation of the repository's own
/// constraint <b>and</b> a control proving a stranger's violation is not dressed up in its message.
/// Three entries do not have both halves today. Writing only the file name would hide that behind a
/// bucket name, which is the same shape of claim this whole census exists to stop: a word that reads
/// as complete and is not.
/// </para>
/// </remarks>
/// <param name="Repository">The type name exactly as the Infrastructure assembly declares it.</param>
/// <param name="PinnedIn">The test file holding the narrowing, named so a reader can go and read it.</param>
/// <param name="PinsWhat">
/// What that file pins, and — where a half is missing — which half. No default value: an entry whose
/// coverage nobody stated is the state this member was added to leave behind.
/// </param>
public sealed record AttributionPin(string Repository, string PinnedIn, string PinsWhat);

/// <summary>
/// Every discovered repository sorted by how many of the two sets claim it, plus the names claimed by
/// no repository at all.
/// </summary>
/// <param name="Unlisted">
/// Repositories no set claims. This is the bucket a new repository lands in, and it is red rather than
/// empty-by-convenience: with two dispositions in play there is nothing to derive for a repository
/// nobody has decided about, and picking one for it would be guessing.
/// </param>
/// <param name="ListedTwice">
/// Repositories both sets claim. Red for the opposite reason: a repository covered in two places has
/// two answers to "where is this pinned", and the one nobody reads is the one that rots.
/// </param>
/// <param name="NamingNoRepository">
/// Names a set claims that the assembly no longer declares. Reported rather than dropped: such a name
/// lies in wait for whatever is next called that, ready to hand it a disposition argued about
/// something else — and, until then, silently shrinks the census by one.
/// </param>
/// <param name="Classified">
/// Repositories exactly one set claims. Carried so a caller can prove the census found real subjects
/// rather than passing over an empty sequence.
/// </param>
public sealed record AttributionCensus(
    IReadOnlyList<string> Unlisted,
    IReadOnlyList<string> ListedTwice,
    IReadOnlyList<string> NamingNoRepository,
    IReadOnlyList<string> Classified);

/// <summary>
/// Pins that every repository the Infrastructure assembly declares has been assigned, by a person, to
/// the file that holds its constraint-name narrowing — <c>RepositoryConstraintAttributionTests</c> or
/// one named in <see cref="PinnedElsewhere" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this replaces.</b> <c>RepositoryConstraintAttributionTests</c> used to claim in prose that
/// "every repository is also covered from the other side". That is a completeness claim, and nothing
/// executed it. It went stale twice without anything noticing — <c>PasskeyRepository</c> fell outside
/// it first, then <c>RecoveryCodeRepository</c> — which is the same failure a counted <c>DELETE</c>
/// grant written in a comment has: <b>a count or a completeness claim written in prose is a claim
/// nothing executes</b>. The point of this file is not that the list is right today. It is that the
/// next repository added to <c>Infrastructure/Repositories/</c> cannot land until somebody says which
/// bucket it is in.
/// </para>
/// <para>
/// <b>Recognisably a sibling of <c>RowLevelSecurityCoverage</c>,</b> and for its reason. The subject is
/// <i>discovered</i> from the live assembly and never written down; what is written down is the
/// disposition. A list of the repositories that <i>are</i> covered fails open — the twelfth nobody
/// adds to it keeps the census green on the only day it matters. Requiring every discovered type to be
/// claimed by exactly one set fails closed: the twelfth is claimed by neither, and it stays red until
/// a person decides. Do not "simplify" this into a written list of repository names; a written list is
/// precisely the thing that just drifted.
/// </para>
/// <para>
/// <b>What counts as a repository, and why the line is drawn where it is.</b> Every <b>public</b> type
/// the assembly declares whose namespace is <c>Infrastructure.Repositories</c> or a descendant of it,
/// excluding only enums and delegates. Deliberately <i>not</i> filtered on a <c>Repository</c> name
/// suffix, not on implementing a Domain port, and not on being concrete. Each of those reads like a
/// definition of "repository" and behaves like a discovery filter, which is the mistake
/// <see cref="Infrastructure.Persistence.Provisioning.RowLevelSecurityCoverage" /> documents at length
/// about <c>relkind = 'r'</c>: it excluded kinds of object rather than tables, and every shape outside
/// it was exempted with no decision and no record. A shared abstract base, a <c>static</c> helper
/// holding the filter predicate, or an interface with a default method are exactly where a translation
/// would migrate to, and "concrete class" silently exempts all three — a <c>static</c> class is
/// <c>abstract sealed</c> in IL, so <c>!IsAbstract</c> drops it without saying so. The two kinds left
/// out cannot declare a method body at all, so there is no <c>catch</c> in them for an attribution
/// rule to be wrong about. That is the test any future narrowing has to pass, and none of the three
/// obvious ones does.
/// </para>
/// <para>
/// <b>The honest limit.</b> Discovery is scoped by namespace and by public visibility, so an
/// <c>internal</c> translating type, or a repository moved to another namespace, is invisible to it —
/// see <c>Discovery_IsBlindToARepositoryOutsideTheNamespace</c>, which is a permanent demonstration
/// rather than a defect to fix by widening the scan. Widening only moves the blind spot; what covers
/// it instead is <c>Discovery_FindsExactlyTheRepositoriesTheNamespaceDeclares</c>, which pins the
/// twelve names, so a repository that leaves the namespace goes red there rather than quietly leaving
/// the census with nothing to count.
/// </para>
/// <para>
/// <b>A gap this recorded, and which is now closed.</b> Narrowing on
/// <c>PostgresException.ConstraintName</c> is the house rule — ten of the twelve repositories do it,
/// and two of those spell it inside a helper rather than in the <c>when</c> clause. Having a narrowed
/// <c>catch</c> is not the same as having it <i>tested from both sides</i>, and
/// <see cref="PinnedElsewhere" /> says per entry which halves exist. It said, for three entries, that
/// the translation was pinned and there was <b>no mis-attribution control at all</b>:
/// <c>PasskeyRepository</c>, <c>TransactionRepository.UpdateAsync</c> and
/// <c>UserRepository.TryAddAsync</c>. Each of those now has one, in the file its entry names, and
/// <c>TransactionRepository.UpdateAsync</c> gained the translation half it also turned out to be
/// missing. <b>The reason lines were rewritten in the same change</b>, which is the discipline this
/// member exists for in both directions: a census that keeps claiming a gap it no longer has is the
/// same defect as one that hides a gap it does have, and the second is only easier to notice.
/// </para>
/// <para>
/// <b>What is not claimed is that the twelve are now uniformly covered</b> — only that every entry says
/// which halves it holds. <c>SessionRepository</c> holds neither and says so, because it translates
/// nothing, and <c>SessionTokenRepository</c> says the stronger version of that: it has no
/// <c>catch</c> at all. The five in <see cref="CoveredByAttributionTests" /> hold both by that file's
/// own definition. The next repository to land here still has to be argued about by a person, which is the
/// property that survives every one of these lines being correct today.
/// </para>
/// <para>
/// Sabotaged in four directions before it was believed, each on synthetic input so the proof is
/// permanent rather than a sentence about a change that was reverted: a repository in neither set, one
/// in both, a set naming a repository that does not exist, and — the control without which the first
/// three could all pass while the real census checked nothing — the live twelve classified against two
/// <b>empty</b> sets, which must report all twelve unlisted.
/// </para>
/// <para>
/// It lives in <c>UnitTests</c> because <c>UnitTests.csproj</c> already references Infrastructure, so
/// nothing here needs a new <c>ProjectReference</c> that <c>ProjectReferenceGraphTests</c> would have
/// to be edited to accept. It is pure reflection: no container, no database, no host.
/// </para>
/// </remarks>
public sealed class RepositoryAttributionCensusTests
{
    /// <summary>
    /// The repositories whose attribution is covered in <c>RepositoryConstraintAttributionTests</c>:
    /// every one reachable through a budget, each with both halves — its own constraint translated,
    /// and a stranger's violation propagating rather than wearing this repository's message.
    /// </summary>
    private static readonly string[] CoveredByAttributionTests =
    [
        nameof(AccountRepository),
        nameof(BudgetRepository),
        nameof(CategoryGroupRepository),
        nameof(CategoryRepository),
        nameof(PayeeRepository),
    ];

    /// <summary>
    /// The repositories whose narrowing is pinned beside the method in their own files — the
    /// identity-side ones, plus the two whose narrowing is not a constraint name at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read <see cref="AttributionPin.PinsWhat" /> on each, not the bucket name. Two of the seven are
    /// pinned in both directions on every narrowing they hold; two — <c>SessionRepository</c> and
    /// <c>SessionTokenRepository</c> — have nothing to attribute at all, which is a different statement
    /// and each says so in its own words; two of the three that write
    /// <c>wrapped_account_keys</c> each gained a <c>factor_id</c> narrowing whose two halves are not
    /// both in the file named beside it; and the third of them —
    /// <c>RegistrationRepository</c> — holds the translation half on all four of its narrowings and the
    /// mis-attribution half on none.
    /// </para>
    /// <para>
    /// That split is stated rather than smoothed over, for the reason the class remarks give about the
    /// gap this member was added to expose: the mis-attribution control for the new filter is at that
    /// layer on both of the first two — an unrelated <c>23505</c> reaching either <c>catch</c> would come
    /// back as a conflict about a factor identifier nobody claimed — while on both the translation is
    /// pinned only over HTTP, by the route that stages a duplicate identifier end to end. Both entries
    /// name the test that does it. <c>RegistrationRepository</c> is the entry the other way up, and its
    /// own line says why the missing control costs less there than it would anywhere else: every unique
    /// rule it does <b>not</b> narrow is keyed on a <c>user_id</c> derived for that one registration, so
    /// no row this save writes can breach one.
    /// </para>
    /// </remarks>
    private static readonly AttributionPin[] PinnedElsewhere =
    [
        new(
            nameof(PasskeyRepository),
            "PasskeyRepositoryTests",
            "both halves on two of the three narrowings: TryAddAsync's webauthn_credential_id filter is "
            + "translated by TryAddAsync_WhenTheHandleIsAlreadyRegistered_ReturnsFalse and "
            + "controlled by TryAddAsync_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape, "
            + "and DeletePasskeyAsync's entries-based narrowing by "
            + "DeletePasskeyAsync_WhenTheRowIsAlreadyGone_ThrowsNotFound and "
            + "DeletePasskeyAsync_WhenAnUnrelatedEntityConflicts_LetsTheConflictEscape. "
            + "PasskeyCeremonyTests takes the same registration refusal as a 409 over HTTP, which is "
            + "the answer a client sees rather than a second pin of the filter. "
            + "THE THIRD IS TryAddAsync's PK_wrapped_account_keys filter — the primary key over "
            + "factor_id, which is where that column's uniqueness lives — which throws where "
            + "the handle filter answers false: its control is that same "
            + "_LetsTheViolationEscape test, which now asserts the escaping violation names neither "
            + "the handle index nor that key, and its TRANSLATION is pinned only over HTTP, by "
            + "PasskeyCeremonyTests.PasskeyRegistration_RefusesAFactorIdentifierAlreadyRegistered — "
            + "nothing here stages a duplicate factor identifier at this layer"),
        new(
            nameof(RecoveryCodeRepository),
            "RecoveryCodeRepositoryTests",
            "both halves on three of the four narrowings: "
            + "AddSetAsync_WhenTheAccountAlreadyHoldsASet_ThrowsConflict translates its own credentials "
            + "index and AddSetAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape controls it; "
            + "DeleteSetAsync's entries-based narrowing is translated by "
            + "DeleteSetAsync_WhenTheSetIsAlreadyGone_ThrowsConflict and controlled by "
            + "DeleteSetAsync_WhenAnUnrelatedEntityConflicts_LetsTheConflictEscape; ConsumeAsync's by "
            + "ConsumeAsync_WhenTheCodeIsAlreadyGone_RefusesTheRedemption and "
            + "ConsumeAsync_WhenAnUnrelatedEntityConflicts_LetsTheConflictEscape. "
            + "THE FOURTH IS AddSetAsync's PK_wrapped_account_keys filter — the primary key over "
            + "factor_id, which is where that column's uniqueness lives — whose two halves "
            + "are not both here: its control is "
            + "AddSetAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape, which asserts the "
            + "escaping violation names neither the one-set index nor that key, and its TRANSLATION is "
            + "pinned only over HTTP, by "
            + "RecoveryCodeGenerationTests.RecoveryCodeGeneration_RefusesAFactorIdentifierAlreadyRegistered "
            + "— which claims the account's passkey factor, because reusing the replaced set's own "
            + "identifier never reaches the filter, and reads the detail sentence, because this route's "
            + "other two conflicts answer the same status and title. Nothing here stages a duplicate "
            + "factor identifier at this layer, exactly as on PasskeyRepository's copy of the same "
            + "catch"),
        new(
            nameof(RegistrationRepository),
            "AccountRegistrationTests",
            "the translation half on all four narrowings and the mis-attribution half on none of them, "
            + "and that asymmetry is the whole of what this entry says. RegisterAsync filters on four "
            + "index names — IX_credentials_provider_subject, IX_users_email, "
            + "IX_passkey_public_keys_webauthn_credential_id and PK_wrapped_account_keys — and each is "
            + "TRANSLATED over HTTP by one test that stages the collision end to end: "
            + "Registration_WhenTheSubjectAlreadyHasAnAccount_Returns409AndChangesNothing, "
            + "Registration_WhenTheEmailBelongsToAnotherAccount_Returns409 — which reads the DETAIL "
            + "sentence, because it is the only thing separating it from the subject conflict once the "
            + "handler's disambiguating re-read has run — "
            + "Registration_WhenTheAuthenticatorIsAlreadyRegistered_Returns409 and "
            + "Registration_WhenAFactorIdIsAlreadyRegistered_Returns409. Each also asserts that nothing "
            + "was written, which is what a translation test on a save of roughly thirty rows owes. "
            + "THERE IS NO MIS-ATTRIBUTION CONTROL, at this layer or over HTTP: nothing stages an "
            + "unrelated unique violation reaching one of these four catches, so an unrelated 23505 "
            + "dressed up as one of the four sentences would go unreported. Three of the four are "
            + "narrower than they look — IX_budgets_user_id_name, IX_credentials_user_id_federated and "
            + "IX_credentials_user_id_recovery_codes are all keyed on a user_id derived for this "
            + "registration alone and cannot be breached by anything this save writes, which is what "
            + "keeps the missing control from being the gap it would be on a repository whose rows share "
            + "an owner with anybody"),
        new(
            nameof(SessionRepository),
            "SessionRepositoryTests",
            "nothing to attribute: the only repository in the folder that filters on no constraint "
            + "name, because its only catch is a bounded DbUpdateConcurrencyException retry, which "
            + "carries no constraint name and no SQLSTATE for a filter to mis-read. "
            + "RevokeForCredentialAsync_RunTwice_KeepsTheFirstRevocationInstant pins the retry's "
            + "outcome"),
        new(
            nameof(SessionTokenRepository),
            "SessionRepositoryTests",
            "nothing to attribute, and it is the stronger form of the sentence SessionRepository's "
            + "entry above makes: that one has a catch narrowed by no constraint name, this one has NO "
            + "CATCH AT ALL and one member — a read. There is no violation for a filter to mis-read "
            + "because the statement writes nothing. What IS pinned there, and is the reason this "
            + "entry names a file rather than nothing, is the read itself: "
            + "FindByTokenHashAsync_FindsOnlyTheSessionItsOwnTokenNames covers both directions of the "
            + "bytea comparison the property's value converter produces, which is the one place a "
            + "silent wrong-overload translation would surface — every session in the system would "
            + "simply stop being found. The exemption that read rests on is pinned separately, by "
            + "RlsIsolationTests.Database_ReadsASessionTokenWithNoUserOnTheSession"),
        new(
            nameof(TransactionRepository),
            "TransactionRepositoryTests",
            "both halves on both narrowings: DeleteAllForAmbientBudgetAsync's entries-based narrowing "
            + "by _WhenAnotherRequestDeletedTheRowsFirst_Completes and "
            + "_WhenTheConflictNamesAnotherEntity_LetsItEscape, and UpdateAsync's two FK "
            + "constraint-name filters by UpdateAsync_WhenTheAccountIsGone_ and "
            + "UpdateAsync_WhenTheCategoryIsGone_TranslatesItsOwnForeignKey — one per clause, since a "
            + "transaction pointed at two missing rows would only ever reach the first — with "
            + "UpdateAsync_WhenATrackedRowBreaksAnotherForeignKey_LetsTheViolationEscape as the "
            + "mis-attribution control for both"),
        new(
            nameof(UserRepository),
            "UserRepositoryTests",
            "both halves on both narrowings: DeleteAsync's entries-based narrowing by "
            + "_WhenAnotherRequestErasedTheRowFirst_Completes and "
            + "_WhenTheConflictNamesAnotherEntity_LetsItEscape, and TryAddAsync's two-name unique "
            + "filter by the TryAddAsync_With* translations plus "
            + "TryAddAsync_WhenATrackedRowBreaksAThirdUniqueRule_LetsTheViolationEscape, which stages "
            + "a third credentials index — IX_credentials_user_id_federated — so the control lives "
            + "between two neighbouring rules on one table rather than across tables"),
    ];

    [Test]
    public async Task Census_ReportsARepositoryInNeitherSet()
    {
        // Arrange — the case this census exists for: a repository lands in the folder and nobody
        // says where its narrowing is pinned. Synthetic names, so the proof survives the day the
        // real sets are correct.
        string[] discovered = ["CoveredRepository", "PinnedRepository", "ForgottenRepository"];

        // Act
        AttributionCensus census = RepositoryAttribution.Take(
            discovered, ["CoveredRepository"], ["PinnedRepository"]);

        // Assert — named, not counted: a failure that says "1 repository is unlisted" sends the
        // reader back to the assembly to work out which.
        await Assert.That(census.Unlisted).IsEquivalentTo(new[] { "ForgottenRepository" });
        await Assert.That(census.Classified)
            .IsEquivalentTo(new[] { "CoveredRepository", "PinnedRepository" });
    }

    [Test]
    public async Task Census_ReportsARepositoryInBothSets()
    {
        // Arrange — the opposite failure, and the one a well-meaning edit produces: a repository
        // gets an entry in the elsewhere list without being taken out of the covered-here list, so
        // two places now claim to be where its narrowing is pinned.
        string[] discovered = ["AmbiguousRepository"];

        // Act
        AttributionCensus census = RepositoryAttribution.Take(
            discovered, ["AmbiguousRepository"], ["AmbiguousRepository"]);

        // Assert
        await Assert.That(census.ListedTwice).IsEquivalentTo(new[] { "AmbiguousRepository" });
        await Assert.That(string.Join(", ", census.Classified)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.Unlisted)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_ReportsAListedNameNoRepositoryAnswersTo()
    {
        // Arrange — a rename, or a deletion. The stale name is not harmless: it silently shrinks the
        // census, and it waits to attach a disposition argued about one type to whatever is next
        // called that.
        string[] discovered = ["LiveRepository"];

        // Act
        AttributionCensus census = RepositoryAttribution.Take(
            discovered, ["LiveRepository", "GhostRepository"], ["VanishedRepository"]);

        // Assert
        await Assert.That(census.NamingNoRepository)
            .IsEquivalentTo(new[] { "GhostRepository", "VanishedRepository" });
        await Assert.That(census.Classified).IsEquivalentTo(new[] { "LiveRepository" });
    }

    [Test]
    public async Task Census_AcceptsARepositoryListedInExactlyOneSet()
    {
        // Arrange — without this, a census that reported every repository in every bucket would
        // satisfy all three tests above while making the live assertions below fire on a folder
        // nobody has broken.
        string[] discovered = ["CoveredRepository", "PinnedRepository"];

        // Act
        AttributionCensus census = RepositoryAttribution.Take(
            discovered, ["CoveredRepository"], ["PinnedRepository"]);

        // Assert
        await Assert.That(string.Join(", ", census.Unlisted)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.ListedTwice)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.NamingNoRepository)).IsEqualTo(string.Empty);
        await Assert.That(census.Classified)
            .IsEquivalentTo(new[] { "CoveredRepository", "PinnedRepository" });
    }

    [Test]
    public async Task Discovery_FindsExactlyTheRepositoriesTheNamespaceDeclares()
    {
        // Arrange
        Assembly infrastructure = typeof(AccountRepository).Assembly;

        // Act
        IReadOnlyList<string> discovered = RepositoryAttribution.DiscoveredIn(infrastructure);

        // Assert — pinning the set is what catches a repository that LEAVES the namespace. Move one
        // to Infrastructure.Persistence and the census below goes on passing forever, having found
        // one fewer subject to check; this line goes red instead and a person has to say what the
        // move meant.
        string[] expected =
        [
            nameof(AccountRepository),
            nameof(BudgetRepository),
            nameof(CategoryGroupRepository),
            nameof(CategoryRepository),
            nameof(PasskeyRepository),
            nameof(PayeeRepository),
            nameof(RecoveryCodeRepository),
            nameof(RegistrationRepository),
            nameof(SessionRepository),
            nameof(SessionTokenRepository),
            nameof(TransactionRepository),
            nameof(UserRepository),
        ];
        await Assert.That(string.Join(", ", discovered.Except(expected))).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", expected.Except(discovered))).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Discovery_IsBlindToARepositoryOutsideTheNamespace()
    {
        // Arrange — a real repository beside a public type from this assembly, which is what a
        // repository moved out of the namespace would look like to the scan.
        Type[] types = [typeof(AccountRepository), typeof(RepositoryAttributionCensusTests)];

        // Act
        IReadOnlyList<string> discovered = RepositoryAttribution.NamesIn(types);

        // Assert — only the one inside the namespace comes back. This is not a defect to fix by
        // widening the scan, which would only move the blind spot to the next boundary: it is the
        // whole reason the pinned name set above exists. The same blindness covers an internal type,
        // for the same reason and with the same answer.
        await Assert.That(discovered).IsEquivalentTo(new[] { nameof(AccountRepository) });
    }

    [Test]
    public async Task EveryRepository_IsInExactlyOneAttributionBucket()
    {
        // Arrange
        Assembly infrastructure = typeof(AccountRepository).Assembly;
        IReadOnlyList<string> discovered = RepositoryAttribution.DiscoveredIn(infrastructure);

        // Act
        AttributionCensus census = RepositoryAttribution.Take(
            discovered,
            CoveredByAttributionTests,
            PinnedElsewhere.Select(pin => pin.Repository));

        // Assert — joined rather than counted so a failure names the offender and the fix it needs.
        await Assert.That(string.Join(", ", census.Unlisted)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.ListedTwice)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.NamingNoRepository)).IsEqualTo(string.Empty);

        // A reflection query that silently came back empty would satisfy all three lines above while
        // proving nothing, so the subject is asserted to exist as well.
        await Assert.That(census.Classified.Count).IsEqualTo(discovered.Count);
        await Assert.That(census.Classified.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task EveryRepository_ClassifiedAgainstEmptySets_ComesBackUnlisted()
    {
        // Arrange — the control the three synthetic ones cannot supply. They prove the classifier
        // sorts names; this proves the LIVE subject reaches it, which is what the census would
        // silently stop doing if discovery ever narrowed to nothing.
        Assembly infrastructure = typeof(AccountRepository).Assembly;
        IReadOnlyList<string> discovered = RepositoryAttribution.DiscoveredIn(infrastructure);

        // Act
        AttributionCensus census = RepositoryAttribution.Take(discovered, [], []);

        // Assert — every real repository comes back unexcused, so the red direction is demonstrated
        // against real input rather than only against strings this file made up.
        await Assert.That(census.Unlisted).IsEquivalentTo(discovered);
        await Assert.That(census.Unlisted.Count).IsGreaterThan(0);
        await Assert.That(string.Join(", ", census.Classified)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task EveryElsewhereEntry_NamesTheFileThatPinsItAndAReason()
    {
        // Arrange — an entry may leave the covered-here bucket only by saying where it went and what
        // is actually pinned there. An empty string satisfies the census perfectly and tells the
        // next reader nothing, which is the prose failure one layer in.
        IReadOnlyList<string> silent =
        [
            .. PinnedElsewhere
                .Where(pin => string.IsNullOrWhiteSpace(pin.PinnedIn)
                              || string.IsNullOrWhiteSpace(pin.PinsWhat))
                .Select(pin => pin.Repository)
                .Order(StringComparer.Ordinal),
        ];

        // Act
        IReadOnlyList<string> notATestFile =
        [
            .. PinnedElsewhere
                .Where(pin => !pin.PinnedIn.EndsWith("Tests", StringComparison.Ordinal))
                .Select(pin => $"{pin.Repository} -> {pin.PinnedIn}")
                .Order(StringComparer.Ordinal),
        ];

        // Assert
        await Assert.That(string.Join(", ", silent)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", notATestFile)).IsEqualTo(string.Empty);
        await Assert.That(PinnedElsewhere.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// Finds the repositories Infrastructure declares, and sorts them by how many of the two written
    /// dispositions claim each one.
    /// </summary>
    /// <remarks>
    /// <see cref="Take" /> takes both sets as parameters rather than reading the fields above, and
    /// <see cref="NamesIn" /> takes types rather than reaching for the assembly itself. That is what
    /// lets the synthetic cases prove both failure directions without anyone deleting a real entry to
    /// watch the suite go red — the same reason
    /// <c>RowLevelSecurityCoverage.Classify</c> is handed its exemption list instead of reading it.
    /// </remarks>
    private static class RepositoryAttribution
    {
        /// <summary>The namespace a repository lives in, and every namespace beneath it.</summary>
        private const string RepositoriesNamespace = "Infrastructure.Repositories";

        /// <summary>Every repository the assembly declares, ordered by name.</summary>
        internal static IReadOnlyList<string> DiscoveredIn(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            return NamesIn(assembly.GetTypes());
        }

        /// <summary>
        /// The subject filter, applied to types rather than to an assembly so it can be aimed at
        /// synthetic input.
        /// </summary>
        /// <remarks>
        /// <c>IsPublic</c> is false for a nested type whatever its accessibility, which is what keeps
        /// compiler-generated state machines and closure classes out without a rule naming them.
        /// Enums and delegates are the only kinds excluded, because neither can declare a method body
        /// and so neither can hold the <c>catch</c> this census is about — see the class remarks for
        /// why "concrete class" and "name ends in Repository" both fail that test.
        /// </remarks>
        internal static IReadOnlyList<string> NamesIn(IEnumerable<Type> types)
        {
            ArgumentNullException.ThrowIfNull(types);

            return
            [
                .. types
                    .Where(type => type.IsPublic)
                    .Where(type => !type.IsEnum && !typeof(Delegate).IsAssignableFrom(type))
                    .Where(type => InRepositoriesNamespace(type.Namespace))
                    .Select(type => type.Name)
                    .Order(StringComparer.Ordinal),
            ];
        }

        /// <summary>
        /// Sorts each discovered repository into unlisted, listed twice, or classified, and reports
        /// the listed names nothing answers to.
        /// </summary>
        internal static AttributionCensus Take(
            IEnumerable<string> discovered,
            IEnumerable<string> coveredHere,
            IEnumerable<string> pinnedElsewhere)
        {
            ArgumentNullException.ThrowIfNull(discovered);
            ArgumentNullException.ThrowIfNull(coveredHere);
            ArgumentNullException.ThrowIfNull(pinnedElsewhere);

            // Ordinal throughout: a type name differing only by case is a different type, and a loose
            // comparison would let one disposition claim a repository it does not name.
            HashSet<string> here = new(coveredHere, StringComparer.Ordinal);
            HashSet<string> elsewhere = new(pinnedElsewhere, StringComparer.Ordinal);

            List<string> unlisted = [];
            List<string> listedTwice = [];
            List<string> classified = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (string repository in discovered)
            {
                seen.Add(repository);

                switch (here.Contains(repository), elsewhere.Contains(repository))
                {
                    case (true, true):
                        listedTwice.Add(repository);
                        break;
                    case (false, false):
                        unlisted.Add(repository);
                        break;
                    default:
                        classified.Add(repository);
                        break;
                }
            }

            List<string> namingNoRepository =
            [
                .. here.Concat(elsewhere)
                    .Where(name => !seen.Contains(name))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];

            return new AttributionCensus(unlisted, listedTwice, namingNoRepository, classified);
        }

        /// <summary>
        /// Whether a namespace is the repositories namespace or one nested inside it.
        /// </summary>
        /// <remarks>
        /// The trailing dot is load-bearing. A bare <c>StartsWith</c> would also claim a sibling
        /// namespace such as <c>Infrastructure.RepositoriesLegacy</c>, and equality alone would let an
        /// <c>Infrastructure.Repositories.Internal</c> folder hold a translating type the census never
        /// sees.
        /// </remarks>
        private static bool InRepositoriesNamespace(string? candidate) =>
            candidate is not null
            && (string.Equals(candidate, RepositoriesNamespace, StringComparison.Ordinal)
                || candidate.StartsWith($"{RepositoriesNamespace}.", StringComparison.Ordinal));
    }
}
