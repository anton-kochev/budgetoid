using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Transactions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers tenant isolation as the <b>database</b> enforces it, on two axes. The budget-owned tables
/// — <c>accounts</c>, <c>category_groups</c>, <c>categories</c>, <c>payees</c>,
/// <c>transactions</c> — are isolated by the session's ambient budget; <c>users</c>, <c>budgets</c>
/// and <c>sessions</c> sit above that scope (a user owns budgets rather than belonging to one, a
/// budget is the tenant rather than a tenant's row, and a sign-in reaches an account before it
/// reaches any budget) and are isolated by the session's user instead.
/// Both axes exist in EF's global query filters too, which are application code and therefore hold
/// exactly as long as the application remembers them: raw SQL, <c>IgnoreQueryFilters</c>, a
/// repository written in a hurry, and a hand-run script all walk straight past. Row-level security is
/// the layer that holds when they do, which is why every statement in this file is raw Npgsql on
/// <see cref="RepositoryTestHost.AppConnectionString" /> — going through EF would only re-measure the
/// filters these tests exist to be independent of.
/// </summary>
/// <remarks>
/// <para>
/// Two connections, and the split is load-bearing. The probes run on the application role, which is
/// neither superuser nor table owner, so policies bind to it. The read-backs run on the container
/// superuser, for which PostgreSQL skips row-level security entirely — that bypass is what lets a
/// test assert "budget B's row is still exactly as it was" after a refusal, which no connection
/// subject to the policy could observe.
/// </para>
/// <para>
/// Every negative is paired, in the same test, with the identical statement aimed at the ambient
/// budget's own row, which must succeed. Without the pair each refusal is vacuous: a policy of
/// <c>USING (false)</c> hides everything from everyone and passes every negative here on its own.
/// The positive half is what pins "another budget's rows are unreachable" rather than "no rows are
/// reachable". Same reasoning as the class remarks on <see cref="TenancySchemaTests" />.
/// </para>
/// <para>
/// The write probes deliberately never touch <c>budget_id</c>. The role's <c>UPDATE</c> grants are
/// column lists and <c>budget_id</c> is on none of them, so <c>set budget_id = …</c> is refused
/// with <c>42501</c> by the column grant before row-level security is consulted — a probe shaped
/// that way passes today, against no policy at all, and measures the grant matrix instead. The
/// updates below therefore write a granted text column, and the inserts satisfy every foreign key
/// they touch by building each row out of the parents of the very budget it names, so that the only
/// thing wrong with a rejected statement is the budget.
/// </para>
/// <para>
/// Each test collects its per-table outcomes and asserts the collection at the end rather than
/// asserting inside the loop. A per-table assertion stops the run at the first table that leaks;
/// collecting means one run names every table that does.
/// </para>
/// </remarks>
public sealed class RlsIsolationTests
{
    /// <summary>
    /// Minor unit of the USD rows these tests seed. Precision is not what any of them is about; the
    /// constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    /// <summary>
    /// The five tables a budget owns, in no particular order — the read probe has no dependencies
    /// between tables to respect.
    /// </summary>
    private static readonly string[] BudgetOwnedTables =
    [
        "accounts", "category_groups", "categories", "payees", "transactions",
    ];

    /// <summary>
    /// One writable text column per budget-owned table, with the value the seeding gives it. The
    /// column is chosen off the role's <c>UPDATE</c> grant lists, so a refusal can only come from
    /// row-level security: <c>transactions</c> has no <c>name</c>, hence <c>description</c>.
    /// </summary>
    private static readonly (string Table, string Column, string SeededValue)[] WritableTextColumns =
    [
        ("accounts", "name", "Checking"),
        ("category_groups", "name", "Everyday"),
        ("categories", "name", "Groceries"),
        ("payees", "name", "Corner Shop"),
        ("transactions", "description", "Weekly shop"),
    ];

    /// <summary>
    /// The deletable budget-owned tables, ordered so a budget's rows can be removed without tripping
    /// a foreign key: transactions reference accounts, categories reference category groups, and
    /// both references are <c>ON DELETE RESTRICT</c>.
    /// </summary>
    /// <remarks>
    /// <c>payees</c> is absent on purpose. The role has no <c>DELETE</c> grant on it at all, so a
    /// delete probe there would be refused with <c>42501</c> by the grant matrix whether a policy
    /// exists or not — it would go green today and measure nothing about isolation.
    /// </remarks>
    private static readonly string[] DeletableTablesInDependencyOrder =
    [
        "transactions", "categories", "category_groups", "accounts",
    ];

    [Test]
    public async Task Database_ShowsOnlyTheAmbientBudgetsRowsToASelect()
    {
        // Arrange — both budgets carry the same shape of rows, so "budget B has none of this table"
        // is never the reason a count comes back zero.
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid ownerId, BudgetRows ambient, BudgetRows other) = await SeedTwoPopulatedBudgetsAsync(host);
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(ownerId, ambient.BudgetId);

        // Act — both halves per table. The ambient half is not decoration: a policy that hides every
        // row from everyone satisfies the foreign half on its own, and only this count notices.
        List<string> ownRowsMissing = [];
        List<string> foreignRowsVisible = [];
        foreach (string table in BudgetOwnedTables)
        {
            long own = await CountRowsAsync(app, table, ambient.BudgetId);
            if (own != 1L)
            {
                ownRowsMissing.Add($"{table}: saw {own} of its own rows, wanted 1");
            }

            long foreign = await CountRowsAsync(app, table, other.BudgetId);
            if (foreign != 0L)
            {
                foreignRowsVisible.Add($"{table}: saw {foreign} of another budget's rows");
            }
        }

        // Assert
        await Assert.That(ownRowsMissing).IsEmpty();
        await Assert.That(foreignRowsVisible).IsEmpty();
    }

    [Test]
    public async Task Database_RefusesToUpdateAnotherBudgetsRow_WhileStillAllowingItsOwn()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid ownerId, BudgetRows ambient, BudgetRows other) = await SeedTwoPopulatedBudgetsAsync(host);
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(ownerId, ambient.BudgetId);

        // Act — a cross-budget update is not refused with an error under row-level security; the row
        // simply is not there to match, so the statement succeeds having affected nothing. That is
        // why the affected count is the observation, and why the read-back below is not optional.
        const string overwritten = "Overwritten from another budget";
        const string renamed = "Renamed in place";
        List<string> foreignRowsReached = [];
        List<string> ownRowsUnreachable = [];
        foreach ((string table, string column, _) in WritableTextColumns)
        {
            int foreign = await UpdateTextAsync(app, table, column, other.RowIn(table), overwritten);
            if (foreign != 0)
            {
                foreignRowsReached.Add($"{table}.{column}: affected {foreign} of another budget's rows");
            }

            int own = await UpdateTextAsync(app, table, column, ambient.RowIn(table), renamed);
            if (own != 1)
            {
                ownRowsUnreachable.Add($"{table}.{column}: affected {own} of its own rows, wanted 1");
            }
        }

        // Assert — the counts first, then what actually survived. An affected count of zero and a
        // statement that was silently filtered are indistinguishable from the count alone, so the
        // foreign rows are read back on the superuser connection, which row-level security does not
        // apply to and which can therefore still see them.
        await Assert.That(ownRowsUnreachable).IsEmpty();
        await Assert.That(foreignRowsReached).IsEmpty();

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        List<string> foreignRowsChanged = [];
        foreach ((string table, string column, string seeded) in WritableTextColumns)
        {
            object? actual = await ReadColumnAsync(admin, table, column, other.RowIn(table));
            if (actual is not string text || text != seeded)
            {
                foreignRowsChanged.Add($"{table}.{column}: '{actual ?? "null"}', wanted '{seeded}'");
            }
        }

        await Assert.That(foreignRowsChanged).IsEmpty();
    }

    [Test]
    public async Task Database_RefusesToDeleteAnotherBudgetsRow_WhileStillAllowingItsOwn()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid ownerId, BudgetRows ambient, BudgetRows other) = await SeedTwoPopulatedBudgetsAsync(host);
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(ownerId, ambient.BudgetId);

        // Act — the foreign deletes run first and in dependency order. Order matters even though
        // every one of them is expected to affect nothing: if the policy is missing they will all
        // land, and a delete that lands out of order trips a RESTRICT foreign key and reports 23503
        // instead of the affected count this test is reading. The failure would still be a failure,
        // but it would be a failure about foreign keys rather than about isolation.
        List<string> foreignRowsReached = [];
        foreach (string table in DeletableTablesInDependencyOrder)
        {
            int foreign = await DeleteAsync(app, table, other.RowIn(table));
            if (foreign != 0)
            {
                foreignRowsReached.Add($"{table}: deleted {foreign} of another budget's rows");
            }
        }

        List<string> ownRowsUnreachable = [];
        foreach (string table in DeletableTablesInDependencyOrder)
        {
            int own = await DeleteAsync(app, table, ambient.RowIn(table));
            if (own != 1)
            {
                ownRowsUnreachable.Add($"{table}: deleted {own} of its own rows, wanted 1");
            }
        }

        // Assert
        await Assert.That(ownRowsUnreachable).IsEmpty();
        await Assert.That(foreignRowsReached).IsEmpty();

        // What survived, on the connection that can see it. A count rather than a column read: the
        // question here is whether the row still exists at all.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        List<string> foreignRowsGone = [];
        foreach (string table in DeletableTablesInDependencyOrder)
        {
            long surviving = await CountRowsAsync(admin, table, other.BudgetId);
            if (surviving != 1L)
            {
                foreignRowsGone.Add($"{table}: {surviving} rows left in the other budget, wanted 1");
            }
        }

        await Assert.That(foreignRowsGone).IsEmpty();
    }

    [Test]
    public async Task Database_RefusesToInsertARowIntoAnotherBudget_WhileStillAllowingItsOwn()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        (Guid ownerId, BudgetRows ambient, BudgetRows other) = await SeedTwoPopulatedBudgetsAsync(host);
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(ownerId, ambient.BudgetId);

        // Act — unlike UPDATE and DELETE, a refused INSERT is loud: the WITH CHECK half of the
        // policy raises 42501, "new row violates row-level security policy". Each probe row is built
        // from the parents of the budget it names — the group for a category, the account for a
        // transaction — because those references are composite on (id, budget_id) and a mismatched
        // parent raises 23503 before the policy is reached, which would let this test pass on a
        // foreign key while the policy is missing entirely.
        List<string> foreignInsertsAccepted = [];
        List<string> ownInsertsRejected = [];
        foreach (string table in BudgetOwnedTables)
        {
            await using NpgsqlCommand intoOther = BuildInsertProbe(app, table, other);
            PostgresException? refusal = await CaptureRefusalAsync(intoOther);
            if (refusal?.SqlState != PostgresErrorCodes.InsufficientPrivilege)
            {
                foreignInsertsAccepted.Add(
                    $"{table}: got {refusal?.SqlState ?? "no error"}, wanted {PostgresErrorCodes.InsufficientPrivilege}");
            }

            await using NpgsqlCommand intoOwn = BuildInsertProbe(app, table, ambient);
            int inserted = await intoOwn.ExecuteNonQueryAsync();
            if (inserted != 1)
            {
                ownInsertsRejected.Add($"{table}: inserted {inserted} rows into its own budget, wanted 1");
            }
        }

        // Assert
        await Assert.That(ownInsertsRejected).IsEmpty();
        await Assert.That(foreignInsertsAccepted).IsEmpty();

        // And nothing landed. A SQLSTATE says the statement was rejected; only this says the other
        // budget still holds exactly the one row it was seeded with.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        List<string> foreignRowsAdded = [];
        foreach (string table in BudgetOwnedTables)
        {
            long rows = await CountRowsAsync(admin, table, other.BudgetId);
            if (rows != 1L)
            {
                foreignRowsAdded.Add($"{table}: {rows} rows in the other budget, wanted 1");
            }
        }

        await Assert.That(foreignRowsAdded).IsEmpty();
    }

    [Test]
    public async Task Database_RefusesToReadAnythingWhenTheSessionNamesNoBudget()
    {
        // Arrange — a bare app-role connection: no set_config, so the session declares no ambient
        // budget. This is the shape of every bug where application code forgets to set one, and it
        // must fail loudly rather than quietly returning an empty result that reads as "no data".
        await using RepositoryTestHost host = await StartHostAsync();
        (_, BudgetRows ambient, _) = await SeedTwoPopulatedBudgetsAsync(host);
        await using NpgsqlConnection bare = new(host.AppConnectionString);
        await bare.OpenAsync();

        // Act — accounts is seeded, and that is a precondition rather than a convenience. A policy
        // qual is only evaluated when there are candidate rows, so the same query over an empty
        // table returns zero rows without ever touching the setting and this guarantee does not
        // reach it. That is the honest limit of what this test proves.
        await using NpgsqlCommand read = new("select count(*) from accounts", bare);
        PostgresException? refusal = await CaptureRefusalAsync(read);

        // Assert — 22P02, not "unrecognized configuration parameter". The determinism comes from the
        // shape of the policy itself, which reads the setting as
        // COALESCE(current_setting('app.current_budget_id', true), '')::uuid. Strict current_setting
        // would be 42704 on a backend that has never seen the setting and 22P02 on one Npgsql had
        // already recycled, which is not a thing a test can assert; the missing_ok overload turns the
        // first case into NULL and the COALESCE turns that NULL into the same ''::uuid cast the
        // recycled connection already produced. Both paths therefore fail identically — one bug, one
        // SQLSTATE.
        //
        // The null coalesce is for the failure message, not the logic: a bare refusal?.SqlState
        // renders a statement that succeeded as the empty string, which reads as an exception
        // carrying a blank SQLSTATE rather than as no exception at all.
        await Assert.That(refusal?.SqlState ?? "no error")
            .IsEqualTo(PostgresErrorCodes.InvalidTextRepresentation);

        // The ambient budget's rows are still there — the refusal above is the session's doing, not
        // a seeding failure that would make every SQLSTATE assertion here meaningless.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountRowsAsync(admin, "accounts", ambient.BudgetId)).IsEqualTo(1L);
    }

    [Test]
    public async Task Database_ShowsOnlyTheSessionsOwnUserRow()
    {
        // Arrange — two owners, because users is isolated by user and not by budget: a second budget
        // under the same owner would be invisible to this rule, and the foreign count would come
        // back zero with or without a policy.
        await using RepositoryTestHost host = await StartHostAsync();
        (RepositoryTestHost.SeededOwner session, RepositoryTestHost.SeededOwner other) =
            await SeedTwoOwnersAsync(host);
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(session.UserId);

        // Act — both halves, on one session. The own count is not decoration: a policy that hides
        // every row from everyone satisfies the foreign half on its own, and only this notices.
        long own = await CountKeyedRowsAsync(app, "users", "id", session.UserId);
        long foreign = await CountKeyedRowsAsync(app, "users", "id", other.UserId);

        // Assert
        await Assert.That(own).IsEqualTo(1L);
        await Assert.That(foreign).IsEqualTo(0L);
    }

    [Test]
    public async Task Database_ShowsOnlyTheSessionsOwnBudgets()
    {
        // Arrange — two owners with one budget each, both keyed on user_id, so "the other owner has
        // no budgets" is never the reason the foreign count is zero.
        await using RepositoryTestHost host = await StartHostAsync();
        (RepositoryTestHost.SeededOwner session, RepositoryTestHost.SeededOwner other) =
            await SeedTwoOwnersAsync(host);
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(session.UserId);

        // Act
        long own = await CountKeyedRowsAsync(app, "budgets", "user_id", session.UserId);
        long foreign = await CountKeyedRowsAsync(app, "budgets", "user_id", other.UserId);

        // Assert
        await Assert.That(own).IsEqualTo(1L);
        await Assert.That(foreign).IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesToInsertABudgetForAnotherUser_WhileStillAllowingItsOwn()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        (RepositoryTestHost.SeededOwner session, RepositoryTestHost.SeededOwner other) =
            await SeedTwoOwnersAsync(host);
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(session.UserId);

        // Act — the WITH CHECK half, which is the only half a SELECT cannot reach: hiding another
        // owner's budgets says nothing about whether this session can create one under their name.
        // A refused INSERT is loud, unlike a filtered UPDATE — 42501, "new row violates row-level
        // security policy".
        await using NpgsqlCommand forOther = BuildBudgetInsertProbe(app, other.UserId);
        PostgresException? refusal = await CaptureRefusalAsync(forOther);

        await using NpgsqlCommand forOwn = BuildBudgetInsertProbe(app, session.UserId);
        int inserted = await forOwn.ExecuteNonQueryAsync();

        // Assert — the null coalesce is for the failure message: a bare refusal?.SqlState renders a
        // statement that went through as the empty string, which reads as a blank SQLSTATE rather
        // than as no exception at all.
        await Assert.That(refusal?.SqlState ?? "no error")
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(inserted).IsEqualTo(1);

        // And nothing landed. A SQLSTATE says the statement was rejected; only this says the other
        // owner still holds exactly the one budget they were seeded with. On the superuser
        // connection, which row-level security does not apply to — no policed session could answer
        // this question about another owner's rows.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountKeyedRowsAsync(admin, "budgets", "user_id", other.UserId))
            .IsEqualTo(1L);
        await Assert.That(await CountKeyedRowsAsync(admin, "budgets", "user_id", session.UserId))
            .IsEqualTo(2L);
    }

    [Test]
    public async Task Database_RefusesToReadAUserWhenTheSessionNamesNoUser()
    {
        // Arrange — a bare app-role connection: no set_config, so the session declares no user. This
        // is the shape of every bug where application code forgets to set one, and it must fail
        // loudly rather than quietly returning an empty result that reads as "no such account".
        await using RepositoryTestHost host = await StartHostAsync();
        (RepositoryTestHost.SeededOwner session, _) = await SeedTwoOwnersAsync(host);
        await using NpgsqlConnection bare = new(host.AppConnectionString);
        await bare.OpenAsync();

        // Act — users is seeded, and that is a precondition rather than a convenience. A policy qual
        // is only evaluated when there are candidate rows, so the same query over an empty table
        // returns zero rows without ever touching the setting and this guarantee does not reach it.
        // That is the honest limit of what this test proves.
        await using NpgsqlCommand read = new("select count(*) from users", bare);
        PostgresException? refusal = await CaptureRefusalAsync(read);

        // Assert — 22P02, not "unrecognized configuration parameter", for the same reason as the
        // budget-less session above. The determinism comes from the shape of the policy, which reads
        // the setting as COALESCE(current_setting('app.current_user_id', true), '')::uuid. Strict
        // current_setting would be 42704 on a backend that has never seen the setting and 22P02 on
        // one Npgsql had already recycled, which is not a thing a test can assert; the missing_ok
        // overload turns the first case into NULL and the COALESCE turns that NULL into the same
        // ''::uuid cast the recycled connection already produced. One bug, one SQLSTATE.
        await Assert.That(refusal?.SqlState ?? "no error")
            .IsEqualTo(PostgresErrorCodes.InvalidTextRepresentation);

        // The session's own row is still there — the refusal above is the session's doing, not a
        // seeding failure that would make the SQLSTATE assertion meaningless.
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountKeyedRowsAsync(admin, "users", "id", session.UserId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_ShowsOnlyTheSignedInUsersSessionRows()
    {
        // Arrange — two owners with one session each, because sessions is isolated by user: a second
        // session under the same owner would be invisible to this rule, and the foreign count would
        // come back zero with or without a policy.
        await using RepositoryTestHost host = await StartHostAsync();
        (RepositoryTestHost.SeededOwner session, RepositoryTestHost.SeededOwner other) =
            await SeedTwoOwnersAsync(host);
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedSessionAsync(admin, session.UserId);
        await SeedSessionAsync(admin, other.UserId);
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(session.UserId);

        // Act — both halves, on one session. The own count is not decoration: a policy that hides
        // every row from everyone satisfies the foreign half on its own, and only this notices.
        long own = await CountKeyedRowsAsync(app, "sessions", "user_id", session.UserId);
        long foreign = await CountKeyedRowsAsync(app, "sessions", "user_id", other.UserId);

        // Assert
        await Assert.That(own).IsEqualTo(1L);
        await Assert.That(foreign).IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesToInsertASessionForAnotherUser()
    {
        // Arrange — the probe below names the other owner's own credential, so the composite foreign
        // key on (credential_id, user_id) is satisfied by construction and the only thing wrong with
        // the row is whose session it is.
        await using RepositoryTestHost host = await StartHostAsync();
        (RepositoryTestHost.SeededOwner session, RepositoryTestHost.SeededOwner other) =
            await SeedTwoOwnersAsync(host);
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid ownCredentialId = await ReadCredentialIdAsync(admin, session.UserId);
        Guid otherCredentialId = await ReadCredentialIdAsync(admin, other.UserId);
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(session.UserId);

        // Act — the WITH CHECK half, which is the only half a SELECT cannot reach: hiding another
        // owner's sessions says nothing about whether this session can establish one in their name,
        // and a USING-only policy would let this through. A refused INSERT is loud, unlike a filtered
        // UPDATE — 42501, "new row violates row-level security policy".
        await using NpgsqlCommand forOther = BuildSessionInsertProbe(
            app, other.UserId, otherCredentialId);
        PostgresException? refusal = await CaptureRefusalAsync(forOther);

        await using NpgsqlCommand forOwn = BuildSessionInsertProbe(
            app, session.UserId, ownCredentialId);
        int inserted = await forOwn.ExecuteNonQueryAsync();

        // Assert — the null coalesce is for the failure message: a bare refusal?.SqlState renders a
        // statement that went through as the empty string, which reads as a blank SQLSTATE rather
        // than as no exception at all.
        await Assert.That(refusal?.SqlState ?? "no error")
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(inserted).IsEqualTo(1);

        // And nothing landed. A SQLSTATE says the statement was rejected; only this says the other
        // owner still has no session at all. On the superuser connection, which row-level security
        // does not apply to — no policed session could answer this question about another owner.
        await Assert.That(await CountKeyedRowsAsync(admin, "sessions", "user_id", other.UserId))
            .IsEqualTo(0L);
        await Assert.That(await CountKeyedRowsAsync(admin, "sessions", "user_id", session.UserId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_RefusesToRevokeAnotherUsersSession()
    {
        // Arrange — one live session for each owner, because the foreign half of this measurement is
        // a count of rows that were there to be touched: against an owner with no session at all,
        // "affected zero rows" is true with or without a policy.
        await using RepositoryTestHost host = await StartHostAsync();
        (RepositoryTestHost.SeededOwner session, RepositoryTestHost.SeededOwner other) =
            await SeedTwoOwnersAsync(host);
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedSessionAsync(admin, session.UserId);
        await SeedSessionAsync(admin, other.UserId);
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(session.UserId);

        // Act — the UPDATE half of the policy, and the half whose failure mode is silent. An INSERT
        // across the boundary raises 42501 and an unpoliced read returns visibly wrong rows, but
        // row-level security narrows an UPDATE by filtering it: a statement reaching another owner's
        // session is not refused, it simply matches nothing, and the only observable difference
        // between "the policy stopped me" and "the policy is gone and I rewrote their row" is the
        // count. revoked_at_utc is the one column the role's UPDATE grant reaches, so the grant
        // matrix cannot be what stops this and the policy is the only thing being measured.
        int foreignRevoked = await RevokeSessionsOfAsync(app, other.UserId);

        // The paired positive, on the same connection and the same statement shape. Without it a
        // policy of USING (false) — or a grant that had quietly lost the column — satisfies the
        // assertion above on its own.
        int ownRevoked = await RevokeSessionsOfAsync(app, session.UserId);

        // Assert — this is what measures the claim SessionRepository.RevokeForCredentialAsync makes
        // by omission: it takes a credential id from outside and narrows on it alone, with no check
        // in application code that the credential belongs to whoever is asking. The database is the
        // only thing standing between that call and one person ending another's sessions, and the
        // read-back on the superuser connection is what says the row is genuinely untouched rather
        // than merely unreported.
        await Assert.That(foreignRevoked).IsEqualTo(0);
        await Assert.That(ownRevoked).IsEqualTo(1);
        await Assert.That(await ReadRevocationOfAsync(admin, other.UserId)).IsEqualTo(DBNull.Value);
        await Assert.That(await ReadRevocationOfAsync(admin, session.UserId))
            .IsEqualTo(RevocationInstant);
    }

    [Test]
    public async Task Database_RefusesToReadSessionsWhenTheConnectionNamesNoUser()
    {
        // Arrange — a bare app-role connection: no set_config, so the session declares no user. This
        // is the shape of every bug where application code forgets to set one, and it must fail
        // loudly rather than quietly returning an empty result that reads as "signed out everywhere".
        await using RepositoryTestHost host = await StartHostAsync();
        (RepositoryTestHost.SeededOwner session, _) = await SeedTwoOwnersAsync(host);
        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await SeedSessionAsync(admin, session.UserId);
        await using NpgsqlConnection bare = new(host.AppConnectionString);
        await bare.OpenAsync();

        // Act — sessions is seeded, and that is a precondition rather than a convenience. A policy
        // qual is only evaluated when there are candidate rows, so the same query over an empty table
        // returns zero rows without ever touching the setting and this guarantee does not reach it.
        // That is the honest limit of what this test proves.
        await using NpgsqlCommand read = new("select count(*) from sessions", bare);
        PostgresException? refusal = await CaptureRefusalAsync(read);

        // Assert — 22P02, the same failure the other policed tables pin, and for the same reason: the
        // policy reads the setting as COALESCE(current_setting('app.current_user_id', true), '')::uuid,
        // so an unset setting reaches it as the ''::uuid cast. One bug, one SQLSTATE.
        await Assert.That(refusal?.SqlState ?? "no error")
            .IsEqualTo(PostgresErrorCodes.InvalidTextRepresentation);

        // The session's own row is still there — the refusal above is the connection's doing, not a
        // seeding failure that would make the SQLSTATE assertion meaningless.
        await Assert.That(await CountKeyedRowsAsync(admin, "sessions", "user_id", session.UserId))
            .IsEqualTo(1L);
    }

    /// <summary>
    /// The one row of each budget-owned table that a budget was seeded with, so a probe can name
    /// "this budget's account" without every test re-deriving it.
    /// </summary>
    private sealed record BudgetRows(
        Guid BudgetId,
        Guid AccountId,
        Guid CategoryGroupId,
        Guid CategoryId,
        Guid PayeeId,
        Guid TransactionId)
    {
        /// <summary>
        /// Maps a table name to this budget's row in it. The tests iterate tables by name because
        /// that is what the SQL takes; this keeps the mapping in one place and throws rather than
        /// returning a default for a name nobody seeded.
        /// </summary>
        public Guid RowIn(string table) => table switch
        {
            "accounts" => AccountId,
            "category_groups" => CategoryGroupId,
            "categories" => CategoryId,
            "payees" => PayeeId,
            "transactions" => TransactionId,
            _ => throw new ArgumentOutOfRangeException(
                nameof(table), table, "Not a budget-owned table these tests seed."),
        };
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Seeds one owner with two budgets, <b>both populated with the same shape of rows</b>, and
    /// returns them together with the owner: the budget every probe session declares, and the budget
    /// every probe tries to reach across into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Populating both is what separates this helper from
    /// <c>TenancySchemaTests.SeedTwoBudgetsAsync</c>, which deliberately leaves its second budget
    /// empty. Isolation cannot be measured against an empty tenant: every "sees nothing" and every
    /// "affected zero rows" would be true because there was nothing there, with or without a policy.
    /// </para>
    /// <para>
    /// One owner, on purpose: these probes are about the budget axis, and a session whose user owns
    /// both budgets is the strictest form of the question — the only thing that can separate the two
    /// tenants is <c>budget_id</c>. The user axis has <see cref="SeedTwoOwnersAsync" />, which needs
    /// two owners for the mirrored reason.
    /// </para>
    /// <para>
    /// Names repeat across the two budgets on purpose — the unique indexes are on
    /// <c>(budget_id, name)</c>, so identical names are legal and make the two tenants genuinely
    /// indistinguishable except by <c>budget_id</c>. The budget names themselves differ, against
    /// <c>IX_budgets_user_id_name</c>.
    /// </para>
    /// <para>
    /// One seeding context writes both budgets. That is safe because the <c>BudgetIsolation</c>
    /// query filters are read-side only and the domain factories take <c>budgetId</c> explicitly, so
    /// the context's own ambient budget never reaches an INSERT.
    /// </para>
    /// </remarks>
    private static async Task<(Guid OwnerId, BudgetRows Ambient, BudgetRows Other)>
        SeedTwoPopulatedBudgetsAsync(RepositoryTestHost host)
    {
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid ambientBudgetId = await host.SeedAdditionalBudgetAsync(userId, "Household");
        Guid otherBudgetId = await host.SeedAdditionalBudgetAsync(userId, "Holiday Fund");

        await using BudgetoidDbContext seed = CreateDb(host, ambientBudgetId);
        BudgetRows ambient = AddRows(seed, ambientBudgetId);
        BudgetRows other = AddRows(seed, otherBudgetId);
        await seed.SaveChangesAsync();
        return (userId, ambient, other);
    }

    /// <summary>
    /// Seeds <b>two</b> owners, each with the default budget provisioning gives them, and returns
    /// both: the owner every probe session declares, and the owner every probe tries to reach across
    /// into.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SeedTwoPopulatedBudgetsAsync" /> rather than an extension of it. The
    /// <c>users</c> and <c>budgets</c> probes are isolated by user, so a second budget under the
    /// same owner is not a second tenant to them at all — every "sees nothing" would be true because
    /// there was only ever one owner, with or without a policy. Both owners are seeded with the same
    /// nameless default budget, which is legal because <c>IX_budgets_user_id_name</c> keys on
    /// <c>user_id</c> too.
    /// </remarks>
    private static async Task<(RepositoryTestHost.SeededOwner Session, RepositoryTestHost.SeededOwner Other)>
        SeedTwoOwnersAsync(RepositoryTestHost host)
    {
        RepositoryTestHost.SeededOwner session =
            await host.SeedOwnerAsync("google-1", "person@example.com");
        RepositoryTestHost.SeededOwner other =
            await host.SeedOwnerAsync("google-2", "other@example.com");
        return (session, other);
    }

    /// <summary>
    /// Adds one account, one category group, one category in that group, one payee and one
    /// transaction on that account to <paramref name="budgetId" />, and returns their ids.
    /// </summary>
    private static BudgetRows AddRows(BudgetoidDbContext seed, Guid budgetId)
    {
        Account account = Account.Create(
            budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant);
        CategoryGroup group = CategoryGroup.Create(budgetId, "Everyday", null, 0, SeedInstant);
        Category category = Category.Create(budgetId, group.Id, "Groceries", null, 0, SeedInstant);
        Payee payee = Payee.Create(budgetId, "Corner Shop", SeedInstant);
        Transaction transaction = Transaction.Create(
            budgetId,
            account.Id,
            -10m,
            UsdMinorUnit,
            new DateOnly(2026, 6, 12),
            "Weekly shop",
            SeedInstant);

        seed.Accounts.Add(account);
        seed.CategoryGroups.Add(group);
        seed.Categories.Add(category);
        seed.Payees.Add(payee);
        seed.Transactions.Add(transaction);

        return new BudgetRows(
            budgetId, account.Id, group.Id, category.Id, payee.Id, transaction.Id);
    }

    /// <summary>
    /// The expiry every seeded session carries. Strictly after <see cref="SeedInstant" />, which is
    /// the whole content of <c>CK_sessions_lifetime</c>.
    /// </summary>
    private static readonly DateTime SessionExpiryInstant =
        new(2026, 6, 13, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Reads back the id of the federated credential <see cref="RepositoryTestHost.SeedOwnerAsync" />
    /// gave an owner. Every session names one, and a probe that named somebody else's would trip the
    /// composite foreign key before any policy was consulted.
    /// </summary>
    private static async Task<Guid> ReadCredentialIdAsync(NpgsqlConnection connection, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select id from credentials where user_id = @id",
            connection);
        command.Parameters.AddWithValue("id", userId);
        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"Expected one credential id, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The instant a revocation probe writes. Distinct from every other constant here, so a
    /// read-back cannot pass on a column nobody wrote.
    /// </summary>
    private static readonly DateTime RevocationInstant =
        new(2026, 6, 12, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Ends every session of one owner and returns the affected-row count. Keyed on
    /// <c>user_id</c> rather than on a session id because that is the shape of the statement the
    /// policy has to narrow, and the count is the whole measurement: a filtered UPDATE raises
    /// nothing.
    /// </summary>
    private static async Task<int> RevokeSessionsOfAsync(NpgsqlConnection connection, Guid ownerId)
    {
        await using NpgsqlCommand command = new(
            "update sessions set revoked_at_utc = @value where user_id = @id",
            connection);
        command.Parameters.AddWithValue("value", RevocationInstant);
        command.Parameters.AddWithValue("id", ownerId);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Reads one owner's session revocation instant back. On the superuser connection, which
    /// row-level security does not apply to — no policed session could answer this about another
    /// owner, which is the whole reason the question is worth asking. A SQL NULL comes back as
    /// <see cref="DBNull.Value" />.
    /// </summary>
    private static async Task<object?> ReadRevocationOfAsync(
        NpgsqlConnection connection,
        Guid ownerId)
    {
        await using NpgsqlCommand command = new(
            "select revoked_at_utc from sessions where user_id = @id",
            connection);
        command.Parameters.AddWithValue("id", ownerId);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>
    /// Writes one live session for an owner on the superuser connection, which row-level security
    /// does not apply to — the seeding is a precondition of these probes rather than one of them.
    /// </summary>
    private static async Task SeedSessionAsync(NpgsqlConnection connection, Guid userId)
    {
        Guid credentialId = await ReadCredentialIdAsync(connection, userId);
        await using NpgsqlCommand command = BuildSessionInsertProbe(connection, userId, credentialId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Builds the INSERT probe for <c>sessions</c>, owned by <paramref name="ownerId" /> and
    /// established by <paramref name="credentialId" />. Separate from
    /// <see cref="BuildInsertProbe" /> because a session names no budget: it is user-owned, so the
    /// column a policy could object to is <c>user_id</c>.
    /// </summary>
    /// <remarks>
    /// <c>('federated', 'locked')</c> because every owner seeded here has a federated credential and
    /// nothing else, and <c>CK_sessions_kind_matches_credential</c> refuses a full session opened by
    /// one. These probes are about the policy rather than about the kind, so the cheapest row the
    /// schema accepts is the right one: a row refused by a CHECK would never reach the policy at
    /// all, and the refusal being read would be the wrong one.
    /// </remarks>
    private static NpgsqlCommand BuildSessionInsertProbe(
        NpgsqlConnection connection,
        Guid ownerId,
        Guid credentialId)
    {
        NpgsqlCommand command = new(
            "insert into sessions " +
            "(id, user_id, credential_id, credential_type, kind, " +
            "created_at_utc, expires_at_utc, revoked_at_utc) " +
            "values (@id, @user_id, @credential_id, 'federated', 'locked', " +
            "@created_at_utc, @expires_at_utc, null)",
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("user_id", ownerId);
        command.Parameters.AddWithValue("credential_id", credentialId);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        command.Parameters.AddWithValue("expires_at_utc", SessionExpiryInstant);
        return command;
    }

    /// <summary>
    /// Builds the INSERT probe for one table, aimed at <paramref name="target" />'s budget and using
    /// <paramref name="target" />'s own rows as parents. Taking the whole
    /// <see cref="BudgetRows" /> rather than a bare budget id is the point: the composite references
    /// — <c>transactions → accounts (id, budget_id)</c> and
    /// <c>categories → category_groups (id, budget_id)</c> — are then satisfied by construction, so
    /// the budget named is the only thing a policy could object to.
    /// </summary>
    private static NpgsqlCommand BuildInsertProbe(
        NpgsqlConnection connection,
        string table,
        BudgetRows target)
    {
        // Distinct from every seeded name, so the case-insensitive unique index on
        // (budget_id, name) never turns a probe into a 23505 about something else.
        const string probeName = "Inserted by an isolation probe";

        // 'USD' is a real currencies row seeded by the migration and 'Checking' satisfies
        // CK_accounts_type; the transaction's date and amount are literals because neither is what
        // any of this is about.
        (string sql, Guid? parentId) = table switch
        {
            "accounts" => (
                "insert into accounts (id, budget_id, name, type, opening_balance, currency_code, created_at_utc) " +
                "values (@id, @budget_id, @name, 'Checking', 0, 'USD', @created_at_utc)",
                (Guid?)null),
            "category_groups" => (
                "insert into category_groups (id, budget_id, name, description, position, created_at_utc) " +
                "values (@id, @budget_id, @name, null, 1, @created_at_utc)",
                null),
            "categories" => (
                "insert into categories (id, budget_id, category_group_id, name, description, position, created_at_utc) " +
                "values (@id, @budget_id, @parent_id, @name, null, 1, @created_at_utc)",
                target.CategoryGroupId),
            "payees" => (
                "insert into payees (id, budget_id, name, created_at_utc) " +
                "values (@id, @budget_id, @name, @created_at_utc)",
                null),
            "transactions" => (
                "insert into transactions (id, budget_id, account_id, amount, date, description, created_at_utc) " +
                "values (@id, @budget_id, @parent_id, -5, date '2026-06-12', @name, @created_at_utc)",
                target.AccountId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(table), table, "Not a budget-owned table these tests seed."),
        };

        NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("budget_id", target.BudgetId);
        command.Parameters.AddWithValue("name", probeName);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);

        if (parentId is { } parent)
        {
            command.Parameters.AddWithValue("parent_id", parent);
        }

        return command;
    }

    /// <summary>
    /// Builds the INSERT probe for <c>budgets</c>, owned by <paramref name="ownerId" />. Separate
    /// from <see cref="BuildInsertProbe" /> because a budget names no budget: it is the tenant, so
    /// the column a policy could object to is <c>user_id</c>.
    /// </summary>
    /// <remarks>
    /// The name is explicit and unlike anything seeded, and that is load-bearing rather than tidy.
    /// <c>IX_budgets_user_id_name</c> is unique on <c>(user_id, name)</c> with <c>NULLS NOT
    /// DISTINCT</c>, and every seeded budget here is the nameless default — so a probe that left the
    /// name null would collide with the owner's own default budget and be refused with <c>23505</c>,
    /// which is not the refusal this test is reading. <c>base_currency_code</c> is left off the
    /// column list because it is nullable and naming a currency here would only add a foreign key
    /// that could fail for its own reasons.
    /// </remarks>
    private static NpgsqlCommand BuildBudgetInsertProbe(NpgsqlConnection connection, Guid ownerId)
    {
        NpgsqlCommand command = new(
            "insert into budgets (id, user_id, name, created_at_utc) " +
            "values (@id, @user_id, @name, @created_at_utc)",
            connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("user_id", ownerId);
        command.Parameters.AddWithValue("name", "Inserted by a user isolation probe");
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        return command;
    }

    /// <summary>
    /// Runs a statement that is expected to be refused and returns the refusal, or
    /// <see langword="null" /> when it went through instead.
    /// </summary>
    /// <remarks>
    /// Null rather than a thrown "expected an exception": the caller asserts on
    /// <c>refusal?.SqlState</c>, so a statement that succeeded is reported as the SQLSTATE that
    /// failed to arrive — the thing the test is actually about — instead of as an unrelated
    /// exception type escaping the act phase.
    /// </remarks>
    private static async Task<PostgresException?> CaptureRefusalAsync(NpgsqlCommand command)
    {
        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Writes one granted text column of one row and returns the affected-row count. Never
    /// <c>budget_id</c> — see the class remarks for why that column would make the probe vacuous.
    /// </summary>
    private static async Task<int> UpdateTextAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        Guid rowId,
        string value)
    {
        // Table and column are interpolated because every call site passes them from the literal
        // arrays above; the values are parameters, as they must be.
        await using NpgsqlCommand command = new(
            $"update {table} set {column} = @value where id = @id",
            connection);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("id", rowId);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> DeleteAsync(NpgsqlConnection connection, string table, Guid rowId)
    {
        await using NpgsqlCommand command = new($"delete from {table} where id = @id", connection);
        command.Parameters.AddWithValue("id", rowId);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Reads one column of one row back, so a refusal can be asserted as "nothing changed". Called
    /// on the superuser connection, which row-level security does not apply to — the app role
    /// cannot answer this question about another budget's row by definition. A SQL NULL comes back
    /// as <see cref="DBNull.Value" />; a missing row comes back as <see langword="null" />.
    /// </summary>
    private static async Task<object?> ReadColumnAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        Guid rowId)
    {
        await using NpgsqlCommand command = new(
            $"select {column} from {table} where id = @id",
            connection);
        command.Parameters.AddWithValue("id", rowId);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>
    /// Counts a budget's rows in one table. On the app connection this measures what the session can
    /// see; on the superuser connection it measures what is actually there.
    /// </summary>
    private static Task<long> CountRowsAsync(
        NpgsqlConnection connection,
        string table,
        Guid budgetId) =>
        CountKeyedRowsAsync(connection, table, "budget_id", budgetId);

    /// <summary>
    /// The same count keyed on whichever column identifies the tenant. <c>users</c> and
    /// <c>budgets</c> have no <c>budget_id</c> to filter on — a user is reached by <c>id</c> and a
    /// budget by its owner's <c>user_id</c> — and widening <see cref="CountRowsAsync" /> instead
    /// would let a budget-owned call site quietly pass the wrong column.
    /// </summary>
    private static async Task<long> CountKeyedRowsAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        Guid id)
    {
        await using NpgsqlCommand command = new(
            $"select count(*) from {table} where {column} = @id",
            connection);
        command.Parameters.AddWithValue("id", id);

        // Pattern-matched rather than cast-and-null-forgive: a null or unexpected scalar means the
        // query changed shape, and that should fail loudly here instead of at the assertion.
        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{table}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Builds a context bound to an ambient budget, which the budget-isolated sets this file seeds
    /// through all require.
    /// </summary>
    private static BudgetoidDbContext CreateDb(RepositoryTestHost host, Guid budgetId) => new(
        new DbContextOptionsBuilder<BudgetoidDbContext>()
            .UseNpgsql(host.ConnectionString)
            .Options,
        new TestBudgetContext(budgetId));

    private static async Task<RepositoryTestHost> StartHostAsync()
    {
        RepositoryTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
