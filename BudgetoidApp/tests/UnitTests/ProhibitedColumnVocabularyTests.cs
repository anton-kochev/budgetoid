using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace UnitTests;

/// <summary>
/// Covers the vocabulary that names the columns the product refuses to carry: an analytics
/// identifier, an advertising identifier, a device fingerprint, or a record of behaviour. The
/// account row is deliberately minimal, and a tracking column arriving on <i>any</i> table is the
/// same rule breaking, so the vocabulary is written schema-wide rather than pointed at
/// <c>users</c>.
/// </summary>
/// <remarks>
/// <para>
/// The two tests are a pair, and the second is what makes the first mean anything.
/// <see cref="Vocabulary_MatchesAColumnNameFromEachForbiddenCategory" /> shows the vocabulary can
/// still recognise something; on its own it is satisfied by a pattern far wider than anyone
/// intended. <see cref="Vocabulary_MatchesNoneOfTheColumnsTheSchemaCarries" /> is the
/// false-positive control that closes that gap: a lazy <c>id</c> pattern passes the first test and
/// detonates on <c>user_id</c>, and only the second one says so.
/// </para>
/// <para>
/// The vocabulary lives in the production assembly beside <c>RowLevelSecurityCoverage</c> rather
/// than as a constant in this file, because a build gate will have to read the same list and cannot
/// reference a test assembly.
/// </para>
/// </remarks>
public sealed class ProhibitedColumnVocabularyTests
{
    [Test]
    [Arguments("ga_client_id", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("analytics_id", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("telemetry_id", ProhibitedColumnCategory.AnalyticsIdentifier)]
    [Arguments("advertising_id", ProhibitedColumnCategory.AdvertisingIdentifier)]
    [Arguments("idfa", ProhibitedColumnCategory.AdvertisingIdentifier)]
    [Arguments("device_fingerprint", ProhibitedColumnCategory.DeviceFingerprint)]
    [Arguments("user_agent", ProhibitedColumnCategory.DeviceFingerprint)]
    [Arguments("ip_address", ProhibitedColumnCategory.DeviceFingerprint)]
    [Arguments("last_seen_at", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("event_name", ProhibitedColumnCategory.BehaviouralEvent)]
    [Arguments("page_view_count", ProhibitedColumnCategory.BehaviouralEvent)]
    public async Task Vocabulary_MatchesAColumnNameFromEachForbiddenCategory(
        string columnName,
        ProhibitedColumnCategory expectedCategory)
    {
        // Arrange — the argument rows above are the subject; each is a column name that has landed in
        // somebody's schema and the bucket it belongs to.

        // Act
        ProhibitedColumnCategory? category = ProhibitedColumnVocabulary.Classify(columnName);

        // Assert — the category rather than merely "not null". A vocabulary that swept every name
        // into one bucket would satisfy a non-null check while losing the ability to say what kind
        // of thing arrived, and the kind is what tells a reviewer whether the column is a mistake or
        // a product decision nobody wrote down.
        await Assert.That(category).IsEqualTo(expectedCategory);
    }

    [Test]
    public async Task Vocabulary_StatesAReasonForEveryRule()
    {
        // Arrange
        IReadOnlyList<ProhibitedColumnRule> rules = ProhibitedColumnVocabulary.Rules;

        // Act — reported by pattern rather than counted, so a failure names the entry to argue about.
        string[] unexplained = rules
            .Where(rule => string.IsNullOrWhiteSpace(rule.Reason))
            .Select(rule => rule.Pattern)
            .ToArray();

        // Assert — the non-empty check first: an empty vocabulary has no unexplained entry either,
        // and would pass the real assertion with nothing in it. A rule whose reason is blank is one
        // nobody argued for, and the next person to hit a false positive has nothing to weigh.
        await Assert.That(rules).IsNotEmpty();
        await Assert.That(unexplained).IsEmpty();
    }

    [Test]
    public async Task Vocabulary_MatchesNoneOfTheColumnsTheSchemaCarries()
    {
        // Arrange — every column the shipped schema maps today, read out of the design-time model
        // rather than out of a database. No container is started: this is a unit test, and the model
        // knows the column names because every one of them is spelled out in an entity configuration.
        IReadOnlyList<string> columns = MappedColumnNames();

        // Act
        string[] offenders = columns
            .Select(column => (Column: column, Category: ProhibitedColumnVocabulary.Classify(column)))
            .Where(match => match.Category is not null)
            .Select(match => $"{match.Column} ({match.Category})")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — offenders by name, because the failure this control exists to catch is a pattern
        // that is too wide, and the only useful thing it can say is which real column it swallowed.
        await Assert.That(columns).IsNotEmpty();
        await Assert.That(offenders).IsEmpty();
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
    public async Task Vocabulary_DoesNotRefuseTheColumnsAnOutboxCarries(string columnName)
    {
        // Arrange — the argument rows above are the subject; each is a column name a canonical
        // outbox_messages table carries.

        // Act
        ProhibitedColumnCategory? category = ProhibitedColumnVocabulary.Classify(columnName);

        // Assert — null, meaning the product is happy to carry the name. A category here names the
        // pattern that has to narrow.
        await Assert.That(category).IsNull();
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
        string columnName)
    {
        // Arrange — the argument rows above are the subject; each is a column name a revocable
        // session, a stored passkey credential or a recovery code will carry.

        // Act
        ProhibitedColumnCategory? category = ProhibitedColumnVocabulary.Classify(columnName);

        // Assert — null. A category here is a pattern that has widened past the third-party tracker it
        // was written for and into the record a person revokes access with.
        await Assert.That(category).IsNull();
    }

    /// <summary>
    /// The column names the EF model maps to tables today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The design-time model rather than a literal list: a literal would agree with itself forever,
    /// staying green on the one day it matters — the day a column arrives. The model is regenerated
    /// from the configurations on every build, so a new column reaches this test without anyone
    /// remembering to add it here.
    /// </para>
    /// <para>
    /// The design-time model rather than <c>db.Model</c> for the reason the neighbouring model tests
    /// give: the runtime read-optimized model drops what only migrations consume. Nothing here opens
    /// the connection the options carry.
    /// </para>
    /// <para>
    /// This reaches only what EF maps. The integration-level scan is what covers the rest of the
    /// schema — a view, a partitioned parent, or anything created outside the model.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> MappedColumnNames()
    {
        DbContextOptions<BudgetoidDbContext> options =
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql("Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres")
                .Options;

        using BudgetoidDbContext db = new(options);
        IModel model = db.GetService<IDesignTimeModel>().Model;
        List<string> columns = [];

        foreach (IEntityType entity in model.GetEntityTypes())
        {
            StoreObjectIdentifier? table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);

            if (table is null)
            {
                continue;
            }

            foreach (IProperty property in entity.GetProperties())
            {
                string? column = property.GetColumnName(table.Value);

                if (column is not null)
                {
                    columns.Add(column);
                }
            }
        }

        return columns;
    }
}
