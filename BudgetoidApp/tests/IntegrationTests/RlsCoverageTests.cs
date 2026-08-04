using Infrastructure.Persistence.Provisioning;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the one thing no isolation test can: that <b>every</b> relation in the schema is accounted
/// for — policed by the policy its ownership requires, exempt for a reason someone wrote down, or
/// reported as something that cannot carry an enforced policy at all. The grant matrix and row-level
/// security fail in opposite directions, and that asymmetry is the whole reason this file exists. A
/// new table nobody grants is simply invisible to the application role — fail-closed, and the first
/// feature that touches it fails loudly with <c>42501</c>. A new table nobody writes a policy for is
/// fully readable and writable by the role across every tenant — fail-open, silent, and
/// indistinguishable from working. So an owned table needs both, and only a test that derives its
/// subject from the live schema can notice the second was forgotten.
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
/// The subject is <b>discovered</b> and never written down: every relation in <c>public</c>. What
/// <i>is</i> written down is <see cref="RowLevelSecurityCoverage.Exemptions" /> — the tables that
/// need no policy, each carrying the reason it belongs to no tenant and the ownership it is exempt
/// <b>despite</b>. Everything discovery finds and that list does not name must be policed, or must
/// be reported as unable to be.
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
/// discovery still reaches relations carrying neither ownership column.
/// </para>
/// <para>
/// Both ownership columns still appear below, in the opposite role to a filter: as facts read about
/// a table rather than conditions applied to one. Every exemption's reason amounts to "this table
/// is not policed the way its shape suggests", so an exempt table whose ownership has changed under
/// it has outlived the reason attached to it, and someone has to reconsider it rather than inherit
/// it.
/// </para>
/// <para>
/// <b>"Needs a policy" and "exempt" were never the only two answers, and this file has now been
/// wrong about that twice.</b> It first argued the pair was exhaustive, and that a table nobody had
/// thought about belonged in "needs a policy". That held only while exactly one policy name existed,
/// because there was only one policy such a table could possibly owe; with two names there is
/// nothing left to derive, so a table owning neither column became <b>unclassifiable</b> — a
/// refusal to guess, and red. The claim that survived that correction — "three buckets, and the
/// third is the undecided one" — is wrong in a different way, because it still assumes every
/// relation discovery finds is one an enforced policy <i>could</i> be attached to.
/// </para>
/// <para>
/// It cannot. A relation that is unable to carry an enforced policy is a third kind of answer, not a
/// harder instance of the first two, and demanding a policy of it would be demanding something
/// nobody can write. That is <b>unpoliceable</b>, the fifth bucket, and it is red for the same
/// reason unclassifiable is: <c>Unpoliceable</c> and <c>Unclassifiable</c> both mean a human has to
/// decide something, and they differ only in what the decision is. Unclassifiable asks "which
/// tenant owns these rows"; unpoliceable asks "why does this relation exist at all, and what stops
/// the application role reading every tenant through it".
/// </para>
/// <para>
/// The shapes that land there are not hypothetical, and each is invisible to a discovery that asks
/// only for ordinary tables. A <b>view</b> granted to the application role runs with its
/// <i>owner's</i> privileges unless <c>security_invoker</c> is set, and an owner bypasses row-level
/// security — so the role reads every tenant through it while coverage reports green. A
/// <b>materialized view</b> cannot carry an enforced policy at all. A <b>partitioned table</b> is
/// the worst of the three: its partitions are ordinary tables and are discovered, its parent is not,
/// and PostgreSQL applies the <i>parent's</i> policies to queries routed through the parent — so a
/// contributor is pushed into writing policies on partitions that never fire, with a green suite the
/// whole way. The partitioned parent therefore does <b>not</b> belong in this bucket: it can carry
/// an enforced policy, it simply has to be seen. Only the two view kinds cannot.
/// </para>
/// <para>
/// A second, independent gap has the opposite sign. An ownership column that is <b>nullable</b>
/// makes rows whose owner is NULL invisible to everyone, because <c>NULL = anything</c> is NULL and
/// never true. That is fail-closed and therefore not a leak — it is undiagnosable, which is its own
/// cost: the row exists, the policy is correct, and the application simply cannot see data it wrote.
/// The column that decides tenancy is required to be <c>NOT NULL</c>, and
/// <see cref="RowLevelSecurityCoverage.FindProblems" /> now enforces that rather than a comment
/// stating it.
/// </para>
/// <para>
/// Discovery, classification, the exemption list and the per-table rules all live in production code
/// (<see cref="RowLevelSecurityCoverage" />) rather than here, because the deploy-time verifier
/// reads the same rule. Two <i>executed</i> lists that disagree have no adjudicator, and the loser
/// fails open — which is the failure mode this whole file is about, one layer up. That is also why
/// <see cref="RowLevelSecurityCoverage.FindProblems" /> exists as a pure function instead of as
/// inline checks in the verifier: "what a healthy policy looks like" was being executed in two
/// places, and the copy the tests do not read is the copy that rots.
/// </para>
/// <para>
/// Every test here runs on the superuser connection. That is not a privilege question:
/// <c>pg_class</c> and <c>pg_policies</c> describe the schema, and the schema is the same whoever
/// reads it.
/// </para>
/// <para>
/// A discovery query that silently stopped matching anything would make these assertions pass
/// vacuously, so each test asserts the list it is about — or, where the real assertion is a negative
/// one, some list that proves discovery ran at all — is non-empty first.
/// </para>
/// </remarks>
public sealed class RlsCoverageTests
{
    /// <summary>
    /// Names of the throwaway relations the classification probes create. Constants rather than
    /// literals repeated at the creation and the lookup, because the two spellings drifting apart
    /// would not fail — the probe would simply never be found, and an assertion looking for a table
    /// that was never created is indistinguishable from one the classifier dropped.
    /// </summary>
    private const string UserOwnedProbeTable = "rls_probe_user_owned";

    private const string UnownedProbeTable = "rls_probe_unowned";

    private const string ViewProbe = "rls_probe_view";

    private const string MaterializedViewProbe = "rls_probe_materialized_view";

    private const string PartitionedParentProbe = "rls_probe_partitioned";

    private const string PartitionProbe = "rls_probe_partition";

    private const string HealthyProbeTable = "rls_probe_healthy";

    private const string NullableOwnerProbeTable = "rls_probe_nullable_owner";

    /// <summary>
    /// The predicate the shipped budget-owned policies actually carry, reproduced verbatim so the
    /// healthy probe is healthy by the standard the schema is held to rather than by a simplified
    /// stand-in. Writing <c>budget_id = current_setting(...)::uuid</c> here instead would make the
    /// positive control assert against a policy body nothing in production has, and the first thing
    /// it would stop catching is the <c>COALESCE</c> going missing — the exact shape ADR 0008 pins.
    /// </summary>
    private const string BudgetIsolationPredicate =
        "budget_id = COALESCE(current_setting('app.current_budget_id', true), '')::uuid";

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

        // Act — the undecided bucket, asserted empty against the schema we actually ship. Every
        // table here is either owned by a budget, owned by a user, or written down as owned by
        // nobody; anything else is a table whose tenancy nobody has decided, and the classifier
        // refuses to decide it on their behalf.
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
    public async Task Classification_TreatsAViewInPublic_AsUnpoliceable()
    {
        // Arrange — a view is the dangerous shape, not merely an unhandled one. Granted to
        // budgetoid_app it executes with its OWNER's privileges unless security_invoker is set, and
        // the owner bypasses row-level security entirely; so the application role reads every
        // tenant's rows through it while the table underneath stays correctly policed and every
        // coverage assertion in this file stays green. The probe needs no columns worth naming —
        // relkind decides this, not shape — so it selects a constant rather than a real table, which
        // also keeps it from implying that a view over an unpoliced table is the only problem case.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"create view {ViewProbe} as select 1 as id");

        // Act
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> unpoliceable = schema.Unpoliceable.Select(table => table.Name).ToList();
        List<string> needingAPolicy = schema.NeedingAPolicy
            .Select(classified => classified.Table.Name)
            .ToList();
        List<string> exempt = schema.Exempt.Select(entry => entry.Table.Name).ToList();
        List<string> unclassifiable = schema.Unclassifiable.Select(table => table.Name).ToList();

        // Assert — the "in none of the others" half is the point, and it is three assertions rather
        // than one because each wrong bucket reports a different wrong remedy. NeedingAPolicy tells
        // the reader to write a policy, which on a view is a statement PostgreSQL will not accept;
        // Exempt tells them it was considered and excused, which nobody did; Unclassifiable tells
        // them to pick an ownership column, which would not close the bypass even if they did.
        // Landing in two buckets at once is the same defect wearing a disguise — a relation with two
        // verdicts has no verdict.
        await Assert.That(schema.NeedingAPolicy).IsNotEmpty();
        await Assert.That(unpoliceable).Contains(ViewProbe);
        await Assert.That(needingAPolicy).DoesNotContain(ViewProbe);
        await Assert.That(exempt).DoesNotContain(ViewProbe);
        await Assert.That(unclassifiable).DoesNotContain(ViewProbe);

        // And the bucket has to say something a human can act on. This is the one bucket whose
        // remedy cannot be inferred from its name — "needs a policy" names its own fix, "unpolice-
        // able" names only the refusal — so the description is the entire actionable output, printed
        // by both this suite and the deploy verifier. It must at minimum identify which relation it
        // is talking about, or a failing deploy reports a problem without a subject.
        DiscoveredTable relation = schema.Unpoliceable.Single(table => string.Equals(
            table.Name, ViewProbe, StringComparison.Ordinal));
        await Assert.That(RowLevelSecurityCoverage.DescribeUnpoliceable(relation))
            .Contains(ViewProbe);
    }

    [Test]
    public async Task Classification_TreatsAMaterializedViewInPublic_AsUnpoliceable()
    {
        // Arrange — this triangulates rather than repeats. The cheapest way to make the view test
        // above pass is to add relkind 'v' to discovery and route it to the new bucket; a
        // materialized view is 'm', so that implementation still cannot see this one and this test
        // still fails. Two examples force the rule to be "which kinds can carry an enforced policy"
        // instead of "the one kind the last test named".
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(
            admin, $"create materialized view {MaterializedViewProbe} as select 1 as id");

        // Act
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> unpoliceable = schema.Unpoliceable.Select(table => table.Name).ToList();
        List<string> needingAPolicy = schema.NeedingAPolicy
            .Select(classified => classified.Table.Name)
            .ToList();
        List<string> exempt = schema.Exempt.Select(entry => entry.Table.Name).ToList();
        List<string> unclassifiable = schema.Unclassifiable.Select(table => table.Name).ToList();

        // Assert — same four-way shape as the view, and for a reason that is stronger here rather
        // than weaker: a materialized view cannot carry an enforced policy at all, so "needs a
        // policy" is not merely the wrong remedy for it, it is an impossible one. Its stored rows
        // were computed by whoever refreshed it, which is another way of saying the isolation
        // question was already answered somewhere this file cannot see.
        await Assert.That(schema.NeedingAPolicy).IsNotEmpty();
        await Assert.That(unpoliceable).Contains(MaterializedViewProbe);
        await Assert.That(needingAPolicy).DoesNotContain(MaterializedViewProbe);
        await Assert.That(exempt).DoesNotContain(MaterializedViewProbe);
        await Assert.That(unclassifiable).DoesNotContain(MaterializedViewProbe);
    }

    [Test]
    public async Task Classification_TreatsAPartitionedTableAndItsPartition_AsNeedingAPolicy()
    {
        // Arrange — the partitioned parent is the shape this whole cycle exists for, and it is the
        // only one of the three that is invisible while looking fully handled. A partition is
        // relkind 'r' and is therefore discovered today; the parent is 'p' and is not. PostgreSQL
        // applies the PARENT's policies to queries routed through the parent — which is how the
        // application reaches these rows — so today's discovery would report the partitions as the
        // subjects, a contributor would dutifully police them, those policies would never fire, and
        // the suite would be green throughout. Both names have to come back, and the parent matters
        // more than the partition: policing only the partition protects nothing, while policing only
        // the parent protects every query the application actually sends. The partition is asserted
        // anyway because dropping it would be the other silent failure — a partition read directly
        // is an ordinary table with rows in it.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(
            admin,
            $"create table {PartitionedParentProbe} (id uuid, budget_id uuid not null) "
            + "partition by list (budget_id)");
        await ExecuteAsync(
            admin,
            $"create table {PartitionProbe} partition of {PartitionedParentProbe} "
            + "for values in ('00000000-0000-0000-0000-000000000001')");

        // Act — both are budget-owned by the same column, so both owe the same policy name, and the
        // pairing is formatted into one string per relation so a failure says which relation got
        // which verdict instead of two lists the reader has to line up by hand.
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> requiredOfProbes = schema.NeedingAPolicy
            .Where(classified =>
                classified.Table.Name is PartitionedParentProbe or PartitionProbe)
            .Select(classified => $"{classified.Table.Name}={classified.RequiredPolicyName}")
            .ToList();

        // Assert
        string budgetIsolation = RowLevelSecurityCoverage.BudgetIsolationPolicyName;
        await Assert.That(requiredOfProbes)
            .Contains($"{PartitionedParentProbe}={budgetIsolation}");
        await Assert.That(requiredOfProbes).Contains($"{PartitionProbe}={budgetIsolation}");
    }

    [Test]
    public async Task FindProblems_ForAHealthyBudgetOwnedTable_ReportsNothing()
    {
        // Arrange — the positive control, and it is not optional politeness. FindProblems returning
        // a non-empty list for everything would pass the nullable-column test below perfectly, for
        // entirely the wrong reason; only a table this file has built to be healthy in every respect
        // can tell "it detected the nullable column" apart from "it complains about everything". So
        // the probe carries a NOT NULL ownership column, row-level security actually enabled, and
        // exactly one policy: the real name, bound to the real role, carrying the real predicate on
        // both USING and WITH CHECK.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await CreatePolicedBudgetOwnedProbeAsync(admin, HealthyProbeTable, nullableOwner: false);

        // Act — FindProblems is pure and takes the classified table, so this exercises the rules the
        // deploy verifier used to hold inline. That is the point of the extraction: two executed
        // copies of "what a policy must look like" have no adjudicator, and the copy no test reads
        // is the copy that drifts.
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> problems = schema.NeedingAPolicy
            .Where(classified => string.Equals(
                classified.Table.Name, HealthyProbeTable, StringComparison.Ordinal))
            .SelectMany(RowLevelSecurityCoverage.FindProblems)
            .ToList();

        // Assert — the membership check first, because "no problems" is what an empty selection
        // produces too: a probe the classifier never routed to NeedingAPolicy would report a clean
        // bill of health it was never examined for.
        List<string> needingAPolicy = schema.NeedingAPolicy
            .Select(classified => classified.Table.Name)
            .ToList();
        await Assert.That(needingAPolicy).Contains(HealthyProbeTable);
        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task FindProblems_ForATableWhoseOwnershipColumnIsNullable_ReportsThatColumn()
    {
        // Arrange — one character different from the probe above, and the difference is the whole
        // test. A nullable budget_id makes every row whose owner is NULL invisible to everyone,
        // because NULL = anything is NULL and never true. That fails CLOSED, so it leaks nothing and
        // is not a security hole; it is an undiagnosable one — the write succeeds, the policy is
        // correct, and the application simply cannot read back data it just stored. Nothing in the
        // schema, the grants or the policy body says why. So the column that decides tenancy is
        // required to be NOT NULL, and this is where that stops being a sentence in a document.
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await CreatePolicedBudgetOwnedProbeAsync(
            admin, NullableOwnerProbeTable, nullableOwner: true);

        // Act
        SchemaClassification schema = await ClassifySchemaAsync(
            admin, RowLevelSecurityCoverage.Exemptions);
        List<string> problems = schema.NeedingAPolicy
            .Where(classified => string.Equals(
                classified.Table.Name, NullableOwnerProbeTable, StringComparison.Ordinal))
            .SelectMany(RowLevelSecurityCoverage.FindProblems)
            .ToList();

        // Assert — exactly one, not at least one, and that is the strict reading on purpose. The
        // probe is deliberately healthy in every other respect, so its health is part of what is
        // being asserted: "at least one" would pass just as happily if the rule also invented
        // complaints about the policy name, the role, the predicate or relrowsecurity, and this
        // test would then be silently doing the positive control's job badly instead of its own.
        // The message has to name the column, because "this table has a problem" is not something
        // anyone can act on when the table has several columns and only one of them decides tenancy.
        await Assert.That(problems.Count).IsEqualTo(1);
        await Assert.That(string.Join(" | ", problems)).Contains("budget_id");
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
    public async Task Exemptions_PinTheColumnsTheirReasonCovers()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — the coverage rule fails closed on a new TABLE and says nothing at all about a new
        // COLUMN on a table that is already exempt. That silence is not a small residue of the
        // design, it is the exact shape of the next hole: an exemption is argued about one QUERY and
        // applied by PostgreSQL to a whole TABLE, and there is no finer grain to apply it at. The
        // credentials exemption reads "this is the table read to discover who is asking", which is
        // an argument about four columns and a primary key; the effect is a table-wide SELECT the
        // application role holds on every session regardless of which user that session names.
        //
        // That gap is cheap only while the columns are the discovery ones, and it is specified to
        // stop being cheap. A passkey's public key and signature counter are specified to arrive
        // here, and a recovery factor's wrapped content and index keys after them — a registered
        // passkey IS a recovery factor. All of that is material read AFTER authentication has
        // already answered who is asking, which is to say material with a real tenant, landing on
        // the one table whose whole reason for being exempt is that it must be readable before any
        // tenant is known.
        //
        // So the exemption is not what gets removed — removing it is not even available. A WebAuthn
        // assertion verifies a signature with the public key BEFORE it knows whose account it is, so
        // the discovery columns genuinely have to be reachable with no identity on the session. What
        // gets fixed is the SCOPE: the exemption keeps only what answers "who is asking" and "is
        // this really them", and everything read after that answer moves to a policed table carrying
        // user_id — which the classifier then catches by itself, with no new rule at all.
        //
        // This test is the thing that forces that decision to be made rather than drifted past. A
        // non-null ColumnsTheReasonCovers means "this exemption was argued over exactly this set of
        // columns, and a new one invalidates the argument"; null means the reason does not depend on
        // the table's shape and the reason itself has to say why.
        //
        // WHEN THIS GOES RED, THE FIX IS TO MOVE THE COLUMN, NOT TO WIDEN THE PINNED LIST. Appending
        // the new column name here is the drift this test exists to stop, and it is the fix that
        // will look obvious at the moment it is least true.
        //
        // Columns are read with a small query of this file's own rather than through the classifier,
        // the same habit DeploymentProvisioningTests keeps and for the same reason: this test states
        // independently what the schema holds, so a classifier that stopped seeing a column cannot
        // also decide that the column is not there.
        List<TableExemption> pinned = RowLevelSecurityCoverage.Exemptions
            .Where(exemption => exemption.ColumnsTheReasonCovers is not null)
            .ToList();
        List<string> live = [];
        List<string> argued = [];
        foreach (TableExemption exemption in pinned)
        {
            // Qualified with the table name on both sides so one order-insensitive comparison can
            // cover every pinned exemption at once and a failure still says which table grew or lost
            // a column, instead of dumping two bare column lists the reader has to attribute by hand.
            IReadOnlyList<string> columns = await ReadColumnNamesAsync(admin, exemption.Table);
            live.AddRange(columns.Select(column => $"{exemption.Table}.{column}"));
            argued.AddRange(
                (exemption.ColumnsTheReasonCovers ?? []).Select(
                    column => $"{exemption.Table}.{column}"));
        }

        // Assert — non-vacuity first, and it is not the usual discovery guard. Every entry on the
        // list being null would make the real assertion below compare nothing against nothing and
        // pass forever, which is precisely the state this test was written to leave behind: an
        // exemption list where nobody has to say which columns their reason covers.
        //
        // Equality as SETS, order-insensitive, for the reason SchemaConstraintSnapshotTests gives
        // about constraints — a column set is a set and catalog order is not policy. Equality rather
        // than containment in either direction: a column appearing that the reason never covered is
        // the leak, and a column disappearing means the argument was written about a table that no
        // longer exists in that shape, and both have to be reconsidered by a person.
        await Assert.That(pinned).IsNotEmpty();
        await Assert.That(live).IsEquivalentTo(argued);
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
    public async Task Exemptions_NameEachTableAtMostOnce()
    {
        // Arrange — no host, no container, no connection. This asserts about the written-down list
        // alone, and the list is a static property; involving a database would only make a pure
        // statement about a constant slow and dependent on Docker.

        // Act — a duplicate entry is not merely untidy, it is lost SILENTLY. Classify matches the
        // first exemption naming a table and stops, so the second copy appears in neither Exempt nor
        // ExemptionsNamingNoTable: it is not applied, and it is not reported as unapplied either.
        // A reason nobody deleted therefore sits in the list reading as current, and the next person
        // to read it — deciding whether credentials may still skip its policy, say — is reading a
        // sentence with no effect on anything.
        List<string> duplicated = RowLevelSecurityCoverage.Exemptions
            .GroupBy(exemption => exemption.Table, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: named by {group.Count()} exemptions")
            .ToList();

        // Assert — and yes, this passes the moment it is written, which is deliberate and not an
        // oversight. It is a standing guard rather than a red-then-green step: nothing about today's
        // three entries is being fixed here, and the failure it exists for is a future edit that
        // adds a fourth entry for a table already on the list. A test that can only fail later is
        // still the only thing standing between that edit and a silently dead reason. The non-empty
        // guard is the usual one: an Exemptions property that somehow emptied would make "no
        // duplicates" true and meaningless.
        await Assert.That(RowLevelSecurityCoverage.Exemptions).IsNotEmpty();
        await Assert.That(duplicated).IsEmpty();
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
            .. schema.Unpoliceable.Select(table => table.Name),
        ];

        // Assert — the claim is "with nothing exempted, no exemption is silently dropped", and the
        // union has to cover EVERY red bucket for that claim to mean anything. Each widening of it
        // has been strictly stricter than the last, never a weakening: the set of names being
        // demanded is unchanged, only the set of places they are allowed to turn up grows, so
        // nothing that used to fail here can start passing.
        //
        // It went from one bucket to two when the exemptions stopped sharing a shape — credentials
        // is user-owned and lands in NeedingAPolicy, while currencies and __EFMigrationsHistory own
        // nothing and land in Unclassifiable — and insisting on the first bucket alone had become
        // wrong rather than strict, failing on a classifier behaving exactly as designed. It goes to
        // three now for the reason the widening is worth doing at all: an exemption naming a view
        // would land in Unpoliceable, and a union that stopped at two would find it in none of the
        // buckets it looked at and report the exemption as silently dropped — from the very check
        // written to catch silent dropping. Every one of the three stops a run, so any of them is an
        // acceptable answer here and none of them is an escape.
        //
        // This stays a standing guard on the mechanism rather than on the schema: none of these
        // tables carries budget_id, so they can only appear here at all while discovery is still
        // reaching every relation in public rather than a filtered subset of it. Narrow it back to a
        // column and this test goes red on its own.
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
    /// out a dozen times. The split itself belongs to the production type: discovery talks to a
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
    /// column, or an entire relation kind, can stop being noticed, so a probe that skips it proves
    /// the less interesting half.
    /// </para>
    /// <para>
    /// No foreign keys, no policy, no grant: every one of those would be a claim about what the
    /// table is for, and the point is that the classifier reaches its verdict from the columns
    /// alone. Probes that <i>do</i> need a policy go through
    /// <see cref="CreatePolicedBudgetOwnedProbeAsync" /> instead, where the policy is the subject
    /// rather than noise. It is dropped by the container going away with the host, which is also
    /// why the name carries an <c>rls_probe_</c> prefix — if one ever does leak into a shared
    /// database, it says what it is.
    /// </para>
    /// </remarks>
    private static Task CreateProbeTableAsync(
        NpgsqlConnection connection,
        string name,
        string ownershipColumn) =>
        ExecuteAsync(connection, $"create table {name} (id uuid primary key, {ownershipColumn})");

    /// <summary>
    /// Creates a budget-owned probe table that is policed exactly the way the shipped schema polices
    /// its own, differing only in whether the ownership column is nullable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One helper for both <see cref="FindProblems_ForAHealthyBudgetOwnedTable_ReportsNothing" /> and
    /// <see cref="FindProblems_ForATableWhoseOwnershipColumnIsNullable_ReportsThatColumn" />, because
    /// the pair is a controlled experiment and a controlled experiment with two independently
    /// written setups is not one. Spelling the DDL out twice would let the "healthy" probe and the
    /// "healthy except for one thing" probe drift into differing in two things, at which point the
    /// second test no longer isolates the column it names.
    /// </para>
    /// <para>
    /// <paramref name="nullableOwner" /> is the single variable, passed at every call site as a named
    /// argument: <c>true</c> at a call reading <c>CreatePolicedBudgetOwnedProbeAsync(admin, name,
    /// true)</c> would be a bare boolean deciding the entire meaning of a security test.
    /// </para>
    /// <para>
    /// Row-level security is enabled explicitly, because a policy on a table without it is inert —
    /// PostgreSQL keeps the definition and enforces nothing. A probe missing that line would be a
    /// table that looks policed in the catalog and is wide open in practice, which is a shape these
    /// tests are supposed to detect rather than create.
    /// </para>
    /// </remarks>
    private static async Task CreatePolicedBudgetOwnedProbeAsync(
        NpgsqlConnection connection,
        string name,
        bool nullableOwner)
    {
        string nullability = nullableOwner ? "null" : "not null";
        await ExecuteAsync(
            connection, $"create table {name} (id uuid primary key, budget_id uuid {nullability})");
        await ExecuteAsync(connection, $"alter table {name} enable row level security");

        // TO budgetoid_app rather than TO PUBLIC, and FOR ALL rather than FOR SELECT: the probe has
        // to be healthy by the same reading the real tables are, and a policy that binds a different
        // role or covers a narrower command would make the positive control assert that a policy
        // nothing in production has is acceptable.
        await ExecuteAsync(
            connection,
            $"create policy {RowLevelSecurityCoverage.BudgetIsolationPolicyName} on {name} "
            + $"for all to {DatabaseProvisioning.AppRoleName} "
            + $"using ({BudgetIsolationPredicate}) "
            + $"with check ({BudgetIsolationPredicate})");
    }

    /// <summary>
    /// Reads the column names a relation carries today, straight from <c>pg_attribute</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not routed through <see cref="RowLevelSecurityCoverage" />. Everything else in
    /// this file asks the production classifier what it thinks; this asks the catalog what is there,
    /// so that a pinned column set is checked against the schema rather than against the same code
    /// that would have to have noticed the column in the first place. It is the habit
    /// <c>DeploymentProvisioningTests</c> keeps for the same reason.
    /// </para>
    /// <para>
    /// <c>attnum &gt; 0</c> drops the system columns, which belong to PostgreSQL and not to anyone's
    /// argument about a table; <c>not attisdropped</c> drops the tombstones a dropped column leaves
    /// behind, which are still rows in <c>pg_attribute</c> under mangled names and would make an
    /// otherwise-correct pinned set look wrong forever.
    /// </para>
    /// <para>
    /// The table name is a real parameter rather than an interpolation, unlike the DDL helpers here:
    /// it is a value in a <c>where</c> clause instead of an identifier, so nothing forces it into the
    /// statement text.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        NpgsqlConnection connection,
        string table)
    {
        const string sql =
            """
            select a.attname::text
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and c.relname = @table
              and a.attnum > 0
              and not a.attisdropped
            order by a.attname
            """;

        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("table", table);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> columns = [];

        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>
    /// Runs one DDL statement on the admin connection.
    /// </summary>
    /// <remarks>
    /// Interpolated rather than parameterised because an identifier cannot be a parameter, and every
    /// argument reaching this is a literal written in this file rather than anything arriving from a
    /// database or a caller outside it.
    /// </remarks>
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
}
