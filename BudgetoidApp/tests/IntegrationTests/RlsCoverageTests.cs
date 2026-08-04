using Infrastructure.Persistence.Provisioning;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the one thing no isolation test can: that <b>every</b> table in the schema is accounted
/// for — policed by the policy its ownership requires, or exempt for a reason someone wrote down.
/// The grant matrix and row-level security fail in opposite directions, and that asymmetry is the
/// whole reason this file exists. A new table nobody grants is simply invisible to the application
/// role — fail-closed, and the first feature that touches it fails loudly with <c>42501</c>. A new
/// table nobody writes a policy for is fully readable and writable by the role across every tenant
/// — fail-open, silent, and indistinguishable from working. So an owned table needs both, and only
/// a test that derives its subject from the live schema can notice the second was forgotten.
/// </summary>
/// <remarks>
/// <para>
/// There are now <b>two</b> kinds of ownership, and a table owes the policy that matches its own.
/// A table carrying <c>budget_id</c> is budget-owned and owes <c>budget_isolation</c>; a table
/// carrying <c>user_id</c> — or the <c>users</c> row that <i>is</i> the user — is user-owned and
/// owes <c>user_isolation</c>. Budget-owned is checked first and deliberately outranks user-owned:
/// a budget belongs to exactly one user, so <c>budget_id = current_budget</c> is strictly narrower
/// than <c>user_id = current_user</c>, and a table that grew both columns has to be protected by
/// the narrower rule rather than by whichever test happened to run first.
/// </para>
/// <para>
/// The subject is <b>discovered</b> and never written down: every ordinary table in <c>public</c>.
/// What <i>is</i> written down is <see cref="RowLevelSecurityCoverage.Exemptions" /> — the tables
/// that need no policy, each carrying the reason it belongs to no tenant and the ownership it is
/// exempt <b>despite</b>. Everything discovery finds and that list does not name must be policed.
/// </para>
/// <para>
/// <b>A list of exceptions is the opposite of the hardcoded list this file used to warn against,
/// and the difference is which way each one fails.</b> A list of the tables that <i>are</i> policed
/// fails open: add the next table and the list still describes the old ones, so the test keeps
/// passing on the only day it matters. A list of the tables that are <i>exempt</i> fails closed:
/// add the next table and no exemption names it, so it is required to have a policy — or to be
/// classifiable at all — and the suite goes red until someone either writes the policy or writes
/// down why none is needed. Both are short lists of names; only one of them can notice a new table.
/// Do not "simplify" this back into a list of policed tables, and do not put a filter back into
/// discovery — either change reopens the hole.
/// </para>
/// <para>
/// Filtering discovery is exactly how this test lost <c>credentials</c>. Subjects used to be found
/// by looking for a <c>budget_id</c> column, which silently exempted every table without one — no
/// decision, no record, just absence. That is the fail-open half of the asymmetry above, reproduced
/// inside the test written to catch it, and server-side sessions, passkey public keys and wrapped
/// encryption keys would each have landed in the same blind spot — which is precisely why the
/// user-owned bucket exists rather than a second column filter.
/// <see cref="Classification_WithNoExemptions_LeavesEveryExemptTableUnexcused" /> is the guard
/// against it coming back: it classifies the same live schema against an <b>empty</b> exemption set
/// and demands every otherwise-exempt table come back unexcused, which they can only do while
/// discovery still reaches tables carrying neither ownership column.
/// </para>
/// <para>
/// Both ownership columns still appear below, in the opposite role to a filter: as facts read about
/// a table rather than conditions applied to one. Every exemption's reason amounts to "this table
/// is not policed the way its shape suggests", so an exempt table whose ownership has changed under
/// it has outlived the reason attached to it, and someone has to reconsider it rather than inherit
/// it.
/// </para>
/// <para>
/// <b>A table that carries neither ownership column is a failure, not a default.</b> This file used
/// to argue there were only two buckets and that a table nobody had thought about belonged in
/// "needs a policy" — correct while exactly one policy name existed, because there was only one
/// policy such a table could possibly owe. With two names there is no longer an answer to derive:
/// a table owning nothing cannot be told whether it owes <c>budget_isolation</c> or
/// <c>user_isolation</c>, and picking one for it would be guessing in a file whose entire job is to
/// refuse to guess. So the third bucket is <b>unclassifiable</b>, and it is red. The fail-closed
/// property is unchanged — an unclassified table still stops the suite until a human decides
/// between writing it an ownership column, a policy, or an exemption.
/// </para>
/// <para>
/// Discovery, classification and the exemption list live in production code
/// (<see cref="RowLevelSecurityCoverage" />) rather than here, because the deploy-time verifier
/// reads the same rule. Two <i>executed</i> lists that disagree have no adjudicator, and the loser
/// fails open — which is the failure mode this whole file is about, one layer up.
/// </para>
/// <para>
/// Every test here runs on the superuser connection. That is not a privilege question:
/// <c>pg_class</c> and <c>pg_policies</c> describe the schema, and the schema is the same whoever
/// reads it.
/// </para>
/// <para>
/// A discovery query that silently stopped matching anything would make these assertions pass
/// vacuously, so each test asserts the list it is about is non-empty before it asserts anything
/// about that list's contents.
/// </para>
/// </remarks>
public sealed class RlsCoverageTests
{
    /// <summary>
    /// Names of the throwaway tables the two classification probes create. Constants rather than
    /// literals repeated at the creation and the lookup, because the two spellings drifting apart
    /// would not fail — the probe would simply never be found, and an assertion looking for a table
    /// that was never created is indistinguishable from one the classifier dropped.
    /// </summary>
    private const string UserOwnedProbeTable = "rls_probe_user_owned";

    private const string UnownedProbeTable = "rls_probe_unowned";

    [Test]
    public async Task Database_EnablesRowLevelSecurityOnEveryTableNeedingAPolicy()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — relrowsecurity is read alongside the discovery, because "which tables are there"
        // and "is each one protected" are one row of pg_class and splitting them would only invite
        // the two lists to drift. This stays a separate assertion from the policy check below
        // rather than folding into it: a policy on a table whose relrowsecurity is off is inert —
        // PostgreSQL keeps the definition and enforces nothing — so a table can be fully policed on
        // paper and completely open in practice.
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> unprotected = schema.NeedingAPolicy
            .Where(classified => !classified.Table.RowSecurityEnabled)
            .Select(classified => classified.Table.Name)
            .ToList();

        // Assert — the non-empty check first: a discovery query that silently stopped matching
        // anything would make the real assertion below pass with nothing in it.
        await Assert.That(schema.NeedingAPolicy).IsNotEmpty();
        await Assert.That(unprotected).IsEmpty();
    }

    [Test]
    public async Task Database_GivesEveryTableNeedingAPolicyThePolicyItsOwnershipRequires()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — exactly one, not at least one. These isolation policies are permissive, and
        // permissive policies OR together, so a second permissive one can only widen what the first
        // allows; anything meant to narrow has to be written AS RESTRICTIVE. A table that grew a
        // stray policy has quietly stopped meaning what the first policy says.
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> wrongly = [];
        foreach (ClassifiedTable classified in schema.NeedingAPolicy)
        {
            DiscoveredTable table = classified.Table;
            if (table.Policies is not [TablePolicy policy])
            {
                wrongly.Add($"{table.Name}: {table.Policies.Count} policies, wanted 1");
                continue;
            }

            // The name, and not only the count — and now the name the table's own ownership
            // requires rather than a single global one. A single policy called something else
            // passes the count check and can pass the role check while doing anything at all to the
            // rows; the count says a rule exists, the name says which rule, and only the named one
            // has been read by anyone here. Naming the wrong isolation is the sharper version of
            // that failure: user_isolation on a budget-owned table is a real policy, enforced, and
            // wider than the tenancy the table is supposed to have.
            if (!string.Equals(policy.Name, classified.RequiredPolicyName, StringComparison.Ordinal))
            {
                wrongly.Add(
                    $"{table.Name}: policy is named '{policy.Name}', wanted "
                    + $"'{classified.RequiredPolicyName}'");
            }

            // The role check is deliberately "binds the application role" rather than "names
            // budgetoid_app and nothing else". A policy written TO PUBLIC also binds the app role —
            // it binds every non-owner role — so it is broader, not weaker, and failing it would be
            // the assertion being brittle about spelling rather than about protection. Anything
            // else means the app role is unpoliced, which is the whole failure this test is for.
            if (!policy.Roles.Contains(DatabaseProvisioning.AppRoleName)
                && !policy.Roles.Contains("public"))
            {
                wrongly.Add(
                    $"{table.Name}: policy '{policy.Name}' binds " +
                    $"[{string.Join(", ", policy.Roles)}], " +
                    $"which does not include {DatabaseProvisioning.AppRoleName}");
            }
        }

        // Assert
        await Assert.That(schema.NeedingAPolicy).IsNotEmpty();
        await Assert.That(wrongly).IsEmpty();
    }

    [Test]
    public async Task Database_LeavesNoTableUnclassified()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — the third bucket, asserted empty against the schema we actually ship. Every table
        // here is either owned by a budget, owned by a user, or written down as owned by nobody;
        // anything else is a table whose tenancy nobody has decided, and the classifier refuses to
        // decide it on their behalf.
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> undecided = schema.Unclassifiable.Select(table => table.Name).ToList();

        // Assert — the usual non-empty guard, on the list that proves discovery ran at all rather
        // than on the list being asserted: "unclassifiable is empty" is trivially true against a
        // discovery that found nothing.
        await Assert.That(schema.NeedingAPolicy).IsNotEmpty();
        await Assert.That(undecided).IsEmpty();
    }

    [Test]
    public async Task Classification_TreatsANewTableCarryingUserId_AsNeedingTheUserIsolationPolicy()
    {
        // Arrange — no table in the shipped schema can demonstrate this: every user-owned table
        // today is either already policed or deliberately exempt, so the rule that catches the
        // *next* one has nothing live to prove it. A throwaway table supplies the missing subject.
        // Its own host, and therefore its own container, is the point rather than overhead — the
        // probe would otherwise appear in every other test's discovery as an unpoliced user-owned
        // table and turn this file red for a reason that is not about the schema.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await CreateProbeTableAsync(admin, UserOwnedProbeTable, "user_id uuid not null");

        // Act — the same classifier, the same real exemption list, one extra table. Nothing about
        // the probe is special-cased; it is classified by exactly the code a real migration's
        // output will be. The required name is read through a null-conditional rather than pulled
        // off a local the compiler has been told to trust: an assertion cannot narrow nullability,
        // so the alternative is a null-forgiving operator asserting the very thing under test.
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        string? requiredOfProbe = schema.NeedingAPolicy
            .SingleOrDefault(classified => string.Equals(
                classified.Table.Name, UserOwnedProbeTable, StringComparison.Ordinal))
            ?.RequiredPolicyName;

        // Assert — it needs a policy, and specifically the user-keyed one. One assertion covers
        // both halves: a probe that never reached NeedingAPolicy leaves this null, which is not the
        // expected name either. Asserting only that it needs *a* policy would pass just as well if
        // the classifier demanded budget_isolation of it, which is a policy the table cannot
        // satisfy and a demand nobody could act on.
        await Assert.That(schema.NeedingAPolicy).IsNotEmpty();
        await Assert.That(requiredOfProbe)
            .IsEqualTo(RowLevelSecurityCoverage.UserIsolationPolicyName);
    }

    [Test]
    public async Task Classification_TreatsANewTableCarryingNeitherOwnershipColumn_AsNeedingADecision()
    {
        // Arrange — same technique, same reason: the shipped schema has no table that owns nothing
        // and is not exempt, because every such table has already been written down. Its own
        // container keeps the probe invisible to Database_LeavesNoTableUnclassified, which asserts
        // the exact opposite about the real schema.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await CreateProbeTableAsync(admin, UnownedProbeTable, "label text not null");

        // Act
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> unclassifiable = schema.Unclassifiable.Select(table => table.Name).ToList();
        List<string> needingAPolicy = schema.NeedingAPolicy
            .Select(classified => classified.Table.Name)
            .ToList();
        List<string> exempt = schema.Exempt.Select(entry => entry.Table.Name).ToList();

        // Assert — and the two negative assertions carry as much weight as the positive one. The
        // failure this bucket exists to prevent is not "wrongly reported"; it is "quietly reported
        // somewhere plausible", where a table owing nobody knows what either gets a policy demanded
        // of it that it cannot satisfy, or is waved through as excused by a list that never named
        // it.
        await Assert.That(unclassifiable).Contains(UnownedProbeTable);
        await Assert.That(needingAPolicy).DoesNotContain(UnownedProbeTable);
        await Assert.That(exempt).DoesNotContain(UnownedProbeTable);
    }

    [Test]
    public async Task Exemptions_DeclareTheOwnershipOfEveryTableTheyExempt()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — this is what stops an exemption outliving its reason. It used to read "no exempt
        // table may carry budget_id", which worked only while every reason on the list meant
        // "belongs to no tenant at all". It no longer does: credentials is genuinely user-owned and
        // exempt anyway, because it is read to discover who is asking and a policy keyed on the
        // identity it resolves would refuse the very query that resolves it. So the check had to
        // become more precise rather than stricter — each exemption now declares the ownership it
        // is exempt *despite*, and the live schema has to still agree with that declaration.
        // currencies growing user_id goes red; credentials growing budget_id goes red; credentials
        // simply staying user-owned stays green, which the old blanket rule could not express.
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> drifted = schema.Exempt
            .Where(entry => entry.Table.Ownership != entry.Exemption.ExemptDespite)
            .Select(entry =>
                $"{entry.Table.Name}: exempt because \"{entry.Exemption.Reason}\", declared "
                + $"{entry.Exemption.ExemptDespite}, but the schema now says "
                + $"{entry.Table.Ownership}")
            .ToList();

        // Assert — non-empty first, for the usual reason: with nothing discovered there is no
        // exempt table left to have drifted and the real assertion would pass on an empty list.
        await Assert.That(schema.Exempt).IsNotEmpty();
        await Assert.That(drifted).IsEmpty();
    }

    [Test]
    public async Task Exemptions_NameOnlyTablesThatExistInTheSchema()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — an exemption whose table is gone is worse than clutter. It is a name lying in wait
        // for whatever is next called that, ready to hand it a reason written about something else
        // entirely, so exemptions have to be pinned to the schema they exempt things from instead
        // of rotting silently as it moves under them.
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> rotted = schema.ExemptionsNamingNoTable
            .Select(exemption =>
                $"{exemption.Table}: exempt because \"{exemption.Reason}\", but no such table exists")
            .ToList();

        // Assert — this one needs no separate non-empty guard. Nothing discovered means every
        // exemption matched nothing, which is this list rather than an empty one.
        await Assert.That(rotted).IsEmpty();
    }

    [Test]
    public async Task Classification_WithNoExemptions_LeavesEveryExemptTableUnexcused()
    {
        // Arrange — the same live schema, handed an empty exemption set. The set is a parameter of
        // the classifier precisely so this test can pass a different one; a classifier that reached
        // for its own list could not be tested at all, only trusted.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act
        SchemaClassification schema = await ClassifySchemaAsync(admin, []);
        List<string> unexcused =
        [
            .. schema.NeedingAPolicy.Select(classified => classified.Table.Name),
            .. schema.Unclassifiable.Select(table => table.Name),
        ];

        // Assert — with nothing exempted, every table the real runs exempt must come back in one of
        // the two buckets that are red. The assertion had to widen from one bucket to two because
        // the exemptions no longer share a shape: credentials is user-owned and lands in
        // NeedingAPolicy, while currencies and __EFMigrationsHistory own nothing and land in
        // Unclassifiable. Insisting on the first bucket alone would now be wrong rather than
        // strict — it would fail on a classifier behaving exactly as designed. What matters is that
        // none of them is silently dropped, and both buckets stop a run.
        //
        // This stays a standing guard on the mechanism rather than on the schema: none of these
        // tables carries budget_id, so they can only appear here at all while discovery is still
        // "every ordinary table in public". Narrow it back to a column and this test goes red on
        // its own.
        foreach (TableExemption exemption in RowLevelSecurityCoverage.Exemptions)
        {
            await Assert.That(unexcused).Contains(exemption.Table);
        }

        // Nothing was exempted, so nothing may be reported as exempt, and no exemption can be
        // reported as naming a missing table. Both would mean the classification invented an entry
        // the caller never supplied.
        await Assert.That(schema.Exempt).IsEmpty();
        await Assert.That(schema.ExemptionsNamingNoTable).IsEmpty();
    }

    /// <summary>
    /// Reads the live schema and sorts it with the production classifier, against
    /// <paramref name="exemptions" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two calls rather than one, and this wrapper exists only to keep the pair from being spelled
    /// out eight times. The split itself belongs to the production type: discovery talks to a
    /// database and classification does not, so the rule that decides what a table owes can be
    /// exercised without one.
    /// </para>
    /// <para>
    /// The exemption set is a parameter of
    /// <see cref="RowLevelSecurityCoverage.Classify" /> and not a reach for
    /// <see cref="RowLevelSecurityCoverage.Exemptions" />, so that
    /// <see cref="Classification_WithNoExemptions_LeavesEveryExemptTableUnexcused" /> can classify
    /// the same schema against an empty set and prove the mechanism still sees the tables the real
    /// set hides. Hardwiring the real list into the classifier would leave the widened discovery
    /// with nothing to check it and only a comment to promise it.
    /// </para>
    /// </remarks>
    private static async Task<SchemaClassification> ClassifySchemaAsync(
        NpgsqlConnection connection,
        IReadOnlyList<TableExemption> exemptions)
    {
        IReadOnlyList<DiscoveredTable> tables =
            await RowLevelSecurityCoverage.DiscoverAsync(connection);
        return RowLevelSecurityCoverage.Classify(tables, exemptions);
    }

    /// <summary>
    /// Creates a table that exists only to be classified, in a container this test owns alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe is the only way to assert on a shape the shipped schema does not contain, and the
    /// alternative — asserting the rule against a hand-built list of
    /// <see cref="DiscoveredTable" /> records — would test the classifier against ownership this
    /// test decided, not ownership PostgreSQL reported. Discovery is exactly the half where a
    /// column can stop being noticed, so a probe that skips it proves the less interesting half.
    /// </para>
    /// <para>
    /// No foreign keys, no policy, no grant: every one of those would be a claim about what the
    /// table is for, and the point is that the classifier reaches its verdict from the columns
    /// alone. It is dropped by the container going away with the host, which is also why the name
    /// carries an <c>rls_probe_</c> prefix — if one ever does leak into a shared database, it says
    /// what it is.
    /// </para>
    /// </remarks>
    private static async Task CreateProbeTableAsync(
        NpgsqlConnection connection,
        string name,
        string ownershipColumn)
    {
        // Interpolated rather than parameterised because an identifier cannot be a parameter, and
        // both arguments are literals written above rather than anything reaching this from a
        // database or a caller outside the file.
        await using NpgsqlCommand command = new(
            $"create table {name} (id uuid primary key, {ownershipColumn})",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
