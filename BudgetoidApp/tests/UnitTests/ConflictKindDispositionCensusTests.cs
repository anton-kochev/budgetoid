using System.Text.RegularExpressions;
using System.Xml.Linq;
using Domain.Common;

namespace UnitTests;

/// <summary>
/// One production source file that names a <see cref="ConflictKind" /> member in code, and the ordered
/// sequence of members it names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sequence is ordered and not a set, and that is the half a reader will want to relax.</b> A set
/// — or a count per member — cannot see two sites inside one file trading kinds with each other, which
/// is exactly the shape <c>RegisterAccountHandler.RefusalFor</c> can take: its <c>EmailTaken</c> arm is a
/// ternary whose two branches carry opposite remedies, and swapping them leaves every count in that file
/// identical while telling somebody who cannot get in to go and sign in. Source order distinguishes
/// them. The cost is that reordering two sites without changing either one reddens this file; that is
/// not a routine edit, unlike a package version bump or a copy edit, both of which this census is
/// deliberately blind to.
/// </para>
/// </remarks>
/// <param name="File">The path relative to the solution root, with <c>/</c> separators.</param>
/// <param name="Raises">The member names, in the order they appear in the file.</param>
public sealed record ConflictSite(string File, IReadOnlyList<string> Raises);

/// <summary>
/// A person's statement of which <see cref="ConflictKind" /> members one file is allowed to name, and
/// why.
/// </summary>
/// <param name="File">
/// The path relative to the solution root, transcribed. This side is text and the discovered side is
/// text, which is what makes <see cref="ConflictDispositionCensus.NamingNoFile" /> a bucket this census
/// needs and its sibling over the enum members does not.
/// </param>
/// <param name="Raises">
/// The members, in source order, written with <c>nameof</c>. Referencing the member rather than
/// transcribing the word is deliberate and is the opposite choice from
/// <c>ConflictKindSpellingTests</c>: there the subject is the wire contract, so the word has to be typed
/// out or a rename reissues it silently. Here the subject is the mapping from a site to a remedy, and a
/// member rename moves the source and this line together without changing what any client reads — so a
/// transcribed word would redden on the one edit that changes nothing.
/// </param>
/// <param name="Note">
/// What this file does with the members it names. No default value: a row somebody pasted without
/// saying what the site is, is the state this member exists to leave behind — and one of the twelve
/// rows is not a throw site at all, which nothing but a sentence can tell a reader.
/// </param>
public sealed record ConflictSitePin(string File, IReadOnlyList<string> Raises, string Note);

/// <summary>
/// Every discovered file sorted by what the pins say about it.
/// </summary>
/// <param name="Unpinned">
/// Files naming a conflict kind that no pin claims. This is the bucket a nineteenth throw site in a new
/// file lands in, and it is red rather than derived: there is nothing to compute for a site nobody has
/// decided the remedy of.
/// </param>
/// <param name="NamingNoFile">
/// Pins naming a path the scan found no conflict kind in. Reported rather than dropped, for the reason
/// <c>RepositoryAttributionCensusTests</c> reports the same shape: a stale row silently shrinks the
/// census by one and lies in wait for whatever is next put at that path. It is also the bucket that
/// catches a scan which came back empty — a broken walker, a published output with no source tree, a
/// stripper that swallowed the whole file — because all twelve pins would land here at once.
/// </param>
/// <param name="PinnedTwice">
/// Paths more than one pin names. Two pins are two answers to "what may this file raise", and the one
/// nobody reads is the one that rots.
/// </param>
/// <param name="Disagreeing">
/// Files whose pinned sequence and raised sequence differ, rendered as both sequences so a failure says
/// which remedy the site moved from and to. This is the bucket the measured defect lands in: a
/// repository swapped from <see cref="ConflictKind.DuplicateIdentifier" /> to
/// <see cref="ConflictKind.DuplicateName" /> compiles, answers 409 with a real token, and told every
/// client that a retried POST was a stale list.
/// </param>
/// <param name="Agreed">
/// Files with exactly one pin whose sequence matches. Carried so a caller can prove the census read real
/// files rather than passing over an empty sequence.
/// </param>
public sealed record ConflictDispositionCensus(
    IReadOnlyList<string> Unpinned,
    IReadOnlyList<string> NamingNoFile,
    IReadOnlyList<string> PinnedTwice,
    IReadOnlyList<string> Disagreeing,
    IReadOnlyList<string> Agreed);

/// <summary>
/// Pins which <see cref="ConflictKind" /> member every conflict in the product is raised with, by
/// reading the production source and refusing a site nobody has decided about.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes, measured rather than argued.</b> <c>ConflictKindSpellingTests</c> pins the
/// eight members against the eight tokens, so a kind that is <i>declared</i> wrongly is red. Nothing saw
/// a kind that is declared correctly and <i>raised</i> wrongly: mutating
/// <c>AccountRepository</c>, <c>CategoryRepository</c>, <c>CategoryGroupRepository</c> and
/// <c>TransactionRepository</c> to raise <see cref="ConflictKind.DuplicateName" /> in place of
/// <see cref="ConflictKind.DuplicateIdentifier" /> left the integration suite at 810/810 and the unit
/// suite at 880/880. Four of the five client-minted tables could tell every client that a retried POST
/// was a stale list, and the bar stayed green. That is the same defect the member was added to end, one
/// level up.
/// </para>
/// <para>
/// <b>Why it reads source text, and why nothing else could.</b> Reflection cannot see a throw site: the
/// kind is an argument at a construction, so by the time an assembly is loaded there is nothing left to
/// enumerate. The alternatives are staging all eighteen conflicts over HTTP — eighteen containers for a
/// property that is decided in one argument — or reading the <c>.cs</c>. The repository already has the
/// second shape in <c>ProjectReferenceGraphTests</c>, which walks up to <c>BudgetoidApp.sln</c> and
/// parses every build file from disk; this follows it, including the walker's habit of throwing rather
/// than returning nothing when no ancestor holds the solution. The walker is re-derived here rather than
/// shared because the one over there is private to its guard, and promoting it would give one method two
/// owners for the sake of ten lines.
/// </para>
/// <para>
/// <b>The subject is discovered and only the disposition is written down.</b> What is discovered is
/// every <i>code</i> occurrence of <c>ConflictKind.&lt;Member&gt;</c> in every project that does not
/// declare <c>&lt;IsTestProject&gt;true&lt;/IsTestProject&gt;</c> — which is wider than the throw sites
/// on purpose. A census keyed on <c>new ConflictException(</c> would have found eight of the eighteen:
/// five of the repositories build theirs through a target-typed <c>new(</c> in a factory method, where
/// the type name appears only on the return type, and every one of the four sites in the measured
/// mutation is one of those. Keying on the enum reference instead means the census does not care how the
/// exception is constructed, or whether it is constructed at that line at all — a kind assigned to a
/// field, passed through a helper, or read in a <c>switch</c> is a row like any other, and a person has
/// to say what it is.
/// </para>
/// <para>
/// <b>One of the twelve rows is not a throw site, and that is the widening working rather than a
/// mis-fit.</b> <c>Domain/Common/ConflictKindSpelling.cs</c> names all eight members in its
/// <c>switch</c>; it is pinned like the rest and its note says what it is. The value of keeping it in is
/// that a ninth place naming a kind — a branch on the kind in <c>ConflictExceptionHandler</c>, say,
/// which would make the API's rendering depend on the remedy — cannot land without somebody writing a
/// sentence about it here.
/// </para>
/// <para>
/// <b>The stripper is the honest risk, and it is built to fail closed.</b> Comment-borne references are
/// real in this tree — <c>PayeeRepository</c> names both of its kinds in a <c>&lt;see cref&gt;</c> as
/// well as in code, and <c>RegistrationConflicts</c> names one in prose and none in code — so a raw text
/// scan reports three sites that raise nothing. What the scanner therefore does is skip comments,
/// ordinary strings, verbatim strings, raw strings and char literals, and skip <b>nothing else</b>: an
/// interpolated string's content is scanned rather than dropped, because a hole is code and losing one
/// would be a site the census cannot see. Every skip is in the direction that adds a row and reddens;
/// no skip is in the direction that removes one. <c>Scan_KeepsReadingCodeAfter…</c> below are the
/// controls for the failure that would be silent — a mis-tracked quote swallowing the code that follows
/// it — and <c>Scan_ReadsTheOneRepositoryHoldingBothACommentAndACodeReference</c> runs the whole thing
/// over real repository text rather than a synthetic string.
/// </para>
/// <para>
/// <b>The key is a path and not a file name.</b> Two files can share a stem — this solution already has
/// three <c>Program.cs</c> — and two sites merged under one key would be a silent narrowing of the
/// census. A path cannot collide, so the uniqueness is structural rather than asserted, and the row
/// tells a reader which ring raises the conflict, which is part of judging whether a new one belongs
/// there. The price is that moving a file reddens its row; that is a real edit and worth a person
/// confirming.
/// </para>
/// <para>
/// <b>The honest limit.</b> Discovery is scoped to projects that do not declare themselves test
/// projects, so a conflict raised from a project wrongly marked as one is invisible — see
/// <c>Discovery_IsBlindToAProjectThatDeclaresItselfATestProject</c>, which is a permanent demonstration
/// rather than a defect to fix by widening the scan. The scoping is the right way round: a <i>new</i>
/// test project that forgets the marker is scanned and reddens, which is the safe direction, and a
/// production project that gained the marker would stop being tested by <c>dotnet test</c> long before
/// this census noticed. What no census of this shape can judge is whether a pinned remedy is the
/// <i>right</i> one; only a reviewer reading the argument beside the site can say that, and this file's
/// whole claim is that they get to read it, because the site cannot move without the pin moving.
/// </para>
/// <para>
/// The synthetic cases below are permanent negative controls, not scaffolding. They feed the census
/// arrangements this solution does not have, so the live assertion cannot pass by having found nothing
/// to report.
/// </para>
/// </remarks>
public sealed partial class ConflictKindDispositionCensusTests
{
    /// <summary>
    /// The one file naming conflict kinds that raises none of them, excluded from the "every member is
    /// raised somewhere" control below so that control cannot be satisfied by the table alone.
    /// </summary>
    private const string SpellingTablePath = "Domain/Common/ConflictKindSpelling.cs";

    /// <summary>
    /// The twelve files, and what each one raises. A thirteenth file naming a conflict kind is red until
    /// it has a line here, and the line is where somebody says which remedy that site asks of a caller.
    /// </summary>
    private static readonly ConflictSitePin[] Pinned =
    [
        new(
            "Application/Passkeys/CompleteRegistration/CompleteRegistrationHandler.cs",
            [nameof(ConflictKind.AuthenticatorAlreadyRegistered)],
            "Adding a passkey to a live account with an authenticator already enrolled. Deliberately "
            + "RegisterAccountHandler's kind for the same fact reached from the other route — the "
            + "account exists here and is being created there, and the caller uses a different "
            + "authenticator either way — and deliberately NOT FactorAlreadyRegistered, which is the "
            + "neighbouring refusal of the same ceremony and asks for work the caller has to do first"),
        new(
            "Application/Passkeys/RevokePasskey/RevokePasskeyHandler.cs",
            [nameof(ConflictKind.LastPasskey)],
            "The account's only passkey. The one conflict in the product whose remedy is an act on a "
            + "DIFFERENT resource — register another passkey, then come back — which is why it holds a "
            + "member no other site shares"),
        new(
            "Application/Registration/BeginAccountRegistrationHandler.cs",
            [nameof(ConflictKind.SubjectAlreadyRegistered)],
            "The options leg refusing a provider subject that already holds an account, above its own "
            + "IssueAsync. It shares this member with the finish leg because a client meeting one on "
            + "this visit and the other on the next must branch identically; the pair travels together "
            + "and neither may be changed alone"),
        new(
            "Application/Registration/RegisterAccountHandler.cs",
            [
                nameof(ConflictKind.SubjectAlreadyRegistered),
                nameof(ConflictKind.EmailAlreadyLinked),
                nameof(ConflictKind.SubjectAlreadyRegistered),
                nameof(ConflictKind.AuthenticatorAlreadyRegistered),
                nameof(ConflictKind.FactorAlreadyRegistered),
            ],
            "RefusalFor, one arm per RegistrationOutcome, and THE ORDER IS THE POINT ON THIS ROW. The "
            + "second and third entries are the two branches of one ternary: the EmailTaken arm re-reads "
            + "the credential, and finding no winner means the address belongs to another Google "
            + "identity (EmailAlreadyLinked — no passkey or code of theirs opens it), while finding one "
            + "means the subject collided too (SubjectAlreadyRegistered — they can sign in). Swapping "
            + "those two leaves every per-member count in this file identical and is the one mutation a "
            + "set-valued pin could not see"),
        new(
            SpellingTablePath,
            [
                nameof(ConflictKind.DuplicateIdentifier),
                nameof(ConflictKind.DuplicateName),
                nameof(ConflictKind.SubjectAlreadyRegistered),
                nameof(ConflictKind.EmailAlreadyLinked),
                nameof(ConflictKind.AuthenticatorAlreadyRegistered),
                nameof(ConflictKind.FactorAlreadyRegistered),
                nameof(ConflictKind.LastPasskey),
                nameof(ConflictKind.RecoveryCodesReplaced),
            ],
            "NOT A THROW SITE. This is the spelling table's switch, in declaration order, and it is "
            + "pinned for the same reason the scan is keyed on the enum reference rather than on a "
            + "constructor: any OTHER place that comes to name a kind — a branch on the remedy in "
            + "ConflictExceptionHandler, a mapping in a route — arrives as a row nobody claims. What "
            + "this row does not do is check the tokens; ConflictKindSpellingTests owns that, and a "
            + "member added there without a token is red in that file before it is red in this one"),
        new(
            "Infrastructure/Repositories/AccountRepository.cs",
            [nameof(ConflictKind.DuplicateIdentifier)],
            "PK_accounts, reported over the name index because PostgreSQL checks a relation's indexes "
            + "in OID order and the key is created with the table. One of the four this census was "
            + "written for: raising DuplicateName here compiles and tells a retried POST to re-read a "
            + "list, sending somebody looking for a name that may not be there"),
        new(
            "Infrastructure/Repositories/CategoryGroupRepository.cs",
            [nameof(ConflictKind.DuplicateIdentifier)],
            "PK_category_groups. The duplicate-NAME rule on this table answers 400 keyed on Name and "
            + "raises no conflict at all, which is why only one member appears in this file"),
        new(
            "Infrastructure/Repositories/CategoryRepository.cs",
            [nameof(ConflictKind.DuplicateIdentifier)],
            "PK_categories, with the same 400-not-409 split on the name rule as its group above"),
        new(
            "Infrastructure/Repositories/PasskeyRepository.cs",
            [nameof(ConflictKind.FactorAlreadyRegistered)],
            "PK_wrapped_account_keys on a client-minted factor id. Not AuthenticatorAlreadyRegistered, "
            + "though the same TryAddAsync also filters the WebAuthn credential index — that arm "
            + "answers false rather than throwing, so it reaches no conflict and appears in no row"),
        new(
            "Infrastructure/Repositories/PayeeRepository.cs",
            [nameof(ConflictKind.DuplicateName), nameof(ConflictKind.DuplicateIdentifier)],
            "The only file raising two different members, and the pair is the whole argument for the "
            + "vocabulary: one blind index answers 409 DuplicateName on the create — adopt the row that "
            + "already exists — and 400 on the rename, where the remedy IS a field somebody can "
            + "correct, while the primary key answers 409 DuplicateIdentifier. It also carries a "
            + "&lt;see cref&gt; to both members in prose, which is what makes it the real-source "
            + "control for the comment stripper"),
        new(
            "Infrastructure/Repositories/RecoveryCodeRepository.cs",
            [
                nameof(ConflictKind.RecoveryCodesReplaced),
                nameof(ConflictKind.FactorAlreadyRegistered),
                nameof(ConflictKind.RecoveryCodesReplaced),
            ],
            "AddSetAsync loses a race (twice — the delete leg and the insert leg) and meets a factor id "
            + "already standing. The two members are deliberately unalike: a replaced set is a fact "
            + "about timing whose remedy is a fresh re-authentication, and a standing factor id at 122 "
            + "random bits is never chance"),
        new(
            "Infrastructure/Repositories/TransactionRepository.cs",
            [nameof(ConflictKind.DuplicateIdentifier)],
            "PK_transactions. The leaf nothing references, so it carries no composite alternate key to "
            + "be reported under and no name index to compete with — one member, one rule"),
    ];

    [Test]
    public async Task EveryConflictSite_RaisesTheKindThePinSaysItDoes()
    {
        // Arrange — the subject is read off disk and never written down here. A file that stops naming
        // a conflict kind disappears from this sequence rather than being reported as raising nothing,
        // which is why NamingNoFile is the bucket that catches a deletion.
        IReadOnlyList<ConflictSite> found =
            ConflictSiteScan.Of(ConflictSiteScan.SolutionRootFrom(AppContext.BaseDirectory));

        // Act
        ConflictDispositionCensus census = ConflictDisposition.Take(found, Pinned);

        // Assert — IsEmpty on the bucket and never IsEqualTo over a joined string, which is what the
        // sibling census does and what this file started as. Measured on TUnit 1.56: the string
        // comparison renders about a hundred characters of the received value and then an ellipsis, so
        // the four-repository mutation this census was written for reported ONE of its four offenders
        // and hid the rest. The collection form prints every item. A guard whose failure names one of
        // four is the shape of guard that gets "fixed" one line at a time.
        //
        // The collection form has a ceiling of its own, also measured: past ten items it prints ten and
        // "and N more…". That ceiling is only reachable by a failure that is not about one site — a
        // dead scanner puts all twelve pins in NamingNoFile — so it costs nothing a reader needs, and
        // it is written down here rather than discovered by somebody counting the ten it printed.
        await Assert.That(census.Unpinned).IsEmpty();
        await Assert.That(census.NamingNoFile).IsEmpty();
        await Assert.That(census.PinnedTwice).IsEmpty();
        await Assert.That(census.Disagreeing).IsEmpty();

        // Non-vacuity, and the half that makes the four lines above mean anything: every pinned file
        // was reached and agreed. Without it a scan that came back empty leaves four empty buckets.
        // Written as a one-way difference rather than an equality for the same rendering reason, and
        // for the reason ProjectReferenceGraphTests gives: a failure has to name the row that moved.
        string[] neverAgreed =
        [
            .. Pinned.Select(pin => pin.File)
                .Except(census.Agreed, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        await Assert.That(neverAgreed).IsEmpty();

        // Set difference is blind to a row counted twice, which is the one way Agreed could hold every
        // pinned file and still not be the pinned set.
        await Assert.That(census.Agreed.Count).IsEqualTo(Pinned.Length);

        // A second, independent net: no member of the vocabulary is dead text. The spelling table names
        // all eight by itself, so it is excluded — otherwise this line would be satisfied by one file
        // and would say nothing about whether anything RAISES a kind. Both directions, because a
        // scanner inventing a member name is as much a defect as a member nothing raises.
        string[] raisedSomewhere =
        [
            .. found
                .Where(site => !string.Equals(site.File, SpellingTablePath, StringComparison.Ordinal))
                .SelectMany(site => site.Raises)
                .Distinct(StringComparer.Ordinal),
        ];

        await Assert.That(Enum.GetNames<ConflictKind>().Except(raisedSomewhere, StringComparer.Ordinal))
            .IsEmpty();
        await Assert.That(raisedSomewhere.Except(Enum.GetNames<ConflictKind>(), StringComparer.Ordinal))
            .IsEmpty();
    }

    [Test]
    public async Task Discovery_ReadsTheProductionRingsAndNotTheTestProjects()
    {
        // Arrange
        DirectoryInfo root = ConflictSiteScan.SolutionRootFrom(AppContext.BaseDirectory);

        // Act
        IReadOnlyList<string> scanned = ConflictSiteScan.ProductionSourcesUnder(root);

        // Assert — every ring that raises a conflict is reached, so the census cannot be green because
        // the walker found one folder. The test projects are absent, which is what keeps this file's own
        // synthetic ConflictKind references out of the subject; without it the census would have to pin
        // its own source and would redden on every edit to itself.
        await Assert.That(Holding(scanned, "/Domain/")).IsTrue();
        await Assert.That(Holding(scanned, "/Application/")).IsTrue();
        await Assert.That(Holding(scanned, "/Infrastructure/")).IsTrue();
        await Assert.That(Holding(scanned, "/Api/")).IsTrue();
        await Assert.That(Holding(scanned, "ConflictKindDispositionCensusTests.cs")).IsFalse();
        await Assert.That(Holding(scanned, "ConflictKindSpellingTests.cs")).IsFalse();

        // Build output is not source. A scan reading obj/ would count generated copies of files it has
        // already read and report every site twice.
        await Assert.That(Holding(scanned, "/obj/")).IsFalse();
        await Assert.That(Holding(scanned, "/bin/")).IsFalse();

        static bool Holding(IEnumerable<string> paths, string fragment) =>
            paths.Any(path => path.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// The scope is projects, and a project can hide from it. Stated as a demonstration rather than
    /// fixed by widening the scan, because widening only moves the blind spot.
    /// </summary>
    [Test]
    public async Task Discovery_IsBlindToAProjectThatDeclaresItselfATestProject()
    {
        // Arrange — the marker is the whole of the filter, so a production project carrying it is
        // invisible here. It would also stop being run by `dotnet test`, which is the louder failure and
        // the reason this is a limit rather than a hole.
        XDocument declared = XDocument.Parse(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
            </Project>
            """);

        XDocument silent = XDocument.Parse("""<Project Sdk="Microsoft.NET.Sdk" />""");

        // Act
        bool skipped = ConflictSiteScan.DeclaresTestProject(declared);
        bool scanned = ConflictSiteScan.DeclaresTestProject(silent);

        // Assert — and the direction is the safe one: a project saying nothing is scanned, so a new test
        // project that forgets the marker reddens rather than disappearing.
        await Assert.That(skipped).IsTrue();
        await Assert.That(scanned).IsFalse();
    }

    [Test]
    public async Task SolutionRootFrom_ThrowsWhenNoAncestorHoldsTheSolution()
    {
        // Act, Assert — a walker that quietly returned null would hand the scan an empty directory, and
        // the whole census would report twelve stale pins for a reason that has nothing to do with the
        // code. Throwing names the real cause.
        await Assert.That(() => ConflictSiteScan.SolutionRootFrom(Path.GetTempPath()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Scan_ReadsTheOneRepositoryHoldingBothACommentAndACodeReference()
    {
        // Arrange — the real file, not a synthetic string. PayeeRepository names DuplicateName and
        // DuplicateIdentifier twice each: once apiece in a <see cref> arguing why the two statuses
        // differ, and once apiece in the factories that construct the exception.
        DirectoryInfo root = ConflictSiteScan.SolutionRootFrom(AppContext.BaseDirectory);
        string source = File.ReadAllText(
            Path.Combine(root.FullName, "Infrastructure", "Repositories", "PayeeRepository.cs"));

        // Act
        IReadOnlyList<string> raised = ConflictKindReferences.In(source);

        // Assert — exactly two of the four, and two DIFFERENT members. A stripper that missed
        // documentation comments reports four here and the live census reddens on a file nobody
        // touched; one that over-reached and swallowed the factories reports none, and the file falls
        // out of the census entirely.
        //
        // WHICH two is deliberately not asserted, and the omission is the point twice over. Once
        // because that is the disposition, and the disposition is pinned exactly once, in the table
        // above — a second copy here would be the PinnedTwice failure one level up, and it was: this
        // case reddened alongside the census when the two payee factories traded kinds, which is a
        // disposition change and no business of the stripper's. And once because it could not be an
        // honest claim anyway: the two crefs in this file name the same two members as the two
        // factories, so a stripper that kept the comments and dropped the code would produce a
        // byte-identical answer. What covers THAT failure is Scan_KeepsReadingCodeAfter… below,
        // which is why those three exist as well as this one.
        await Assert.That(raised.Count).IsEqualTo(2);
        await Assert.That(raised.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(2);
    }

    [Test]
    public async Task Scan_FindsAKindPassedToATargetTypedConstruction()
    {
        // Arrange — the spelling five of the eighteen sites use, and the reason this census is keyed on
        // the enum reference rather than on `new ConflictException(`: the type name appears only on the
        // return type, so a scan looking for the constructor finds nothing here.
        const string source = """
            private static ConflictException DuplicateAccountIdConflictException() => new(
                "An account already exists with this identifier.",
                ConflictKind.DuplicateIdentifier);
            """;

        // Act
        IReadOnlyList<string> raised = ConflictKindReferences.In(source);

        // Assert
        await Assert.That(string.Join(", ", raised))
            .IsEqualTo(nameof(ConflictKind.DuplicateIdentifier));
    }

    [Test]
    public async Task Scan_ReportsEveryReferenceInSourceOrderIncludingRepeats()
    {
        // Arrange — the shape RegisterAccountHandler and RecoveryCodeRepository both have: one file, one
        // member named more than once, and two members that must not be allowed to trade places.
        const string source = """
            throw new ConflictException(A, ConflictKind.RecoveryCodesReplaced);
            throw new ConflictException(B, ConflictKind.FactorAlreadyRegistered);
            throw new ConflictException(C, ConflictKind.RecoveryCodesReplaced);
            """;

        // Act
        IReadOnlyList<string> raised = ConflictKindReferences.In(source);

        // Assert — order preserved and the repeat kept. Deduplicating here would make the two
        // RecoveryCodesReplaced sites one, and a third arm added between them would move nothing.
        await Assert.That(string.Join(", ", raised)).IsEqualTo(
            $"{nameof(ConflictKind.RecoveryCodesReplaced)}, "
            + $"{nameof(ConflictKind.FactorAlreadyRegistered)}, "
            + $"{nameof(ConflictKind.RecoveryCodesReplaced)}");
    }

    [Test]
    public async Task Scan_IgnoresEveryFormOfCommentAndLiteral()
    {
        // Arrange — each line names a member somewhere that is not code. All five forms occur in this
        // tree: /// crefs on PayeeRepository and RegistrationConflicts, and the rest are one refactor
        // away from occurring.
        const string source = """
            // ConflictKind.LastPasskey
            /// <see cref="ConflictKind.DuplicateName"/>
            /* ConflictKind.EmailAlreadyLinked */
            var a = "ConflictKind.SubjectAlreadyRegistered";
            var b = @"ConflictKind.FactorAlreadyRegistered";
            """;

        // Act
        IReadOnlyList<string> raised = ConflictKindReferences.In(source);

        // Assert — nothing. This is the half without which the live census reports three sites that
        // raise no conflict at all.
        await Assert.That(string.Join(", ", raised)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Scan_ReportsAReferenceInsideAnInterpolationHole()
    {
        // Arrange — a hole is code. The scanner therefore does NOT skip an interpolated string, which
        // means the literal text of one is scanned too and a member named there is reported.
        const string source = """
            var message = $"refused: {ConflictKind.LastPasskey}";
            """;

        // Act
        IReadOnlyList<string> raised = ConflictKindReferences.In(source);

        // Assert — kept, deliberately, and the asymmetry with the plain string above is the fail-closed
        // rule this scanner is built on: every skip may add a row that a person then argues away, and no
        // skip may remove one. A scanner that treated an interpolated string like an ordinary one would
        // be silently blind to a kind chosen inside a hole.
        await Assert.That(string.Join(", ", raised)).IsEqualTo(nameof(ConflictKind.LastPasskey));
    }

    [Test]
    public async Task Scan_KeepsReadingCodeAfterAVerbatimStringHoldingQuotesAndASlash()
    {
        // Arrange — the failure that would be silent. A stripper that mis-tracked the doubled quote or
        // read the // inside the string as a comment would swallow everything after it, and the file
        // would quietly fall out of the census.
        const string source = """
            var path = @"c:\x ""quoted"" // not a comment";
            throw new ConflictException(m, ConflictKind.LastPasskey);
            """;

        // Act
        IReadOnlyList<string> raised = ConflictKindReferences.In(source);

        // Assert
        await Assert.That(string.Join(", ", raised)).IsEqualTo(nameof(ConflictKind.LastPasskey));
    }

    [Test]
    public async Task Scan_KeepsReadingCodeAfterARawStringLiteral()
    {
        // Arrange — same failure, the newest spelling. A raw string can hold anything at all, including
        // the sequence that would otherwise end an ordinary one.
        string source = "var sql = \"\"\"\n  select \"x\" -- ConflictKind.DuplicateName\n  \"\"\";\n"
            + "throw new ConflictException(m, ConflictKind.LastPasskey);";

        // Act
        IReadOnlyList<string> raised = ConflictKindReferences.In(source);

        // Assert — the member inside the raw string is not reported, and the one after it is.
        await Assert.That(string.Join(", ", raised)).IsEqualTo(nameof(ConflictKind.LastPasskey));
    }

    [Test]
    public async Task Scan_KeepsReadingCodeAfterACharLiteralHoldingAQuote()
    {
        // Arrange — one unbalanced-looking quote is enough to lose the rest of a file.
        const string source = """
            const char q = '"';
            throw new ConflictException(m, ConflictKind.LastPasskey);
            """;

        // Act
        IReadOnlyList<string> raised = ConflictKindReferences.In(source);

        // Assert
        await Assert.That(string.Join(", ", raised)).IsEqualTo(nameof(ConflictKind.LastPasskey));
    }

    [Test]
    public async Task Scan_DoesNotMistakeTheSpellingTypeForTheEnum()
    {
        // Arrange — ConflictKindSpelling shares eleven characters with ConflictKind, and a scan matching
        // the prefix would report a member called "Of" on every call in the product.
        const string source = "Spelling = ConflictKindSpelling.Of(kind);";

        // Act
        IReadOnlyList<string> raised = ConflictKindReferences.In(source);

        // Assert
        await Assert.That(string.Join(", ", raised)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_ReportsAFileNoPinClaims()
    {
        // Arrange — the case this census exists for: a nineteenth site lands in a new file and nobody
        // says which remedy it asks of a caller. Synthetic, so the proof survives the day the real set
        // is complete.
        ConflictSite[] found =
        [
            new("Infrastructure/Repositories/PinnedRepository.cs", [nameof(ConflictKind.DuplicateIdentifier)]),
            new("Infrastructure/Repositories/ForgottenRepository.cs", [nameof(ConflictKind.DuplicateName)]),
        ];

        ConflictSitePin[] pins =
        [
            new(
                "Infrastructure/Repositories/PinnedRepository.cs",
                [nameof(ConflictKind.DuplicateIdentifier)],
                "decided"),
        ];

        // Act
        ConflictDispositionCensus census = ConflictDisposition.Take(found, pins);

        // Assert — named, not counted. Its neighbour stays in Agreed, which is what says the census
        // reported the one file nobody decided about rather than giving up on the whole scan.
        await Assert.That(string.Join(", ", census.Unpinned))
            .IsEqualTo("Infrastructure/Repositories/ForgottenRepository.cs");
        await Assert.That(string.Join(", ", census.Agreed))
            .IsEqualTo("Infrastructure/Repositories/PinnedRepository.cs");
    }

    [Test]
    public async Task Census_ReportsAPinNamingAFileThatRaisesNothing()
    {
        // Arrange — the bucket its sibling over the enum members has no need of, because a pin there
        // references a member and cannot name one that is gone. Here both sides are text: a site
        // deleted, renamed or moved leaves a row claiming a path nothing raises from, which shrinks the
        // census by one and says nothing.
        ConflictSite[] found = [];
        ConflictSitePin[] pins =
        [
            new("Infrastructure/Repositories/MovedRepository.cs", [nameof(ConflictKind.LastPasskey)], "stale"),
        ];

        // Act
        ConflictDispositionCensus census = ConflictDisposition.Take(found, pins);

        // Assert — this is also the bucket that catches a dead scan, which is why the live case asserts
        // it is empty rather than trusting that an empty scan would show up elsewhere.
        await Assert.That(string.Join(", ", census.NamingNoFile))
            .IsEqualTo("Infrastructure/Repositories/MovedRepository.cs");
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_ReportsAFilePinnedTwice()
    {
        // Arrange — the well-meaning edit: a new row added without the old one being taken out, so two
        // entries answer "what may this file raise" and the reader below picks whichever is first.
        ConflictSite[] found =
        [
            new("Infrastructure/Repositories/AmbiguousRepository.cs", [nameof(ConflictKind.DuplicateName)]),
        ];

        ConflictSitePin[] pins =
        [
            new(
                "Infrastructure/Repositories/AmbiguousRepository.cs",
                [nameof(ConflictKind.DuplicateName)],
                "first"),
            new(
                "Infrastructure/Repositories/AmbiguousRepository.cs",
                [nameof(ConflictKind.DuplicateIdentifier)],
                "second"),
        ];

        // Act
        ConflictDispositionCensus census = ConflictDisposition.Take(found, pins);

        // Assert — reported as ambiguous rather than resolved. A census that took the first entry would
        // be green here on a table half of which is dead text, and the half nobody reads is the half a
        // later edit would trust.
        await Assert.That(string.Join(", ", census.PinnedTwice))
            .IsEqualTo("Infrastructure/Repositories/AmbiguousRepository.cs");
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_ReportsASiteRaisingAKindFromAnotherFamily()
    {
        // Arrange — the measured defect, and the one that costs somebody their account: the email arm of
        // RegisterAccountHandler raising SubjectAlreadyRegistered tells a person whose address belongs
        // to another Google identity to go and sign in, which no passkey or code of theirs can do.
        ConflictSite[] found =
        [
            new(
                "Application/Registration/RegisterAccountHandler.cs",
                [nameof(ConflictKind.SubjectAlreadyRegistered), nameof(ConflictKind.SubjectAlreadyRegistered)]),
        ];

        ConflictSitePin[] pins =
        [
            new(
                "Application/Registration/RegisterAccountHandler.cs",
                [nameof(ConflictKind.SubjectAlreadyRegistered), nameof(ConflictKind.EmailAlreadyLinked)],
                "decided"),
        ];

        // Act
        ConflictDispositionCensus census = ConflictDisposition.Take(found, pins);

        // Assert — both sequences in the message, because "RegisterAccountHandler disagrees" sends the
        // reader back to two files to find out which way, and the direction is the whole of the harm.
        await Assert.That(string.Join(" | ", census.Disagreeing)).IsEqualTo(
            "Application/Registration/RegisterAccountHandler.cs: "
            + "pinned 'SubjectAlreadyRegistered, EmailAlreadyLinked', "
            + "raises 'SubjectAlreadyRegistered, SubjectAlreadyRegistered'");
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_ReportsASiteWhoseKindsAreRightAndOutOfOrder()
    {
        // Arrange — the mutation a set-valued or count-valued pin cannot see: two sites in one file
        // trading kinds. Every per-member count is identical, and the two remedies have swapped.
        ConflictSite[] found =
        [
            new(
                "Infrastructure/Repositories/PayeeRepository.cs",
                [nameof(ConflictKind.DuplicateIdentifier), nameof(ConflictKind.DuplicateName)]),
        ];

        ConflictSitePin[] pins =
        [
            new(
                "Infrastructure/Repositories/PayeeRepository.cs",
                [nameof(ConflictKind.DuplicateName), nameof(ConflictKind.DuplicateIdentifier)],
                "decided"),
        ];

        // Act
        ConflictDispositionCensus census = ConflictDisposition.Take(found, pins);

        // Assert
        await Assert.That(string.Join(" | ", census.Disagreeing)).IsEqualTo(
            "Infrastructure/Repositories/PayeeRepository.cs: "
            + "pinned 'DuplicateName, DuplicateIdentifier', "
            + "raises 'DuplicateIdentifier, DuplicateName'");
    }

    [Test]
    public async Task Census_ReportsANineteenthSiteAddedToAnAlreadyPinnedFile()
    {
        // Arrange — the other way a site arrives: not a new file, but one more arm in a file that
        // already has a row. A pin naming a set of members would be green here as long as the new arm
        // reused a member the file already raises.
        ConflictSite[] found =
        [
            new(
                "Infrastructure/Repositories/AccountRepository.cs",
                [nameof(ConflictKind.DuplicateIdentifier), nameof(ConflictKind.DuplicateIdentifier)]),
        ];

        ConflictSitePin[] pins =
        [
            new(
                "Infrastructure/Repositories/AccountRepository.cs",
                [nameof(ConflictKind.DuplicateIdentifier)],
                "decided"),
        ];

        // Act
        ConflictDispositionCensus census = ConflictDisposition.Take(found, pins);

        // Assert
        await Assert.That(string.Join(" | ", census.Disagreeing)).IsEqualTo(
            "Infrastructure/Repositories/AccountRepository.cs: "
            + "pinned 'DuplicateIdentifier', "
            + "raises 'DuplicateIdentifier, DuplicateIdentifier'");
        await Assert.That(string.Join(", ", census.Agreed)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Census_AcceptsAScanWhereEveryFileIsPinnedOnceAndAgrees()
    {
        // Arrange — without this, a census that reported every file in every bucket would satisfy all
        // six cases above while making the live assertion fire on a product nobody broke.
        ConflictSite[] found =
        [
            new("Infrastructure/Repositories/A.cs", [nameof(ConflictKind.DuplicateIdentifier)]),
            new(
                "Infrastructure/Repositories/B.cs",
                [nameof(ConflictKind.DuplicateName), nameof(ConflictKind.DuplicateIdentifier)]),
        ];

        ConflictSitePin[] pins =
        [
            new("Infrastructure/Repositories/A.cs", [nameof(ConflictKind.DuplicateIdentifier)], "decided"),
            new(
                "Infrastructure/Repositories/B.cs",
                [nameof(ConflictKind.DuplicateName), nameof(ConflictKind.DuplicateIdentifier)],
                "decided"),
        ];

        // Act
        ConflictDispositionCensus census = ConflictDisposition.Take(found, pins);

        // Assert
        await Assert.That(string.Join(", ", census.Unpinned)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.NamingNoFile)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.PinnedTwice)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(" | ", census.Disagreeing)).IsEqualTo(string.Empty);
        await Assert.That(string.Join(", ", census.Agreed))
            .IsEqualTo("Infrastructure/Repositories/A.cs, Infrastructure/Repositories/B.cs");
    }

    /// <summary>
    /// Sorts the discovered files by what the pins say about each.
    /// </summary>
    /// <remarks>
    /// Takes the discovered sites as an argument rather than reading the disk itself, which is what lets
    /// the synthetic cases above feed it arrangements this solution does not have. The live case passes
    /// the real scan, so nothing about the shipped answer is reproduced here.
    /// </remarks>
    private static class ConflictDisposition
    {
        internal static ConflictDispositionCensus Take(
            IEnumerable<ConflictSite> found,
            IEnumerable<ConflictSitePin> pins)
        {
            ArgumentNullException.ThrowIfNull(found);
            ArgumentNullException.ThrowIfNull(pins);

            ConflictSite[] sites = [.. found.OrderBy(site => site.File, StringComparer.Ordinal)];
            ConflictSitePin[] claims = [.. pins];
            ILookup<string, ConflictSitePin> byFile = claims.ToLookup(pin => pin.File, StringComparer.Ordinal);

            List<string> unpinned = [];
            List<string> pinnedTwice = [];
            List<string> disagreeing = [];
            List<string> agreed = [];

            foreach (ConflictSite site in sites)
            {
                ConflictSitePin[] claiming = [.. byFile[site.File]];
                string raises = Render(site.Raises);

                switch (claiming.Length)
                {
                    case 0:
                        unpinned.Add(site.File);
                        break;
                    case 1 when !string.Equals(Render(claiming[0].Raises), raises, StringComparison.Ordinal):
                        disagreeing.Add(
                            $"{site.File}: pinned '{Render(claiming[0].Raises)}', raises '{raises}'");
                        break;
                    case 1:
                        agreed.Add(site.File);
                        break;
                    default:
                        pinnedTwice.Add(site.File);
                        break;
                }
            }

            // Ordinal, and never a case-insensitive comparison: two paths differing only by case are two
            // files on this repository's build agents, and treating them as one would let a pin claim a
            // file it does not name.
            HashSet<string> scanned = [.. sites.Select(site => site.File)];

            List<string> namingNoFile =
            [
                .. claims
                    .Select(pin => pin.File)
                    .Where(file => !scanned.Contains(file))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];

            return new ConflictDispositionCensus(unpinned, namingNoFile, pinnedTwice, disagreeing, agreed);
        }

        private static string Render(IEnumerable<string> kinds) => string.Join(", ", kinds);
    }

    /// <summary>
    /// Finds the production source files and renders each as the conflict kinds it names.
    /// </summary>
    private static class ConflictSiteScan
    {
        private const string SolutionFileName = "BudgetoidApp.sln";

        /// <summary>
        /// Walks up from <paramref name="startDirectory" /> to the directory holding the solution, and
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
                $"No ancestor of '{startDirectory}' holds {SolutionFileName}. This census reads the "
                + "product's source from disk; it cannot run from a published output that carries no "
                + "source tree.");
        }

        /// <summary>
        /// Every file under <paramref name="root" /> that names at least one conflict kind in code, with
        /// the members it names in source order.
        /// </summary>
        internal static IReadOnlyList<ConflictSite> Of(DirectoryInfo root) =>
        [
            .. ProductionSourcesUnder(root)
                .Select(path => new ConflictSite(
                    Path.GetRelativePath(root.FullName, path).Replace(Path.DirectorySeparatorChar, '/'),
                    ConflictKindReferences.In(File.ReadAllText(path))))
                .Where(site => site.Raises.Count > 0)
                .OrderBy(site => site.File, StringComparer.Ordinal),
        ];

        /// <summary>
        /// The <c>.cs</c> files of every project that does not declare itself a test project.
        /// </summary>
        /// <remarks>
        /// Discovery is a bare recursive glob over the project files, exactly as
        /// <c>ProjectReferenceGraphTests</c> does, with build output the only exclusion — it is a copy
        /// of source already read, so counting it would report every site twice. No other filter may be
        /// added: narrowing which files are examined is how the next site slips through unnoticed.
        /// </remarks>
        internal static IReadOnlyList<string> ProductionSourcesUnder(DirectoryInfo root)
        {
            List<string> sources = [];

            foreach (string project in Directory.EnumerateFiles(
                         root.FullName, "*.csproj", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(root, project) || DeclaresTestProject(XDocument.Load(project)))
                {
                    continue;
                }

                string directory = Path.GetDirectoryName(project)
                    ?? throw new InvalidOperationException($"'{project}' has no directory.");

                sources.AddRange(
                    Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                        .Where(path => !IsBuildOutput(root, path)));
            }

            return
            [
                .. sources
                    .Select(path => path.Replace(Path.DirectorySeparatorChar, '/'))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];
        }

        /// <summary>
        /// Whether a project declares itself a test project, which is the whole of the scan's scope.
        /// </summary>
        /// <remarks>
        /// Read on <c>LocalName</c> and compared case-insensitively because MSBuild is: a project
        /// writing <c>True</c> is a test project, and a scan that read it as production would pin a
        /// suite's own synthetic references as product sites.
        /// </remarks>
        internal static bool DeclaresTestProject(XDocument project) =>
            project.Descendants().Any(element =>
                element.Name.LocalName == "IsTestProject"
                && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

        private static bool IsBuildOutput(DirectoryInfo root, string path) =>
            Path.GetRelativePath(root.FullName, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj");
    }

    /// <summary>
    /// Reads the conflict kinds one file's text names in code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes a <see cref="string" /> and never a path, which is what lets every control above prove a
    /// property of the scanner without anyone editing a production file.
    /// </para>
    /// <para>
    /// <b>Every rule here is chosen so that being wrong adds a row rather than removing one.</b> The
    /// skipped regions are the five a C# file can hold that are definitely not executable — line and
    /// block comments, ordinary strings, verbatim strings, raw strings and char literals — and an
    /// interpolated string is not among them, because its holes are code. What that costs is a member
    /// named in the literal text of an interpolated string being reported as a site; what it buys is
    /// that no member chosen inside a hole can hide. Anything the walker does not recognise is left in
    /// place for the same reason.
    /// </para>
    /// </remarks>
    private static partial class ConflictKindReferences
    {
        internal static IReadOnlyList<string> In(string source)
        {
            ArgumentNullException.ThrowIfNull(source);

            return
            [
                .. Reference()
                    .Matches(Scannable(source))
                    .Select(match => match.Groups[1].Value),
            ];
        }

        /// <summary>
        /// A member access on the enum, qualified or not.
        /// </summary>
        /// <remarks>
        /// The leading word boundary is what keeps <c>ConflictKindSpelling.Of</c> out — the type name
        /// there is followed by <c>S</c> rather than by a dot, so the pattern never starts — while
        /// <c>Domain.Common.ConflictKind.SubjectAlreadyRegistered</c> matches, because the dot before the
        /// type name is a boundary.
        /// </remarks>
        [GeneratedRegex(@"\bConflictKind\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
        private static partial Regex Reference();

        /// <summary>
        /// The same text with every definitely-non-executable region blanked out, character for
        /// character so nothing shifts.
        /// </summary>
        private static string Scannable(string source)
        {
            char[] buffer = new char[source.Length];
            Array.Fill(buffer, ' ');

            int index = 0;

            while (index < source.Length)
            {
                char current = source[index];

                if (current == '/' && Peek(source, index + 1) == '/')
                {
                    while (index < source.Length && source[index] != '\n')
                    {
                        index++;
                    }

                    continue;
                }

                if (current == '/' && Peek(source, index + 1) == '*')
                {
                    index += 2;

                    while (index + 1 < source.Length
                           && !(source[index] == '*' && source[index + 1] == '/'))
                    {
                        index++;
                    }

                    index = Math.Min(index + 2, source.Length);
                    continue;
                }

                if (current == '\'')
                {
                    index = EndOfCharLiteral(source, index);
                    continue;
                }

                if (current == '"')
                {
                    (int end, bool interpolated) = EndOfString(source, index);

                    if (interpolated)
                    {
                        // Copied verbatim rather than blanked: a hole is code, and the literal text
                        // around it is reported instead of being dropped. Adding a row is the safe
                        // direction; losing one is not.
                        source.AsSpan(index, end - index).CopyTo(buffer.AsSpan(index));
                    }

                    index = end;
                    continue;
                }

                buffer[index] = current;
                index++;
            }

            return new string(buffer);
        }

        private static char Peek(string source, int index) =>
            index < source.Length ? source[index] : '\0';

        /// <summary>
        /// The index just past a char literal beginning at <paramref name="start" />.
        /// </summary>
        private static int EndOfCharLiteral(string source, int start)
        {
            int index = start + 1;

            if (index < source.Length && source[index] == '\\')
            {
                index += 2;
            }
            else
            {
                index++;
            }

            while (index < source.Length && source[index] != '\'')
            {
                index++;
            }

            return Math.Min(index + 1, source.Length);
        }

        /// <summary>
        /// The index just past the string beginning at <paramref name="start" />, and whether it was
        /// interpolated.
        /// </summary>
        /// <remarks>
        /// The prefix is read by looking back over the <c>$</c> and <c>@</c> characters immediately
        /// before the quote, so <c>$@"</c>, <c>@$"</c> and <c>$$"""</c> are all recognised. Any
        /// <c>$</c> at all makes it interpolated: the count only sets how many braces open a hole, and
        /// this scanner does not need to know.
        /// </remarks>
        private static (int End, bool Interpolated) EndOfString(string source, int start)
        {
            int prefix = start;

            while (prefix > 0 && source[prefix - 1] is '$' or '@')
            {
                prefix--;
            }

            ReadOnlySpan<char> marks = source.AsSpan(prefix, start - prefix);
            bool interpolated = marks.Contains('$');
            bool verbatim = marks.Contains('@');

            int quotes = 0;

            while (start + quotes < source.Length && source[start + quotes] == '"')
            {
                quotes++;
            }

            if (quotes >= 3)
            {
                return (EndOfRawString(source, start + quotes, quotes), interpolated);
            }

            if (quotes == 2)
            {
                // An empty string, which cannot hold anything.
                return (start + 2, interpolated);
            }

            return verbatim
                ? (EndOfVerbatimString(source, start + 1), interpolated)
                : (EndOfRegularString(source, start + 1), interpolated);
        }

        private static int EndOfRegularString(string source, int index)
        {
            while (index < source.Length)
            {
                if (source[index] == '\\')
                {
                    index += 2;
                    continue;
                }

                if (source[index] == '"')
                {
                    return index + 1;
                }

                index++;
            }

            return source.Length;
        }

        private static int EndOfVerbatimString(string source, int index)
        {
            while (index < source.Length)
            {
                if (source[index] != '"')
                {
                    index++;
                    continue;
                }

                if (Peek(source, index + 1) == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1;
            }

            return source.Length;
        }

        private static int EndOfRawString(string source, int index, int quotes)
        {
            while (index < source.Length)
            {
                if (source[index] != '"')
                {
                    index++;
                    continue;
                }

                int run = 0;

                while (index + run < source.Length && source[index + run] == '"')
                {
                    run++;
                }

                if (run >= quotes)
                {
                    return index + run;
                }

                index += run;
            }

            return source.Length;
        }
    }
}
