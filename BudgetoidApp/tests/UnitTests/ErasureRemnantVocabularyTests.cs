using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TestSupport;

namespace UnitTests;

/// <summary>
/// Covers the vocabulary that names the columns and tables an erasure would survive in: a
/// soft-delete flag, a tombstone, a deletion record, an anonymized remnant, or an archived copy.
/// FR-026 is the clause — "erasure leaves no soft-delete flag, tombstone, deletion record,
/// anonymized remnant, or archived copy" — and the five categories are its own five nouns in its
/// own order, so a reviewer meeting a red is handed the sentence they tripped rather than a bucket
/// name somebody invented.
/// </summary>
/// <remarks>
/// <para>
/// The tests here are a pair of opposing forces, and the second kind is what makes the first mean
/// anything. <see cref="Vocabulary_MatchesAColumnNameFromEachRemnantCategory" /> shows the
/// vocabulary can still recognise something; on its own it is satisfied by a pattern far wider than
/// anyone intended. <see cref="Vocabulary_AndTheShippedSchemaShareNoName" /> closes that gap over
/// every name the model maps: a pattern widened into suffix-matching territory reaches
/// <c>sessions.revoked_at_utc</c> without anyone adding a row, and only that test says so. It is
/// deliberately named for an <i>intersection</i> rather than for a wrong vocabulary, because it is
/// bidirectional — a red there is either a pattern that has grown too wide or a real remnant that
/// landed in the schema, and the test cannot tell which. The four <c>DoesNotRefuse</c> tests are
/// the same control aimed by hand at the names that are not in the model yet, or are in it under a
/// word the deny-list has to leave alone.
/// </para>
/// <para>
/// This is a <b>sibling</b> of <see cref="ProhibitedColumnVocabulary" />, not an extension of it,
/// and the two are kept apart because they answer different questions.
/// <see cref="ProhibitedColumnCategory" /> names what the schema is keeping <i>about a person</i>;
/// a <c>deleted_at</c> says nothing about a person, it says the row is still there. Different rule,
/// different remedy — a tracking column is deleted, a soft-delete flag is replaced by an actual
/// delete — and a different owning doc, <c>docs/business-logic/erasure.md</c>. Folding the two
/// enums together would produce a classifier that cannot tell a reviewer which of the two arguments
/// they are having.
/// </para>
/// </remarks>
public sealed class ErasureRemnantVocabularyTests
{
    /// <summary>
    /// Every way a column or relation name says a row outlived its own erasure, in each of the five
    /// categories and in both the singular and the plural.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The plural rows are not decoration.</b> The matcher compares whole tokens ordinally and
    /// does not stem — <see cref="IdentifierTokens.ContainsRun" /> is string equality per token — so
    /// <c>archives</c> is a different token from <c>archive</c> and reaches no rule the singular
    /// reaches. That would be an academic gap if this vocabulary were read over columns alone, and
    /// it is not: <see cref="ErasureRemnantVocabulary" />'s own remarks say the refusal "arrives at
    /// table grain more often than at column grain", and <b>every table this schema maps is named in
    /// the plural</b> — <c>users</c>, <c>budgets</c>, <c>sessions</c>, <c>credentials</c>,
    /// <c>transactions</c>, and so on down the list. So the plural is the spelling a remnant
    /// <i>table</i> arrives in, and the singular rows below cover the shape that arrives least often.
    /// </para>
    /// <para>
    /// The rows are chosen to pin the rule and not a hand-written list of six names: what has to hold
    /// is that <b>the last token of a pattern is matched in its plural form too</b>. <c>soft_deletes</c>
    /// and <c>erasure_logs</c> are the ones that show it generalises past the bare nouns, because
    /// their patterns are phrases and only the final token pluralises. <c>deletion_records</c> is
    /// deliberately <i>not</i> here: it already matches on the <c>deletion</c> token and would pass
    /// whether the rule generalised or not, so it proves nothing.
    /// </para>
    /// <para>
    /// <c>trashes</c> is the one row that reaches the sibilant branch of
    /// <see cref="IdentifierTokens.PluralOf" />, which is what the vocabulary forms its plurals with.
    /// A bare <c>+s</c> produces the non-word <c>trashs</c> and leaves a relation actually named
    /// <c>trashes</c> — the table form the <c>trash</c> rule exists for — reaching no rule at all.
    /// </para>
    /// <para>
    /// <b><c>discarded_at</c> is refused, and it is the row most likely to be argued about.</b>
    /// <i>Discard</i> is this repository's own word for an intentional hard delete — "Recorded money
    /// movement MUST NOT be discarded as a side effect of deleting something else. It is discarded
    /// only by explicit intent" in <c>docs/business-logic/transactions.md</c> — which reads like an
    /// argument for permitting the word. It is not, because of what
    /// <see cref="ErasureRemnantVocabulary.Classify" /> is ever handed: its own remarks say it is "a
    /// classifier reading names out of a catalog or a model", and an intentional hard delete leaves
    /// <b>no column behind at all</b>. The correct behaviour the word describes can therefore never
    /// appear as an identifier, and every <c>discarded_at</c> that ever reaches this classifier is a
    /// soft delete wearing the product's own hard-delete word — which is precisely the spelling a
    /// reviewer waves through.
    /// </para>
    /// <para>
    /// <b><c>removed</c> and <c>trash</c> are refused for the same reason in two shapes.</b>
    /// <c>is_removed</c> reads more neutral than <c>deleted_at</c> and says exactly the same thing
    /// about the row, which is what makes it the spelling that lands unnoticed. <c>trash</c> bare is
    /// the table a trashed row is listed in — <c>trashed</c> alone catches the column and walks past
    /// the relation, and the relation is the axis that matters here.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("deleted_at", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("is_deleted", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("deleted_by", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("soft_delete_flag", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("soft_deletes", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("trashed_at", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("trash", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("trashes", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("trashed_items", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("restored_at", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("is_removed", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("removed_at", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("discarded_at", ErasureRemnantCategory.SoftDeleteFlag)]
    [Arguments("tombstone", ErasureRemnantCategory.Tombstone)]
    [Arguments("tombstones", ErasureRemnantCategory.Tombstone)]
    [Arguments("erased_at", ErasureRemnantCategory.Tombstone)]
    [Arguments("purged_at", ErasureRemnantCategory.Tombstone)]
    [Arguments("deletions", ErasureRemnantCategory.DeletionRecord)]
    [Arguments("deletion_reason", ErasureRemnantCategory.DeletionRecord)]
    [Arguments("erasure_log", ErasureRemnantCategory.DeletionRecord)]
    [Arguments("erasure_logs", ErasureRemnantCategory.DeletionRecord)]
    [Arguments("anonymized_email", ErasureRemnantCategory.AnonymizedRemnant)]
    [Arguments("anonymised_email", ErasureRemnantCategory.AnonymizedRemnant)]
    [Arguments("redacted_name", ErasureRemnantCategory.AnonymizedRemnant)]
    [Arguments("users_archive", ErasureRemnantCategory.ArchivedCopy)]
    [Arguments("users_archives", ErasureRemnantCategory.ArchivedCopy)]
    [Arguments("transaction_archives", ErasureRemnantCategory.ArchivedCopy)]
    [Arguments("archives", ErasureRemnantCategory.ArchivedCopy)]
    [Arguments("archive_id", ErasureRemnantCategory.ArchivedCopy)]
    public async Task Vocabulary_MatchesAColumnNameFromEachRemnantCategory(
        string identifier,
        ErasureRemnantCategory expectedCategory)
    {
        // Arrange — the argument rows above are the subject; each is a column or relation name that
        // would leave something of a person behind after an erasure, and the remnant it is.

        // Act — the rule the name trips, read for its category. The whole rule comes back so a caller
        // scanning a schema can print the argument for the refusal; this test only wants the bucket.
        ErasureRemnantCategory? category = ErasureRemnantVocabulary.Classify(identifier)?.Category;

        // Assert — the category rather than merely "not null". The five nouns are the five different
        // ways a row survives a delete, and the remedy differs per noun: a soft-delete flag is a
        // delete that never happened, a deletion record is a delete that happened and was written
        // down anyway. A classifier that swept every name into one bucket would pass a non-null check
        // while telling a reviewer nothing about which of those they are looking at.
        await Assert.That(category).IsEqualTo(expectedCategory);
    }

    [Test]
    public async Task Vocabulary_StatesAReasonForEveryRule()
    {
        // Arrange
        IReadOnlyList<ErasureRemnantRule> rules = ErasureRemnantVocabulary.Rules;

        // Act — reported by pattern rather than counted, so a failure names the entry to argue about.
        // A reason short enough to fit in a few words is a restatement of the verdict — "forbidden",
        // "not allowed" — and the threshold is what separates an argument from a label.
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
    /// No name the shipped schema carries — table or column — is a name this vocabulary refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named for the <b>intersection</b> rather than for one side being wrong, because the test reads
    /// both ways and cannot tell them apart. It is also the fastest check over this vocabulary and
    /// the only one needing no container, so it is the first red a developer sees when they add a
    /// genuine <c>deleted_at</c> — and a name or a message pointing at the pattern would send them to
    /// narrow the rule, which is the exact failure this design exists to prevent. Either reading may
    /// be the true one, and the assertion says so in the sentence the failure is read next to.
    /// </para>
    /// <para>
    /// It covers <b>tables as well as columns</b>. The vocabulary argues the relation axis is the
    /// dominant one — <c>deletions</c> and <c>users_archive</c> are tables, not columns — so a scan
    /// that read only columns would leave the more likely axis to the container-backed catalog scan
    /// alone. The offender is reported with the axis it came from, because the remedy differs: a
    /// column is dropped from a configuration, a table is not built.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Vocabulary_AndTheShippedSchemaShareNoName()
    {
        // Arrange — every table the shipped schema maps today and every column on it, read out of the
        // design-time model rather than out of a database. No container is started: this is a unit
        // test, and the model knows the names because every one is spelled out in a configuration.
        IReadOnlyList<MappedIdentifier> identifiers = MappedIdentifiers();

        // Act
        string[] offenders = identifiers
            .Select(identifier => (
                identifier.Kind,
                identifier.Name,
                Category: ErasureRemnantVocabulary.Classify(identifier.Name)?.Category))
            .Where(match => match.Category is not null)
            .Select(match => $"{match.Kind} {match.Name} ({match.Category})")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert — offenders by axis, name and category. The axis says whether a table or a column
        // was swallowed, the name says which one, and the category says which of the five nouns the
        // vocabulary thought it was looking at. A pattern widened into suffix-matching territory
        // reaches `sessions.revoked_at_utc` here without anyone adding a row.
        //
        // Both axes are asserted present before the offender list is. A helper that read no table
        // would satisfy "no offender" perfectly while covering only half of what the vocabulary is
        // read over — and the half it dropped is the one the vocabulary calls the more likely.
        await Assert.That(identifiers).IsNotEmpty();
        await Assert.That(identifiers.Any(identifier => identifier.Kind == "table")).IsTrue();
        await Assert.That(identifiers.Any(identifier => identifier.Kind == "column")).IsTrue();
        await Assert.That(offenders)
            .IsEmpty()
            .Because("the shipped schema and the remnant vocabulary intersect at "
                     + string.Join(", ", offenders)
                     + ". Exactly one of two things is true and this test cannot say which. Either "
                     + "the pattern is too wide and has swallowed an ordinary name, and the pattern "
                     + "narrows — or the table or column is a genuine remnant, in which case the "
                     + "vocabulary is right and it is the SCHEMA that has to change: the name goes, "
                     + "and with it whatever kept the row alive past its own erasure. Do not narrow "
                     + "a pattern to make a real remnant green");
    }

    /// <summary>
    /// The columns a first-party security record carries are names about access, not about a row that
    /// outlived its own deletion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Belt to the braces of <see cref="Vocabulary_AndTheShippedSchemaShareNoName" />.
    /// <c>revoked_at_utc</c> is in the schema today, so the model scan already covers it — but it
    /// covers it silently, as one name in a list nobody reads. Naming it here puts it where somebody
    /// tempted to widen <c>deleted</c> into "any past participle beside a timestamp" will look, and
    /// says in one line what that widening would cost.
    /// </para>
    /// <para>
    /// The distinction the vocabulary has to keep is that a security record legitimately names when
    /// access was revoked, consumed, expired or last used, because each of those is read in order to
    /// <i>end</i> access. None of them is a row surviving an erasure: revoking a session is the
    /// opposite of keeping one. This is why every pattern in the vocabulary is a bare noun or past
    /// participle and never a suffix rule.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("revoked_at_utc")]
    [Arguments("consumed_at")]
    [Arguments("expires_at_utc")]
    [Arguments("last_used_at")]
    [Arguments("created_at_utc")]
    public async Task Vocabulary_DoesNotRefuseTheColumnsAFirstPartySecurityRecordCarries(
        string identifier)
    {
        // Arrange — the argument rows above are the subject; each is a column name a session, a
        // stored credential or a WebAuthn challenge carries so that access can be ended.

        // Act
        ErasureRemnantRule? rule = ErasureRemnantVocabulary.Classify(identifier);

        // Assert — null, meaning the name leaves nothing behind. A rule here is a pattern that has
        // widened past the remnant it was written for and into the record access is revoked with.
        await Assert.That(rule).IsNull();
    }

    /// <summary>
    /// The names a delayed, cancellable erasure would carry are names about a schedule, not about a
    /// row kept back from a delete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// None of these names exist in the schema. Nothing schedules an erasure in this codebase — an
    /// erasure takes effect at commit — and a delayed, cancellable one is a capability the
    /// requirements describe that has not been built. If it were built, it would bring a row saying
    /// an erasure was requested, when it takes effect, and whether it was cancelled before it did.
    /// </para>
    /// <para>
    /// This control is written <b>before</b> that table exists, and that is the whole point of it.
    /// Whoever builds the schedule would otherwise meet this vocabulary as an unexplained red bar on
    /// their own work — the worst possible moment to start arguing about what a deny-list should
    /// contain, because the cheapest way out of a red on a branch is to widen or delete the rule. The
    /// sibling test above did exactly this for sessions, and was written before <c>sessions</c>
    /// existed.
    /// </para>
    /// <para>
    /// The vocabulary keeps room for it by construction: the past participle <c>erased</c> is
    /// refused while the noun <c>erasure</c> stays legal, so <c>scheduled_erasures</c> and
    /// <c>erasure_scheduled_at</c> pass and only <c>erasure_log</c> — a record kept <i>about</i> a
    /// completed deletion — does not. <c>cancelled</c> is permitted for the same reason: a
    /// cancellation belongs to the schedule, and cancelling before the erasure runs means no row was
    /// ever deleted for one to survive.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("scheduled_erasures")]
    [Arguments("erasure_scheduled_at")]
    [Arguments("takes_effect_at")]
    [Arguments("effective_at")]
    [Arguments("requested_at")]
    [Arguments("cancelled_at")]
    public async Task Vocabulary_DoesNotRefuseTheColumnsAScheduledErasureWillCarry(string identifier)
    {
        // Arrange — the argument rows above are the subject; each is a name a delayed, cancellable
        // erasure would carry if this product grew one.

        // Act
        ErasureRemnantRule? rule = ErasureRemnantVocabulary.Classify(identifier);

        // Assert — null. A rule here is this vocabulary refusing a capability the requirements ask
        // for, and it has to be argued out here rather than on the branch that builds it.
        await Assert.That(rule).IsNull();
    }

    /// <summary>
    /// The words this product uses for hiding a row it keeps on purpose are not remnant words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>archived_at</c> and <c>is_archived</c>.</b> Hiding a closed account somebody no longer
    /// uses is a plausible live-row product state, and a rule that cannot tell "this account is
    /// closed" from "this person's rows were copied aside" refuses the feature in order to catch the
    /// copy. Only the noun <c>archive</c> is refused, because a copy kept aside arrives as a place —
    /// <c>users_archive</c>. What stops an <c>archived_at</c> landing where it genuinely <i>would</i>
    /// be a remnant is a different and narrower check:
    /// <c>DataMinimizationSchemaTests.Schema_PinsTheColumnsOfTheUserRow</c> pins <c>users</c> to
    /// exactly <c>created_at_utc, email, id</c>, so nothing can be added to that row at all. <b>The
    /// narrow pin guards the row that matters; the wide vocabulary guards everything else.</b>
    /// Widening this list to cover what the pin already covers would buy nothing and cost the
    /// feature.
    /// </para>
    /// <para>
    /// The omission looks like a gap to somebody reading only the rule list, which is why it is
    /// named here rather than left to the rule's own reason. It is the one place where a live-row
    /// product state and a remnant share a word, and the division of labour above — narrow pin,
    /// wide vocabulary — is what makes keeping both possible.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("archived_at")]
    [Arguments("is_archived")]
    public async Task Vocabulary_DoesNotRefuseTheWordsTheProductUsesForHidingALiveRow(
        string identifier)
    {
        // Arrange — the argument rows above are the subject; each is a word the product already uses,
        // or would use, for something other than a row surviving its own deletion.

        // Act
        ErasureRemnantRule? rule = ErasureRemnantVocabulary.Classify(identifier);

        // Assert — null. A rule here is the deny-list having taken a word the product needs.
        await Assert.That(rule).IsNull();
    }

    /// <summary>
    /// <c>backup</c> and <c>history</c> stay permitted, because each already names something real
    /// that has nothing to do with a row outliving its erasure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other omission in this file is argued somewhere, so silence on these two would read as
    /// oversight — and the two words are close enough to the remnant idea that somebody will reach
    /// for them. Both collisions are real rather than hypothetical, which is what makes the omission
    /// an argument rather than a preference.
    /// </para>
    /// <para>
    /// <b><c>history</c>.</b> <c>__EFMigrationsHistory</c> is a relation in <c>public</c> today:
    /// <c>app-role-grants.sql</c> revokes and re-grants <c>SELECT</c> on it under that exact
    /// spelling, and <c>AppRoleGrantMatrixTests</c>, <c>RlsCoverageTests</c> and
    /// <c>SchemaConstraintSnapshotTests</c> each name it as a table their catalog scans meet. EF
    /// creates it and this repository cannot rename it, so a <c>history</c> pattern would red a
    /// relation with no available remedy — the shape of red that teaches a reader to ignore the
    /// check. It is a ledger of applied migrations, not of deleted rows.
    /// </para>
    /// <para>
    /// <b><c>backup</c>.</b> BE and BS are WebAuthn authenticator-data flags — <c>BackupEligible</c>
    /// and <c>BackedUp</c> on <c>AuthenticatorDataFlags</c>, parsed on every assertion — so
    /// <c>backup_eligible</c> and <c>backup_state</c> are the column names a credential row would
    /// carry them under. They say a passkey syncs, which is a fact about a device and not about a
    /// deleted row. What keeps them off the row that matters is the same narrow pin the
    /// <c>archived</c> argument rests on:
    /// <c>DataMinimizationSchemaTests.Schema_PinsTheColumnsOfThePasskeyPublicKeyRow</c> names
    /// <c>backup_eligible</c> outright as a column that must not arrive there. The word is load-bearing
    /// in one more place — <c>docs/business-logic/erasure.md</c> states the point-in-time backup
    /// window as erasure's one physical limit — so a rule on <c>backup</c> would refuse the vocabulary
    /// the erasure documentation itself uses.
    /// </para>
    /// </remarks>
    [Test]
    [Arguments("backup_eligible")]
    [Arguments("backup_state")]
    [Arguments("__EFMigrationsHistory")]
    public async Task Vocabulary_DoesNotRefuseTheWordsAMigrationLedgerAndAPasskeyFlagCarry(
        string identifier)
    {
        // Arrange — the argument rows above are the subject; each is a name that exists in or beside
        // this schema already, carrying a word a wider rule would have mistaken for a remnant.

        // Act
        ErasureRemnantRule? rule = ErasureRemnantVocabulary.Classify(identifier);

        // Assert — null. A rule here is a pattern that has taken a table EF owns, or the words this
        // product describes a synced passkey and its own backup window with.
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
    /// The design-time model rather than a literal list, for the reason the sibling's helper gives:
    /// a literal would agree with itself forever, staying green on the one day it matters — the day
    /// a name arrives. The model is regenerated from the configurations on every build, so a new
    /// table or column reaches this test without anyone remembering to add it here.
    /// </para>
    /// <para>
    /// The design-time model rather than <c>db.Model</c> because the runtime read-optimized model
    /// drops what only migrations consume. Nothing here opens the connection the options carry.
    /// </para>
    /// <para>
    /// Both axes, because the vocabulary is read against relation names as well as columns and says
    /// so: the refusal arrives at table grain more often than at column grain. The table name comes
    /// off the same <see cref="StoreObjectIdentifier" /> the column lookup already needs, so the
    /// relation axis costs one line and closes the axis this scan would otherwise leave entirely to
    /// the container-backed catalog test.
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
