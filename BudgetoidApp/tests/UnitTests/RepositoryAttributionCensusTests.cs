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
/// Two entries do not have both halves today — the two whose <c>factor_id</c> narrowing is
/// translated only over HTTP — and a third, <c>KeyRotationRepository</c>, holds <b>two catches of two
/// different shapes</b> whose halves are spread over <b>two different files</b>, which is why its
/// <paramref name="PinnedIn" /> names two. Writing only
/// the file name would hide either shape behind a bucket name, which is the same claim this whole
/// census exists to stop: a word that reads as complete and is not.
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
/// disposition. A list of the repositories that <i>are</i> covered fails open — the next one nobody
/// adds to it keeps the census green on the only day it matters. Requiring every discovered type to be
/// claimed by exactly one set fails closed: the next one is claimed by neither, and it stays red until
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
/// fourteen names, so a repository that leaves the namespace goes red there rather than quietly leaving
/// the census with nothing to count.
/// </para>
/// <para>
/// <b>A gap this recorded, closed, and then partly reopened by a deletion.</b> Narrowing on
/// <c>PostgresException.ConstraintName</c> is the house rule — ten of the fourteen repositories do it,
/// and two of those spell it inside a helper rather than in the <c>when</c> clause. Having a narrowed
/// <c>catch</c> is not the same as having it <i>tested from both sides</i>, and
/// <see cref="PinnedElsewhere" /> says per entry which halves exist. It once said, for three entries,
/// that the translation was pinned and there was <b>no mis-attribution control at all</b>:
/// <c>PasskeyRepository</c>, <c>TransactionRepository.UpdateAsync</c> and
/// <c>UserRepository.TryAddAsync</c>. Each gained one, and <c>TransactionRepository.UpdateAsync</c>
/// gained the translation half it also turned out to be missing.
/// </para>
/// <para>
/// <b>Then <c>UserRepository.TryAddAsync</c> was deleted with the provisioning path, its control went
/// with it, and the replacement was written against the catch shape that survived.</b> The two-name
/// unique filter over <c>IX_credentials_provider_subject</c> and <c>IX_users_email</c> did not leave
/// the assembly: <c>RegistrationRepository.RegisterAsync</c> narrows on the same two names, among
/// four, and <c>RegistrationRepositoryTests</c> now stages a stranger's
/// <c>PK_recovery_code_hashes</c> violation into it, in
/// <c>RegisterAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape</c> — one test controlling all
/// four clauses, because widening any one of them to the bare SQLSTATE swallows that violation. So the
/// suite is no longer a control short, and <b>both</b> entries say what they hold in their own words
/// rather than one of them going quiet. This edit is the discipline this member exists for, running
/// the other way for once: a census that keeps claiming a gap it no longer has is the same defect as
/// one that hides a gap it does have, and the second is only easier to notice.
/// </para>
/// <para>
/// <b>What is not claimed is that the fourteen are now uniformly covered</b> — only that every entry says
/// which halves it holds. <c>SessionRepository</c> holds neither and says so, because it translates
/// nothing; <c>SessionTokenRepository</c> says the stronger version of that, having no <c>catch</c> at
/// all over a member that writes nothing; <c>NarrativeResealRepository</c> used to say a third version —
/// no <c>catch</c> over a member that <em>does</em> write — and stopped being true the day its
/// <c>SaveAsync</c> gained a filter on four name indexes, so its entry now names both halves of that
/// filter and the file each is held in; and <c>KeyRotationRepository</c> now
/// carries <b>two catches of two different shapes</b> and says which halves each one has. Its
/// <c>StageAsync</c> gained the two-name narrowing that entry once recorded as owed, in the commit that
/// mapped a begin route, so the translation is pinned over HTTP by the route's own tests while the
/// mis-attribution control is at the repository layer, where a stranger's violation can be staged into
/// the save at all — and what that half still refuses to claim is the retry, which nothing in the suite
/// deterministically runs. Its <c>PromoteAsync</c> narrows on EF's <em>entries</em> rather than on
/// anything PostgreSQL says, because a concurrency failure carries no SQLSTATE and no constraint name,
/// and both directions of that one are pinned in the repository file with no race in either.
/// The five in <see cref="CoveredByAttributionTests" /> hold both by that file's
/// own definition. The next repository to land here still has to be argued about by a person, which is the
/// property that survives every one of these lines being correct today.
/// </para>
/// <para>
/// Sabotaged in four directions before it was believed, each on synthetic input so the proof is
/// permanent rather than a sentence about a change that was reverted: a repository in neither set, one
/// in both, a set naming a repository that does not exist, and — the control without which the first
/// three could all pass while the real census checked nothing — the live fourteen classified against
/// two <b>empty</b> sets, which must report all fourteen unlisted.
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
    /// <remarks>
    /// <para>
    /// <b>"BOTH HALVES" IS PER REPOSITORY AND NOT PER VERB, AND THE DIFFERENCE IS WORTH READING BEFORE
    /// TRUSTING THIS LIST.</b> The two halves are the two DIRECTIONS of attribution — translate my own,
    /// propagate a stranger's — and every entry has both. What the sentence does not say, and a reader
    /// will assume, is that it holds for <c>AddAsync</c> and <c>UpdateAsync</c> alike. It does not.
    /// </para>
    /// <para>
    /// <b>The translate half is now on every verb; it was not until this slice.</b> When this list was
    /// written, <c>AccountRepository.UpdateAsync</c>, <c>PayeeRepository.UpdateAsync</c> and BOTH verbs
    /// of <c>CategoryRepository</c> had no case translating their duplicate-name arm — four arms of a
    /// seven-arm class, found by grepping for the SHAPE of the defect rather than for the file the first
    /// instance turned up in. <c>UpdateAccount_RenamedOntoATakenName_</c>,
    /// <c>UpdatePayee_RenamedOntoATakenName_</c>, <c>AddCategory_WithADuplicateCategoryName_</c> and
    /// <c>UpdateCategory_RenamedOntoATakenName_</c> closed them.
    /// </para>
    /// <para>
    /// <b>The propagate half is still CREATE-VERB ONLY on all five, and that is a decision rather than a
    /// gap.</b> Enumerated: every <c>_WhenATrackedRowBreaks…_LetsTheViolationEscape</c> case in that file
    /// stages an <c>AddAsync</c>. No rename verb has one. What such a case measures is the narrowing
    /// predicate — <c>IsUniqueViolationOf</c> on categories and category groups, the inline
    /// <c>ConstraintName:</c> pattern on accounts and payees — and on each table that predicate is ONE
    /// helper shared by both verbs, so a second staging on the rename would re-measure it. The exception
    /// is <c>TransactionRepository</c>, whose <c>UpdateAsync</c> narrows on foreign keys its
    /// <c>AddAsync</c> does not have and therefore carries its own control in its own file.
    /// </para>
    /// <para>
    /// <b>What that leaves unmeasured is narrow and worth naming</b>: a rename arm whose <c>when</c>
    /// clause was widened INDEPENDENTLY of the create arm's — two catch blocks, one predicate today, but
    /// nothing stops somebody inlining a looser test into one of them. No case would see it.
    /// </para>
    /// </remarks>
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
    /// Read <see cref="AttributionPin.PinsWhat" /> on each, not the bucket name. Three of the nine are
    /// pinned in both directions on every narrowing they hold — <c>TransactionRepository</c>;
    /// <c>UserRepository</c>, which now holds only one narrowing because the insert that carried its
    /// other one was deleted with the provisioning path; and <c>RegistrationRepository</c>, which holds
    /// both halves on all four of its narrowings, the second half being <b>one</b> test rather than
    /// four. Two — <c>SessionRepository</c> and <c>SessionTokenRepository</c> — have nothing to
    /// attribute at all, which is a different statement and each says so in its own words: a
    /// <c>catch</c> narrowed by no constraint name, and no <c>catch</c> over a member that writes
    /// nothing. <c>NarrativeResealRepository</c> used to be a third, with no <c>catch</c> over a member
    /// that writes; its <c>SaveAsync</c> now narrows a <c>23505</c> on four name-index constants and
    /// holds both halves at the repository layer in <c>RepositoryConstraintAttributionTests</c>, and it
    /// stays in this bucket because its route-level answer is pinned in a file of its own.
    /// <c>KeyRotationRepository</c> used to be one of those and is not any more: its
    /// <c>StageAsync</c> now narrows a <c>23505</c> on two primary-key names and its <c>PromoteAsync</c>
    /// narrows a concurrency failure on EF's entries, so that entry has been rewritten three times —
    /// from an absence into a gap, from a gap into <b>both halves in two files</b>, and now into
    /// <b>two catches</b> whose narrowings are not the same kind of thing: the begin translation pinned
    /// over HTTP by <c>KeyRotationBeginEndpointTests</c>, its mis-attribution control at the repository
    /// layer in <c>KeyRotationRepositoryTests</c>, the promotion's <em>both</em> directions in that same
    /// file and needing no race, and the limits of each — the begin's retry, which nothing runs
    /// deterministically, and the promotion's state clause, which nothing in the product can produce —
    /// written out rather than left to the bucket name. The remaining two of the three that write
    /// <c>wrapped_account_keys</c> each gained a <c>factor_id</c> narrowing whose two halves are not
    /// both in the file named beside it.
    /// </para>
    /// <para>
    /// <b>The <c>UserRepository</c> entry is the one to read before trusting the shape of this
    /// list.</b> "Both halves on every narrowing it holds" is a true sentence that got easier to say by
    /// losing a narrowing rather than by gaining a control, and the catch shape it lost is still live
    /// one entry up — where it now has a control of its own, written against another table's rule
    /// rather than a third rule on <c>credentials</c>. Both entries name each other for that reason.
    /// </para>
    /// <para>
    /// The <c>factor_id</c> split is stated rather than smoothed over, for the reason the class remarks
    /// give about the gap this member was added to expose: the mis-attribution control for the new
    /// filter is at that layer on both of those two — an unrelated <c>23505</c> reaching either
    /// <c>catch</c> would come back as a conflict about a factor identifier nobody claimed — while on
    /// both the translation is pinned only over HTTP, by the route that stages a duplicate identifier
    /// end to end. Both entries name the test that does it.
    /// </para>
    /// </remarks>
    private static readonly AttributionPin[] PinnedElsewhere =
    [
        new(
            nameof(KeyRotationRepository),
            "KeyRotationBeginEndpointTests and KeyRotationRepositoryTests",
            "TWO CATCHES OF TWO DIFFERENT SHAPES, AND THE CENSUS CANNOT SEE THE DIFFERENCE — WHICH IS "
            + "WHY THIS ENTRY NAMES BOTH. StageAsync narrows a DbUpdateException on what PostgreSQL "
            + "says: a SQLSTATE and a constraint name. PromoteAsync narrows a "
            + "DbUpdateConcurrencyException, which says NEITHER of those — there is no violation, only a "
            + "statement that matched fewer rows than EF expected — so its only available narrowing is "
            + "the ENTRIES EF could not account for. A reader who takes 'narrowed on ConstraintName' as "
            + "this class's habit and writes the next filter that way will be writing a clause that "
            + "cannot match, because the property is null on every exception that reaches it. BOTH "
            + "HALVES EXIST FOR BOTH CATCHES, and they are pinned in different places and with "
            + "different strength, so read the four statements below rather than the bucket name. This "
            + "entry has been rewritten three times: it once said there was NO CATCH AT ALL, then that "
            + "the catch existed with its mis-attribution half owed and absent, and then that StageAsync "
            + "was the whole of the file. All three are now false and each has been replaced rather "
            + "than softened. "
            + "WHAT IS THERE, FIRST CATCH: StageAsync catches a DbUpdateException whose inner "
            + "PostgresException carries the unique-violation SQLSTATE and whose ConstraintName is EITHER "
            + "PK_key_rotations OR PK_key_rotation_seals, and it CONVERGES rather than conflicting — "
            + "detach the rolled-back attempt's Added rows, re-read the account's staged row and its "
            + "seals, copy the submitted generation onto them, and save once more — ONE BOUNDED RETRY "
            + "and never a loop, because the only state the re-read can find that the first attempt did "
            + "not is a row the request that beat this one committed. "
            + "NEVER A 409, and that is the port's own promise rather than a preference: begin is the "
            + "repair path a refused completion is answered by, so a conflict raised at the one moment "
            + "the path is under load would leave a client holding a staged row it cannot replace and a "
            + "run it cannot finish, with no route that removes either. "
            + "THE TRANSLATION IS PINNED FROM THE OUTSIDE, because the convergence is only observable "
            + "where a begin has a caller — the repository-layer file beside it can stage a violation "
            + "but cannot stage a RACE: KeyRotationBeginEndpointTests."
            + "BeginKeyRotation_FromTwoConcurrentRequests_ConvergesOnOneStagedRotation posts two whole "
            + "begins of one account at once and asserts that NEITHER answers 500, that exactly one "
            + "staged rotation survives, and that its seals are ONE generation's rather than a blend of "
            + "two — the last being the half a retry written at the wrong ring fails. "
            + "BeginKeyRotation_Twice_ReplacesTheStagedGenerationAndItsSeals is the sequential statement "
            + "of the same promise, over a path where no violation is raised at all. "
            + "WHAT THAT PIN DOES NOT HOLD, said here rather than left for the next reader to discover: "
            + "NO TEST IN THE SUITE DETERMINISTICALLY EXERCISES THE RETRY. The concurrency test cannot "
            + "force two Task.WhenAll requests to interleave, so it is green over a repository whose "
            + "catch was deleted outright — it can only lose toward a false pass, never toward a false "
            + "failure, and its own remarks say so. Do not read this bucket as covering the retry. "
            + "TWO FACTS ARE MEASURED RATHER THAN REASONED, by mutations run against a forced race that "
            + "is not in the suite and that nothing standing reproduces. First, BOTH CONSTRAINT NAMES "
            + "ARE GENUINELY REACHABLE: narrowing the filter to either name alone reddened the "
            + "sequential replacement test under that race, which is the executable form of the "
            + "parent-and-children-in-one-batch argument — which of the two reports the violation "
            + "depends on statement order inside the batch and is not a thing a caller can predict. "
            + "Second, THE DETACH IS LOAD BEARING: without it the re-read resolves through EF's identity "
            + "map to the instances the failed save left Added, so the converge finds its own objects, "
            + "takes them for the staged row, changes nothing, and re-issues the very INSERT that "
            + "raised. "
            + "THE MIS-ATTRIBUTION CONTROL IS KeyRotationRepositoryTests."
            + "StageAsync_WhenATrackedRowBreaksAnotherUniqueRule_LetsTheViolationEscape, written to the "
            + "shape PasskeyRepositoryTests, RecoveryCodeRepositoryTests and RegistrationRepositoryTests "
            + "already hold: a tracked row breaking an unrelated unique rule in the same SaveChanges, "
            + "asserted to ESCAPE rather than be converged away. It tracks a second users row carrying "
            + "the seeded account's address — IX_users_email, unique TABLE-WIDE REGARDLESS OF OWNER, so "
            + "it is keyed on a value a STRANGER holds — and hands StageAsync a begin that is beyond "
            + "reproach: no rotation staged for the account and one factor with no seal against it, so "
            + "neither name the filter carries is breakable by the act itself. "
            + "THE RULE HAD TO COME FROM ANOTHER TABLE, and that is forced rather than preferred. "
            + "key_rotations is keyed on user_id and declares no index at all — its configuration says "
            + "in as many words that rotation_id is deliberately NOT unique — and key_rotation_seals "
            + "holds its composite key, two check constraints and two foreign keys, with no unique index "
            + "among them. The two names in the when clause are therefore the WHOLE of the unique rules "
            + "those two tables have, so a control staged on a neighbouring rule of the same table — the "
            + "shape the deleted UserRepository control had — is not merely weaker here, it is "
            + "unreachable. "
            + "THE VIOLATION HAS TO BE STAGED and cannot be produced from the outside, which is why all "
            + "four of these controls live at the repository layer rather than over HTTP: the save "
            + "covers the WHOLE change tracker rather than this method's two tables, and the begin path "
            + "as it stands leaves nothing else pending in it — the reauthentication gate flushes the "
            + "signature counter in a save of its own before StageAsync is entered, so that row is "
            + "Unchanged by then. "
            + "IT ASSERTS THE SAVE COUNT BESIDE THE EXCEPTION, AND WITHOUT THAT LINE IT WOULD BE A "
            + "DECORATION. The three sibling controls can stop at 'something escaped' because their "
            + "repositories answer a swallowed violation with a VALUE — false, or one of four outcomes — "
            + "so widening their filters turns a throw into a return. This one CONVERGES instead: "
            + "widened, the catch detaches the rolled-back attempt's Added rotation and seals, re-reads, "
            + "re-adds and saves again, and the stranger's row is still Added through all of it because "
            + "the detach loop is typed to this method's own two entities — so the second save raises "
            + "the SAME violation under the SAME constraint name and every assertion about the escaping "
            + "exception stays green. MEASURED: with the ConstraintName clause removed and only the "
            + "SQLSTATE left, this test fails on the attempt count alone, at two. WIDENING THE `when` "
            + "CLAUSE TO THE BARE SQLSTATE IS THE SINGLE CHANGE THIS ENTRY WAS ASKING SOMEBODY TO MAKE "
            + "IMPOSSIBLE, AND IT IS IMPOSSIBLE NOW. "
            + "WHAT IT STILL DOES NOT PIN IS THE RETRY, which is the limit stated above and which this "
            + "control does not close: it pins that the filter is NARROW, never that the convergence "
            + "behind it RUNS. Do not read this entry as covering both. "
            + "WHAT IS THERE, SECOND CATCH: PromoteAsync catches a DbUpdateConcurrencyException when "
            + "IsManifestPromotionLost holds — every entry EF could not account for is a FactorManifest "
            + "AND is Modified, with the entry count tested first so an exception attributable to no "
            + "entry cannot satisfy the All vacuously — and TRANSLATES it to "
            + "ConflictException(ConflictKind.FactorSetMoved), a 409 rather than the 500 an untranslated "
            + "concurrency failure would be. The rule is EF optimistic concurrency over "
            + "factor_manifests.rotation_epoch: the completion's one save promotes the manifest and every "
            + "factor together, and the manifest's UPDATE carries WHERE rotation_epoch = @original, so a "
            + "registration or a recovery-code issue that committed between this request's read and its "
            + "write matches nothing. THE FACTOR ROWS ARE NOT IN THE FILTER AND CANNOT BE — "
            + "wrapped_account_keys carries no concurrency token of any kind, so no conflict is ever "
            + "attributable to one of them and a filter written to tolerate them would be tolerating a "
            + "state this provider cannot produce. "
            + "THIS CATCH IS THE ONE HALF OF THIS CLASS WITH BOTH DIRECTIONS IN ONE FILE AND NEITHER OF "
            + "THEM DEPENDENT ON A RACE, which is the strongest statement anything in this entry makes. "
            + "KeyRotationRepositoryTests.PromoteAsync_WhenTheStoredGenerationMovedFirst_"
            + "RaisesFactorSetMoved commits the overtaking promotion on a context of its own, IN ORDER, "
            + "between the read and the write — optimistic concurrency over one stored integer needs no "
            + "interleaving to stage — and asserts the member a client branches on, that the surviving "
            + "manifest is the racer's BYTES rather than its epoch (both promotions write the same "
            + "number, so the epoch tells the two apart not at all), and that the factor never adopted "
            + "its seal, which is what the sentence 'nothing here was written' owes somebody whose "
            + "account keys these are. "
            + "THE MIS-ATTRIBUTION CONTROL IS KeyRotationRepositoryTests.PromoteAsync_"
            + "WhenAStrangersRowLosesItsOwnRowCount_LetsTheFailureEscape: a bare users row the act's "
            + "context holds as Modified, deleted underneath it, so its UPDATE matches nothing and EF "
            + "attributes the shortfall to an entry that is not a manifest. It asserts the escape AND "
            + "reads the entries off the escaping exception, because 'something other than a "
            + "ConflictException escaped' is satisfied by a catch that was deleted outright — naming "
            + "what EF attributed the failure to is what says the predicate LOOKED and declined, and it "
            + "is the line that would fail if somebody relaxed All to Any, which reads as a tightening "
            + "and is the exact opposite. "
            + "WHAT THE PROMOTION HALF DOES NOT PIN: the STATE clause. A manifest conflicting in any "
            + "state but Modified would have to be Deleted, and no route, repository or grant in the "
            + "product removes one — the application role holds no DELETE on factor_manifests at all — so "
            + "that half is unreachable from anything this path can produce and is carried on the "
            + "argument in the predicate's own remarks rather than by a test"),
        new(
            nameof(NarrativeResealRepository),
            "RepositoryConstraintAttributionTests, KeyRotationUnderChangeEndpointTests and ResealChunkTests",
            "ONE CATCH, ON SaveAsync, NARROWED ON FOUR CONSTRAINT NAMES AND ON NOTHING WIDER. This entry "
            + "used to say the class had no catch anywhere in it and that the day one was added both "
            + "halves became owed; that sentence is now false and is replaced rather than softened. "
            + "WHAT IS THERE: SaveAsync catches a DbUpdateException whose inner PostgresException carries "
            + "the unique-violation SQLSTATE and whose ConstraintName is one of the four blind-index "
            + "uniqueness rules a chunk can break — PayeeConfiguration.NameIndexName, "
            + "AccountConfiguration.NameIndexName, CategoryGroupConfiguration.NameIndexName and "
            + "CategoryConfiguration.NameIndexName — and TRANSLATES it to a ConflictException spelled "
            + "rotation_name_collision, a 409 rather than the 500 an untranslated save answered. The "
            + "violation is reachable during a run: a row re-sealed under the incoming index key frees "
            + "its outgoing value, a stale tab writes a second row under that freed value, and the chunk "
            + "that re-seals the second row onto the first's new value breaks the index. The message "
            + "names no row, because ConflictExceptionHandler copies it into the response verbatim. "
            + "NEVER ON SQLSTATE ALONE, for this folder's usual reason: SaveAsync flushes the whole "
            + "change tracker, so a stranger's 23505 would come back telling a client to rename a row "
            + "that broke nothing. A 42501 the day a rotation_id leaves one of the five GRANT UPDATE "
            + "column lists, and every other constraint the five tables carry, still leave as the "
            + "DbUpdateException EF threw. "
            + "BOTH HALVES ARE HELD AT THE REPOSITORY LAYER, in RepositoryConstraintAttributionTests: "
            + "SaveAsync_WithAResealedNameAlreadyHeld_TranslatesItsOwnUniqueIndex is the translation, "
            + "parameterised over all four tables so a filter naming three of the four indexes is red on "
            + "the fourth, and SaveAsync_WhenATrackedRowBreaksAnotherUniqueIndex_LetsTheViolationEscape "
            + "is one control — a tracked users row reusing a taken address, IX_users_email, the same "
            + "23505 under a name that also begins IX_, so a filter on SQLSTATE alone or on the IX_ "
            + "prefix swallows it. SaveAsync_WhenATrackedRowBreaksAnotherNameIndex_LetsTheViolationEscape "
            + "is the other — a second nameless budget for the same owner, IX_budgets_user_id_name, so a "
            + "filter keyed on the _name fragment swallows it. No real index outside the four ends in "
            + "_name_key, so a filter on that suffix is reddened by neither. It stays in this bucket "
            + "rather than the covered-here one because its route-level answer and its read predicate "
            + "are pinned in two other files. "
            + "KeyRotationUnderChangeEndpointTests holds the answer a client sees: the 409 and its "
            + "token over HTTP on payees and on accounts, that the chunk rolls back across the two arms "
            + "it drives there, "
            + "that the body carries no row identifier, and that a run refused this way can still be "
            + "completed once the colliding row is renamed on the ordinary route. "
            + "WHAT THE CONTROL DOES NOT PIN: a filter that names a fifth unique rule by mistake — "
            + "PK_payees, say — is not reddened by either intruder. No case stages a primary-key "
            + "violation into this save. "
            + "WHAT ResealChunkTests PINS is the class's other half, and the file is named because that "
            + "is where this adapter is driven against a real PostgreSQL on the least-privilege role: "
            + "ResealChunk_LoadsOnlyTheRowsTheChunkNames asks ListPayeesAsync and ListTransactionsAsync "
            + "directly, out of a budget holding a sibling beside each named row, which is the one place "
            + "the identifier predicate is observable at all — a handler driving from the command writes "
            + "to none of an over-fetch, so the surplus is invisible on disk. "
            + "ResealChunk_StampsEveryRowItRewrites_InOneTransaction and "
            + "ResealChunk_WhenTheUnitOfWorkIsAbandoned_LeavesEveryRowAsItWas exercise SaveAsync over "
            + "that role in both directions. "
            + "THREE ALTERNATIVES WERE CONSIDERED AND REJECTED, and the third is the one this census was "
            + "built to catch. An internal class would have kept it out of the census entirely — "
            + "Discovery_IsBlindToARepositoryOutsideTheNamespace is a permanent demonstration that "
            + "visibility is a blind spot — so nobody would ever have had to say any of the above. "
            + "Declaring the six members on an existing repository would have spread one chunk's reads "
            + "across five classes, and each of those already narrows a 23505 of its own on the same "
            + "index, into a DIFFERENT answer — the create's duplicate_name and the rename's 400 — so "
            + "this save would have arrived under a filter argued about another verb. MOVING IT TO "
            + "Infrastructure.Persistence IS THE DEFECT THIS FILE NAMES IN ITS OWN REMARKS: the scan is "
            + "scoped by namespace, so a repository placed outside Infrastructure.Repositories is not "
            + "discovered, not unlisted, and not red — the census would have gone on passing with one "
            + "fewer subject. Discovery_FindsExactlyTheRepositoriesTheNamespaceDeclares is the line that "
            + "would have caught the move and it only works because the names are pinned there, which is "
            + "why this type is named in BOTH places"),
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
            "both halves on all four narrowings, and the SHAPE of the second half is what this entry "
            + "says: ONE test controls all four clauses, not one test per clause. RegisterAsync filters "
            + "on four index names — IX_credentials_provider_subject, IX_users_email, "
            + "IX_passkey_public_keys_webauthn_credential_id and PK_wrapped_account_keys — and each is "
            + "TRANSLATED over HTTP by one test that stages the collision end to end: "
            + "Registration_WhenTheSubjectAlreadyHasAnAccount_Returns409AndChangesNothing, "
            + "Registration_WhenTheEmailBelongsToAnotherAccount_Returns409 — which reads the DETAIL "
            + "sentence, because it is the only thing separating it from the subject conflict once the "
            + "handler's disambiguating re-read has run — "
            + "Registration_WhenTheAuthenticatorIsAlreadyRegistered_Returns409 and "
            + "Registration_WhenAFactorIdIsAlreadyRegistered_Returns409. Each also asserts that nothing "
            + "was written, which is what a translation test on a save of roughly thirty rows owes. "
            + "THE MIS-ATTRIBUTION CONTROL IS AT THIS LAYER, in RegistrationRepositoryTests: "
            + "RegisterAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape registers a bystander "
            + "account, keeps one of the ten recovery-code VERIFIERS it minted, and submits a second "
            + "registration — different subject, different email, fresh handle, fresh factor ids, fresh "
            + "account id — with exactly one of its ten RecoveryCodeHash rows rebuilt from that "
            + "verifier. The rule it breaks is PK_recovery_code_hashes, the primary key over "
            + "verifier_hash, unique TABLE-WIDE REGARDLESS OF OWNER, so it is keyed on a value a "
            + "STRANGER holds — the same property that makes this control matter for IX_users_email and "
            + "IX_credentials_provider_subject. It asserts that something escaped, that it is a "
            + "DbUpdateException, that its SQLSTATE is the unique violation, that its ConstraintName IS "
            + "PK_recovery_code_hashes and that it is NONE of the four names above, plus that nothing of "
            + "the refused account was written and that the bystander's ten hashes survive. FOUR "
            + "CLAUSES, ONE TEST, because widening ANY ONE of them to the bare SQLSTATE swallows this "
            + "violation. That was watched, on the EMAIL clause: with IsUniqueViolationOf(exception, "
            + "UserConfiguration.EmailIndexName) widened to a bare PostgresException SqlState check, "
            + "this test failed on its first assertion and "
            + "RegisterAsync_AfterARefusedRegistration_LeavesTheContextUsable stayed green — this pin is "
            + "the only thing in the suite that notices the widening. Three of the unique rules this "
            + "save could meet are narrower than they look — IX_budgets_user_id_name, "
            + "IX_credentials_user_id_federated and IX_credentials_user_id_recovery_codes are all keyed "
            + "on a user_id derived for this registration alone and cannot be breached by anything this "
            + "save writes, which is why RegisterAsync deliberately does not narrow on them AND why the "
            + "control had to be staged on a table-wide rule: a per-account rule is unbreakable from "
            + "here, so no arrangement built on one of those three could have reached a catch at all"),
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
            "both halves on its one remaining narrowing: DeleteAsync's entries-based narrowing by "
            + "_WhenAnotherRequestErasedTheRowFirst_Completes and "
            + "_WhenTheConflictNamesAnotherEntity_LetsItEscape. "
            + "IT HAD A SECOND NARROWING AND THE SUITE LOST A CONTROL WITH IT. TryAddAsync — the "
            + "insert that wrote a user, its federated credential and its default budget in one save "
            + "for the deleted provisioning path — filtered a 23505 on two index names, "
            + "IX_credentials_provider_subject and IX_users_email, and was pinned in both directions "
            + "here: the TryAddAsync_With* tests translated it and "
            + "TryAddAsync_WhenATrackedRowBreaksAThirdUniqueRule_LetsTheViolationEscape controlled it "
            + "by staging a third credentials index, IX_credentials_user_id_federated, so the control "
            + "lived between two neighbouring rules on one table rather than across tables. That "
            + "method and every one of those tests are gone with the middleware. "
            + "The two-name filter itself is not gone: RegistrationRepository.RegisterAsync narrows on "
            + "the same two index names, among four. THE CONTROL THIS ENTRY USED TO NAME HAS BEEN "
            + "REPLACED, NOT MERELY LOST — RegistrationRepositoryTests."
            + "RegisterAsync_WhenAnotherUniqueRuleIsBroken_LetsTheViolationEscape stages an unrelated "
            + "23505 into that surviving catch shape, and that entry says so in its own words. The two "
            + "are not the same control and the difference is worth keeping: the deleted one staged a "
            + "THIRD INDEX ON credentials, so it sat between two neighbouring rules on one table, while "
            + "the replacement stages PK_recovery_code_hashes, a rule on another table entirely. Both "
            + "are table-wide rather than per-account, which is the property that lets either one stand "
            + "in for a stranger's row"),
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
            nameof(KeyRotationRepository),
            nameof(NarrativeResealRepository),
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
