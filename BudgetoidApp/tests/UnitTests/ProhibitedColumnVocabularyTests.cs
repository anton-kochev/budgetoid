using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace UnitTests;

/// <summary>
/// Covers the vocabulary that names the columns and tables the product refuses to carry: an
/// analytics identifier, an advertising identifier, a device fingerprint, or a record of behaviour.
/// The account row is deliberately minimal, and a tracking name arriving on <i>any</i> table is the
/// same rule breaking, so the vocabulary is written schema-wide rather than pointed at
/// <c>users</c>.
/// </summary>
/// <remarks>
/// <para>
/// The tests here are a pair of opposing forces, and the second kind is what makes the first mean
/// anything. <see cref="Vocabulary_MatchesAColumnNameFromEachForbiddenCategory" /> shows the
/// vocabulary can still recognise something; on its own it is satisfied by a pattern far wider than
/// anyone intended. <see cref="Vocabulary_AndTheShippedSchemaShareNoName" /> closes that gap over
/// every name the model maps: a lazy <c>id</c> pattern passes the first test and detonates on
/// <c>user_id</c>, and only that one says so. The two <c>DoesNotRefuse</c> tests are the same
/// control aimed by hand at names that are not in the model yet.
/// </para>
/// <para>
/// This is a <b>sibling</b> of <c>ErasureRemnantVocabulary</c> and its tests, and the shape is
/// mirrored deliberately: the two answer different questions over the same identifiers by the same
/// mechanism, so a divergence between the two test files reads as an oversight unless it is argued.
/// Two divergences are argued where they occur — the shadowing check reads the written patterns
/// rather than the executed ones, and the ordering check has no counterpart in the sibling's tests,
/// whose own subject says the ordering of its list carries no meaning.
/// </para>
/// <para>
/// The vocabulary lives in the production assembly beside <c>RowLevelSecurityCoverage</c> rather
/// than as a constant in this file, because a build gate will have to read the same list and cannot
/// reference a test assembly.
/// </para>
/// </remarks>
public sealed class ProhibitedColumnVocabularyTests
{
    /// <summary>
    /// Every kind of thing a column or relation name says the schema is keeping about a person, in
    /// each of the four categories and — where a table would carry one — in the plural.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The plural rows are not decoration, and both of the names in the subject's own remarks
    /// are here.</b> <see cref="ProhibitedColumnVocabulary" /> argues that the same list is read
    /// against relation names because "<c>user_analytics</c> and <c>device_fingerprints</c> arrive
    /// as tables far more often than as columns", and both of that pair are caught.
    /// <see cref="IdentifierTokens.ContainsRun" /> compares whole tokens ordinally and does not
    /// stem, so <c>fingerprints</c> is a different token from <c>fingerprint</c> and reaches
    /// nothing on its own; what catches it is the subject compiling every pattern twice, in the
    /// form it is written and in the plural. These rows are what holds that expansion in place —
    /// back it out and the subject's sentence stops being true with nothing else to say so.
    /// </para>
    /// <para>
    /// The rows are chosen to pin the rule rather than a list of names. What has to hold is that
    /// <b>the last token of a pattern is matched in its plural form too</b>. <c>impressions</c> and
    /// <c>device_fingerprints</c> show it for bare-token patterns; <c>page_views</c> and
    /// <c>event_logs</c> show it generalises to phrases, where only the final token pluralises —
    /// <c>page_view</c> becomes <c>page_views</c> and never <c>pages_view</c>. <c>user_analytics</c>
    /// is here for the opposite reason: the written pattern <c>analytics</c> already reaches it and
    /// no expansion is involved, so it pins the half of the subject's sentence that a change to the
    /// expansion cannot reach.
    /// </para>
    /// <para>
    /// <b><c>mac_addresses</c> is a row rather than a named gap, and this is where the sibling's
    /// shape fits after all.</b> Both vocabularies pluralise through
    /// <see cref="IdentifierTokens.PluralOf" />, which appends <c>+es</c> after a sibilant —
    /// <c>s</c>, <c>x</c>, <c>z</c>, <c>ch</c> or <c>sh</c> — and <c>+s</c> otherwise, so
    /// <c>mac_address</c> expands to <c>mac_addresses</c> and a table named for what it holds is
    /// refused by the rule written for it. That branch reaches exactly two patterns on this list:
    /// <c>mac_address</c>, and the already-plural <c>analytics</c>, whose expansion
    /// <c>analyticses</c> is a non-word. The sibling carries the case that proves the branch from
    /// its own side — <c>trash</c> ends in <c>sh</c>, and <c>trashes</c> is a row there.
    /// </para>
    /// <para>
    /// <b>The endings English inflects some other way are still not handled</b>, and nothing on
    /// either list needs them. A <c>y</c> becomes <c>ys</c> rather than <c>ies</c>, and the only
    /// <c>y</c> ending across the two vocabularies is the mass noun <c>telemetry</c>, which does
    /// not arrive in the plural. No irregular is attempted, and no pattern on either list inflects
    /// irregularly. Where the ending is wrong the result is a non-word — <c>telemetrys</c>,
    /// <c>seens</c>, <c>analyticses</c> — which matches nothing rather than something wrong, so a
    /// wrong ending costs a refusal nobody had rather than buying one nobody wanted.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("ga_client_id", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("analytics_id", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("user_analytics", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("telemetry_id", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("advertising_id", ProhibitedColumnCategory.AdvertisingIdentifier)]
    [Arguments("idfa", ProhibitedColumnCategory.AdvertisingIdentifier)]
    [Arguments("device_fingerprint", ProhibitedColumnCategory.DeviceFingerprint)]
    [Arguments("device_fingerprints", ProhibitedColumnCategory.DeviceFingerprint)]
    [Arguments("user_agent", ProhibitedColumnCategory.DeviceFingerprint)]
    [Arguments("ip_address", ProhibitedColumnCategory.DeviceFingerprint)]
    [Arguments("last_seen_at", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("event_name", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("event_logs", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("page_view_count", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("page_views", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("utm_source", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("referrer_url", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("http_referer", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("anonymous_id", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("distinct_id", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("mixpanel_id", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("last_login_at", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("login_count", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("sign_in_count", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("session_count", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("impression_count", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("impressions", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("click_count", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("cookie_id", ProhibitedColumnCategory.DeviceFingerprint)]
    [Arguments("mac_address", ProhibitedColumnCategory.DeviceFingerprint)]
    [Arguments("mac_addresses", ProhibitedColumnCategory.DeviceFingerprint)]
    public async Task Vocabulary_MatchesAColumnNameFromEachForbiddenCategory(
        string identifier,
        ProhibitedColumnCategory expectedCategory)
    {
        // Arrange — the argument rows above are the subject; each is a column or relation name that
        // has landed in somebody's schema and the bucket it belongs to.

        // Act — the rule the name trips, read for its category. The whole rule comes back so a
        // caller scanning a schema can print the argument for the refusal; this test wants the
        // bucket.
        ProhibitedColumnCategory? category = ProhibitedColumnVocabulary.Classify(identifier)?.Category;

        // Assert — the category rather than merely "not null". A vocabulary that swept every name
        // into one bucket would satisfy a non-null check while losing the ability to say what kind
        // of thing arrived, and the kind is what tells a reviewer whether the column is a mistake or
        // a product decision nobody wrote down.
        await Assert.That(category).IsEqualTo(expectedCategory);
    }

    /// <summary>
    /// Every rule carries an argument long enough to be one.
    /// </summary>
    /// <remarks>
    /// The floor is the sibling's, for the sibling's reason: a reason short enough to fit in a few
    /// words is a restatement of the verdict — "forbidden", "not allowed" — and the threshold is
    /// what separates an argument a reviewer can weigh from a label they can only obey. The
    /// vocabulary's own remarks make that claim about <see cref="ProhibitedColumnRule.Reason" />
    /// already; this is the thing that holds the next entry to it, at the moment somebody adds a
    /// pattern in a hurry.
    /// </remarks>
    [Test]
    public async Task Vocabulary_StatesAReasonForEveryRule()
    {
        // Arrange
        IReadOnlyList<ProhibitedColumnRule> rules = ProhibitedColumnVocabulary.Rules;

        // Act — reported by pattern rather than counted, so a failure names the entry to argue about.
        string[] unexplained = rules
            .Where(rule => string.IsNullOrWhiteSpace(rule.Reason) || rule.Reason.Length < 40)
            .Select(rule => rule.Pattern)
            .ToArray();

        // Assert — the non-empty check first: an empty vocabulary has no unexplained entry either,
        // and would pass the real assertion with nothing in it. A rule whose reason is blank is one
        // nobody argued for, and the next person to hit a false positive has nothing to weigh.
        await Assert.That(rules).IsNotEmpty();
        await Assert.That(unexplained).IsEmpty();
    }

    /// <summary>
    /// No pattern's tokens are a contiguous run inside another pattern's, so no rule can shadow a
    /// rule listed after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half of the vocabulary's disjointness claim that is actually true, and it is
    /// worth pinning because it is the half a future entry breaks. The bare token <c>event</c> is
    /// the obvious candidate: it would swallow <c>event_name</c>, <c>event_count</c>,
    /// <c>event_log</c> and <c>user_event</c>, collapsing four arguments into one and making the
    /// order of <see cref="ProhibitedColumnVocabulary.Rules" /> decide which reason a reviewer is
    /// handed. The subject's own remarks already refuse that token by name; this is what makes the
    /// refusal fail rather than persuade.
    /// </para>
    /// <para>
    /// <b>It reads the written patterns rather than the compiled ones</b>, which is where it
    /// diverges from the claim the sibling's subject makes about its own list. The compiled list is
    /// private and holds twice the entries the written one does, each pattern also in the plural;
    /// the written list is the one a reviewer edits, so it is the one a red has a remedy on. The
    /// expansion built from these patterns has to preserve the property this test pins rather than
    /// be exempted from it.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Vocabulary_HasNoPatternThatShadowsAnother()
    {
        // Arrange — every pattern tokenized the way Classify tokenizes it, so the comparison is the
        // one the matcher will actually make rather than a comparison of the written strings.
        (string Pattern, string[] Tokens)[] patterns = ProhibitedColumnVocabulary.Rules
            .Select(rule => (rule.Pattern, Tokens: IdentifierTokens.Tokenize(rule.Pattern)))
            .ToArray();

        // Act — every ordered pair, because shadowing has a direction: the shorter run swallows the
        // longer one and never the other way round.
        string[] shadowed = patterns
            .SelectMany(outer => patterns
                .Where(inner => !ReferenceEquals(outer.Tokens, inner.Tokens)
                                && IdentifierTokens.ContainsRun(inner.Tokens, outer.Tokens))
                .Select(inner => $"{outer.Pattern} shadows {inner.Pattern}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — the pairs by name. A count would say a rule is unreachable; the pair says which
        // argument was swallowed by which, and that is the one the new pattern has to answer.
        await Assert.That(patterns).IsNotEmpty();
        await Assert.That(shadowed).IsEmpty();
    }

    /// <summary>
    /// A name carrying two vocabularies at once is answered by the rule listed first, deterministically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The subject states this property outright</b> — "the first matching rule wins, and the
    /// order of <c>Rules</c> is what decides it" — and nothing else in the file holds it, because
    /// it does not follow from disjointness. Disjoint patterns cannot shadow each other —
    /// <see cref="Vocabulary_HasNoPatternThatShadowsAnother" /> holds — but a single name can carry
    /// two of them side by side, and then the order decides. <c>analytics_event_log</c> is the
    /// counterexample across categories: it matches <c>analytics</c> and <c>event_log</c>, and
    /// reordering the list would change an AnalyticsIdentifier verdict into a BehaviouralEvent one.
    /// </para>
    /// <para>
    /// <c>user_event_log</c> is the sharper row, because both rules it reaches sit in the same
    /// category: which of the two <i>reasons</i> a reviewer is handed is invisible to anything that
    /// only reports a bucket. That is the whole argument for
    /// <see cref="ProhibitedColumnVocabulary.Classify" /> answering with the rule — the ordering
    /// claim is unfalsifiable until the answer says which rule won.
    /// </para>
    /// <para>
    /// The winner is computed here as well as named, so this is not a restatement of the classifier
    /// against itself: the expected rule is the first pattern in written order whose tokens appear
    /// in the name, worked out from <see cref="IdentifierTokens" /> directly. The named winner
    /// beside it is what keeps the test honest if the compiled list ever stops answering in the
    /// written list's order.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("analytics_event_log", "analytics")]
    [Arguments("tracking_page_view", "tracking")]
    [Arguments("user_event_log", "user_event")]
    [Arguments("telemetry_ip", "telemetry")]
    public async Task Vocabulary_AnswersANameMatchingTwoRulesWithTheFirstOneListed(
        string identifier,
        string expectedWinningPattern)
    {
        // Arrange — the rules this name reaches, in the order they are written down.
        string[] tokens = IdentifierTokens.Tokenize(identifier);
        ProhibitedColumnRule[] reached = ProhibitedColumnVocabulary.Rules
            .Where(rule => IdentifierTokens.ContainsRun(tokens, IdentifierTokens.Tokenize(rule.Pattern)))
            .ToArray();

        // Act
        ProhibitedColumnRule? answer = ProhibitedColumnVocabulary.Classify(identifier);

        // Assert — that the name reaches more than one rule at all comes first: without it the
        // ordering assertion below is satisfied by a name with a single match, which proves nothing
        // about determinism. Then the answer is the first rule listed, both as computed and as
        // named, so a reviewer meeting this red is told which of two arguments the list handed them.
        await Assert.That(reached.Length).IsGreaterThan(1);
        await Assert.That(answer).IsNotNull();
        await Assert.That(answer!.Pattern).IsEqualTo(reached[0].Pattern);
        await Assert.That(answer.Pattern).IsEqualTo(expectedWinningPattern);
    }

    /// <summary>
    /// A name the model or the catalog does not have is answered with nothing rather than an
    /// exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The parameter is nullable because its callers' inputs are.</b>
    /// <c>IEntityType.GetTableName()</c> and <c>IProperty.GetColumnName()</c> both answer
    /// <see langword="null" /> for an entity or a property the model maps to no table or column, so
    /// a non-null parameter leaves every model-reading caller either reaching for <c>!</c> —
    /// asserting something the model does not promise — or dropping the name before the classifier
    /// sees it. Either way the blank guard inside <see cref="ProhibitedColumnVocabulary.Classify" />
    /// could not fire, which would make the fail-open argument its own remarks give for that guard a
    /// claim about unreachable code.
    /// </para>
    /// <para>
    /// Fail-open is the right direction here and only here: this is a classifier reading names out
    /// of a catalog or a model, not a validator of its caller's arguments, and a scan that threw
    /// partway through would report <i>fewer</i> offenders than exist.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Vocabulary_AnswersNothingForANameTheModelDoesNotHave(string? identifier)
    {
        // Arrange — the argument rows above are the subject; each is what a model read hands a
        // caller for something mapped to no table or column at all.

        // Act
        ProhibitedColumnRule? rule = ProhibitedColumnVocabulary.Classify(identifier);

        // Assert — null, and no exception. A throw here is a scan that stops at the first unmapped
        // entity and reports a clean schema for the rest of it.
        await Assert.That(rule).IsNull();
    }

    /// <summary>
    /// No name the shipped schema carries — table or column — is a name this vocabulary refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named for the <b>intersection</b> rather than for one side being wrong, because the test
    /// reads both ways and cannot tell them apart. It is also the fastest check over this vocabulary
    /// and the only one needing no container, so it is the first red a developer sees when they add
    /// a genuine <c>last_seen_at</c> — and a name or a message pointing at the pattern would send
    /// them to narrow the rule, which is the exact failure this design exists to prevent. Either
    /// reading may be the true one, and the assertion says so in the sentence the failure is read
    /// next to.
    /// </para>
    /// <para>
    /// It covers <b>tables as well as columns</b>. The vocabulary argues the relation axis is where
    /// a behavioural feature is actually modelled — <c>user_analytics</c> and
    /// <c>device_fingerprints</c> arrive as tables far more often than as columns — so a scan that
    /// read only columns would leave the axis the subject calls the more likely to the
    /// container-backed catalog scan alone. The offender is reported with the axis it came from,
    /// because the remedy differs: a column is dropped from a configuration, a table is not built.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Vocabulary_AndTheShippedSchemaShareNoName()
    {
        // Arrange — every table the shipped schema maps today and every column on it, read out of
        // the design-time model rather than out of a database. No container is started: this is a
        // unit test, and the model knows the names because every one is spelled out in a
        // configuration.
        IReadOnlyList<MappedIdentifier> identifiers = MappedIdentifiers();

        // Act
        string[] offenders = identifiers
            .Select(identifier => (
                identifier.Kind,
                identifier.Name,
                Category: ProhibitedColumnVocabulary.Classify(identifier.Name)?.Category))
            .Where(match => match.Category is not null)
            .Select(match => $"{match.Kind} {match.Name} ({match.Category})")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — offenders by axis, name and category. The axis says whether a table or a column
        // was swallowed, the name says which one, and the category says which of the four refusals
        // the vocabulary thought it was looking at. A lazy `id` pattern reaches `users.user_id` here
        // without anyone adding a row.
        //
        // Both axes are asserted present before the offender list is. A helper that read no table
        // would satisfy "no offender" perfectly while covering only half of what the vocabulary is
        // read over — and the half it dropped is the one the vocabulary calls the more likely.
        await Assert.That(identifiers).IsNotEmpty();
        await Assert.That(identifiers.Any(identifier => identifier.Kind == "table")).IsTrue();
        await Assert.That(identifiers.Any(identifier => identifier.Kind == "column")).IsTrue();
        await Assert.That(offenders)
            .IsEmpty()
            .Because("the shipped schema and the prohibited-column vocabulary intersect at "
                     + string.Join(", ", offenders)
                     + ". Exactly one of two things is true and this test cannot say which. Either "
                     + "the pattern is too wide and has swallowed an ordinary name, and the pattern "
                     + "narrows — or the table or column really does keep an analytics identifier, "
                     + "an advertising identifier, a device fingerprint or a record of behaviour, in "
                     + "which case the vocabulary is right and it is the SCHEMA that has to change: "
                     + "the column is dropped, or the table is not built. Do not narrow a pattern to "
                     + "make a real tracking column green");
    }

    /// <summary>
    /// The columns a transactional outbox carries are names about a message, not about a person.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outbox and the domain events feeding it are the expected pattern for cross-aggregate work
    /// in this solution, so these names are not hypothetical — they are scheduled. <c>event_type</c>
    /// holds a serialized .NET type name and <c>event_data</c> the payload of a domain event; neither
    /// records what a person did, and the outbox exists to make a write and its consequences one
    /// transaction rather than to watch anybody.
    /// </para>
    /// <para>
    /// What this protects is the credibility of the check itself. The vocabulary's own remarks argue
    /// that it is only worth having while its reds are believed, and a red bar on work the
    /// architecture already calls for is spent credibility: the reviewer who meets it learns that the
    /// list over-refuses, and the next real offender is waved through by the same reflex. The bare
    /// token <c>event</c> is the pattern that buys a refusal it cannot justify.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("event_type")]
    [Arguments("event_id")]
    [Arguments("event_data")]
    [Arguments("event_payload")]
    [Arguments("event_version")]
    [Arguments("occurred_at")]
    [Arguments("processed_at")]
    public async Task Vocabulary_DoesNotRefuseTheColumnsAnOutboxCarries(string identifier)
    {
        // Arrange — the argument rows above are the subject; each is a column name a canonical
        // outbox_messages table carries.

        // Act
        ProhibitedColumnRule? rule = ProhibitedColumnVocabulary.Classify(identifier);

        // Assert — null, meaning the product is happy to carry the name. A rule here names the
        // pattern that has to narrow, and its reason is the argument that has to be answered.
        await Assert.That(rule).IsNull();
    }

    /// <summary>
    /// The columns a first-party security record carries are names about access, not about behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None of these columns exist in the schema yet. They are the names revocable sessions, passkey
    /// credentials and recovery codes will bring, and this test is the only thing standing between the
    /// deny-list and a red bar on a story already marked <c>Ready</c> — the pattern that refuses them
    /// would be written before the table that needs them, and nothing else in the suite would notice.
    /// </para>
    /// <para>
    /// The distinction the vocabulary has to keep is this: a first-party security record may
    /// legitimately name the session or credential itself, when it began, when it expires or was
    /// revoked, and when it was last used, because each of those is read in order to <i>end</i>
    /// access. A person cannot revoke a session the schema is forbidden to name, and cannot be shown
    /// what to revoke without the moment it was last used. That is the opposite of a usage log kept
    /// about them, so the tracking patterns for login-, session- and cookie-shaped columns have to be
    /// phrases naming the third-party idea — never the bare tokens <c>session</c>, <c>login</c> or
    /// <c>cookie</c>, which refuse the mechanism that protects the account.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("session_id")]
    [Arguments("sessions")]
    [Arguments("token_hash")]
    [Arguments("expires_at_utc")]
    [Arguments("revoked_at_utc")]
    [Arguments("last_used_at")]
    [Arguments("credential_id")]
    [Arguments("public_key")]
    [Arguments("sign_count")]
    [Arguments("aaguid")]
    [Arguments("transports")]
    [Arguments("device_name")]
    [Arguments("cookie_hash")]
    [Arguments("login_at")]
    [Arguments("code_hash")]
    [Arguments("consumed_at")]
    public async Task Vocabulary_DoesNotRefuseTheColumnsAFirstPartySecurityRecordCarries(
        string identifier)
    {
        // Arrange — the argument rows above are the subject; each is a column name a revocable
        // session, a stored passkey credential or a recovery code will carry.

        // Act
        ProhibitedColumnRule? rule = ProhibitedColumnVocabulary.Classify(identifier);

        // Assert — null. A rule here is a pattern that has widened past the third-party tracker it
        // was written for and into the record a person revokes access with.
        await Assert.That(rule).IsNull();
    }

    /// <summary>One name the EF model maps, and whether it is a table or a column.</summary>
    /// <remarks>
    /// The axis travels with the name because it decides the remedy a failure asks for: a column is
    /// dropped from an entity configuration, a table is not built at all. An offender list of bare
    /// names would make the reader work that out from the name, which is exactly the moment the
    /// design wants to be unambiguous.
    /// </remarks>
    /// <param name="Kind">The axis the name sits on — <c>table</c> or <c>column</c>.</param>
    /// <param name="Name">The mapped identifier, spelled as the database stores it.</param>
    private sealed record MappedIdentifier(string Kind, string Name);

    /// <summary>
    /// The table and column names the EF model maps today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The design-time model rather than a literal list: a literal would agree with itself forever,
    /// staying green on the one day it matters — the day a name arrives. The model is regenerated
    /// from the configurations on every build, so a new table or column reaches this test without
    /// anyone remembering to add it here.
    /// </para>
    /// <para>
    /// The design-time model rather than <c>db.Model</c> for the reason the neighbouring model tests
    /// give: the runtime read-optimized model drops what only migrations consume. Nothing here opens
    /// the connection the options carry.
    /// </para>
    /// <para>
    /// Both axes, because the vocabulary is read against relation names as well as columns and says
    /// so: a table named for what it holds is the identical refusal. The table name comes off the
    /// entity the column lookup already needs, so the relation axis costs one line and closes the
    /// axis this scan would otherwise leave entirely to the container-backed catalog test.
    /// </para>
    /// <para>
    /// This reaches only what EF maps. The integration-level catalog scan is what covers the rest of
    /// the schema — a view, a partitioned parent, or anything created outside the model.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<MappedIdentifier> MappedIdentifiers()
    {
        DbContextOptions<BudgetoidDbContext> options =
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql("Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres")
                .Options;

        using BudgetoidDbContext db = new(options);
        IModel model = db.GetService<IDesignTimeModel>().Model;
        List<MappedIdentifier> identifiers = [];

        foreach (IEntityType entity in model.GetEntityTypes())
        {
            StoreObjectIdentifier? table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);

            if (table is null)
            {
                continue;
            }

            string? tableName = entity.GetTableName();

            if (tableName is not null)
            {
                identifiers.Add(new MappedIdentifier("table", tableName));
            }

            foreach (IProperty property in entity.GetProperties())
            {
                string? column = property.GetColumnName(table.Value);

                if (column is not null)
                {
                    identifiers.Add(new MappedIdentifier("column", column));
                }
            }
        }

        return identifiers;
    }
}
