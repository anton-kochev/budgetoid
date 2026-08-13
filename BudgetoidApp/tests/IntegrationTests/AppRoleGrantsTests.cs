using Domain.Accounts;
using Domain.Categories;
using Domain.CategoryGroups;
using Domain.Payees;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// Covers the immutability rules that only the application role's column grants can enforce: a
/// budgets row is never updated at all, an account's currency never changes, a session's identity —
/// who it belongs to, which credential opened it, how much it reaches and when it runs out — is
/// written whole when the session is established and only revoked_at_utc can ever be edited, and a
/// credential — the row the whole sign-in resolves through — has an immutable identity: user_id,
/// type, provider, subject and created_at_utc are written whole at registration and have no edit
/// that means anything. Today those five are every column credentials has, which is why the role holds
/// no <c>UPDATE</c> grant on that table of any shape rather than a column list with nothing on
/// it — read that as where the list stands today, not as a property of the table. The role's
/// <c>UPDATE</c> grant names its columns explicitly, and PostgreSQL column privileges are additive,
/// so an immutable column is one that is simply absent from the list; writing it fails with
/// <c>42501</c> before the row is touched. Every statement here is raw Npgsql on
/// <see cref="RepositoryTestHost.AppConnectionString" />, because grants only bind connections
/// opened as the role — the host's own connection is the container superuser and answers every
/// privilege question with yes. What each test then puts on that session is not uniform and is
/// never incidental: a test aiming at a policed row declares an identity so the row is reachable at
/// all, and a test aiming at a table row-level security exempts declares nothing — the two
/// <c>credentials</c> tests, the two <c>passkey_public_keys</c> tests, the two
/// <c>recovery_code_hashes</c> tests and the <c>webauthn_challenges</c> one — which is the
/// measurement rather than a gap.
/// </summary>
/// <remarks>
/// <para>
/// Each refusal is paired, in the same test, with a write that must succeed on the same
/// connection. Without the pair the <c>42501</c> is vacuous: a role with no <c>UPDATE</c> grant
/// at all — or a grants script that is an empty file — refuses everything with the same SQLSTATE.
/// The success half is what pins "exactly this column is immutable" rather than "the role cannot
/// write". On <c>budgets</c> no column is updatable — that is the whole content of that rule. On
/// <c>credentials</c> none is updatable today, not because the table is closed to writes but
/// because the identity columns happen to be all the columns there are. Either way the pair is a
/// permitted <c>INSERT</c> instead: provisioning creates budgets and sign-up creates credentials,
/// and the role must still be able to. That stays the right pairing when an updatable column joins
/// credentials — the <c>INSERT</c> is still the success the refusals need beside them.
/// </para>
/// <para>
/// A missing grant is not the only way the success half can become unreachable, and the other way
/// is the quiet one. Row-level security decides which rows exist for a session before any grant is
/// consulted, and a session the policy cannot satisfy is not refused — it simply matches nothing.
/// The permitted write then reports success against zero rows, the refusals beside it stay red for
/// a reason nobody measured, and the test passes having proved only "the role cannot write", which
/// is precisely the claim the pairing exists to rule out. So every test aiming at a policed row
/// configures the session it sends on, and asserts an affected count of <c>1</c> rather than the
/// absence of an exception. Each test argues this locally about its own table; it is stated here
/// because a new test added to this class inherits the trap, not the argument.
/// </para>
/// <para>
/// <c>credentials</c> is the exception, and its bare connection is load-bearing rather than an
/// omission someone should tidy. That table is permanently exempt from row-level
/// security because it is what the sign-in path reads to discover who is asking, so a policy keyed
/// on the identity it resolves would refuse the query that resolves it. The anonymous session in
/// that test is the executable form of the exemption: it is the statement that the table is
/// reachable with no identity on the session at all, which is the property the entire sign-in path
/// stands on. Give that connection a user and the property is checked nowhere.
/// </para>
/// </remarks>
public sealed class AppRoleGrantsTests
{
    /// <summary>
    /// Minor unit of the USD account these tests seed. Precision is not what any of them is
    /// about; the constant keeps a bare <c>2</c> from reading as a rule.
    /// </summary>
    private const int UsdMinorUnit = 2;

    [Test]
    public async Task Database_RefusesEveryUpdateOnABudget_WhileStillAllowingInsert()
    {
        // Arrange — one budget, plus a second real user for the user_id statement below to aim
        // at: if the grant ever leaked user_id, the reassignment would then succeed outright
        // instead of tripping the users foreign key and passing for the wrong reason.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");
        Guid budgetId = await host.SeedAdditionalBudgetAsync(userId, "Household");

        // A user-only app-role session, not RepositoryTestHost.OpenAppConnectionAsync: budgets is
        // policed on the user — a budget is the tenant, not a tenant's row — so the session names
        // the owner and no ambient budget. The identity is what keeps the pair below intact: on a
        // session with no user id the INSERT would fail its WITH CHECK instead of landing, and a
        // refusal with no permitted write beside it proves nothing (see the class remarks). With
        // the row reachable, only the column grant decides, which is what this test measures.
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Act — every column of budgets by name: name, user_id, base_currency_code. The rule is
        // "a budgets row is never updated", and column-for-column is the only shape the grant
        // list can hold that in. USD is a real currencies row, for the same leak-detection reason
        // as the second user.
        PostgresException nameRefusal = await ThrowsPostgresExceptionAsync(
            app, "update budgets set name = @value where id = @id", "Renamed", budgetId);
        PostgresException userRefusal = await ThrowsPostgresExceptionAsync(
            app, "update budgets set user_id = @value where id = @id", otherUserId, budgetId);
        PostgresException currencyRefusal = await ThrowsPostgresExceptionAsync(
            app, "update budgets set base_currency_code = @value where id = @id", "USD", budgetId);

        // Assert
        await Assert.That(nameRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(userRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(currencyRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await SelectScalarAsync(
                admin, "select name from budgets where id = @id", budgetId))
            .IsEqualTo("Household");
        await Assert.That(await SelectScalarAsync(
                admin, "select user_id from budgets where id = @id", budgetId))
            .IsEqualTo(userId);
        await Assert.That(await SelectScalarAsync(
                admin, "select base_currency_code from budgets where id = @id", budgetId))
            .IsEqualTo(DBNull.Value);

        // The success half of the pair (see the class remarks) — an INSERT, because budgets is
        // the one table where no UPDATE column exists to pair with.
        await using NpgsqlCommand insert = new(
            "insert into budgets (id, user_id, name, created_at_utc) " +
            "values (@id, @user_id, @name, @created_at_utc)",
            app);
        insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("name", "Holiday Fund");
        insert.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await Assert.That(await insert.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task Database_RefusesToChangeAnAccountsCurrency_WhileStillAllowingRename()
    {
        // Arrange — a USD account. EUR is a real currencies row seeded by the migration, so if
        // the grant ever leaked currency_code the statement would succeed outright instead of
        // tripping the currency foreign key and passing for the wrong reason.
        await using RepositoryTestHost host = await StartHostAsync();
        // SeedOwnerAsync rather than SeedBudgetAsync: the app-role connection below names the user
        // as well as the budget, and this is the seeding call that returns both.
        RepositoryTestHost.SeededOwner owner =
            await host.SeedOwnerAsync("google-1", "person@example.com");
        Guid budgetId = owner.BudgetId;
        Guid accountId;
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            Account account = Account.Create(
                budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant);
            seed.Accounts.Add(account);
            await seed.SaveChangesAsync();
            accountId = account.Id;
        }

        // accounts is row-level-security scoped, so this connection carries the budget the account
        // is in. Without it the rename would match zero rows and still report no error, which would
        // leave the refusal it is paired with proving nothing.
        await using NpgsqlConnection app = await host.OpenAppConnectionAsync(owner.UserId, budgetId);

        // Act — currency_code is absent from the accounts grant list; name is on it. Same table,
        // same row, same connection: only the column decides.
        PostgresException refusal = await ThrowsPostgresExceptionAsync(
            app, "update accounts set currency_code = @value where id = @id", "EUR", accountId);
        int renamed = await ExecuteAsync(
            app, "update accounts set name = @value where id = @id", "Everyday Checking", accountId);

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(renamed).IsEqualTo(1);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await SelectScalarAsync(
                admin, "select currency_code from accounts where id = @id", accountId))
            .IsEqualTo("USD");
        await Assert.That(await SelectScalarAsync(
                admin, "select name from accounts where id = @id", accountId))
            .IsEqualTo("Everyday Checking");
    }

    [Test]
    public async Task Database_RefusesToChangeAUsersCreatedAt_WhileStillAllowingProfileEdits()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");

        // A user-only app-role session, for the same reason as the budgets test: users is policed
        // on the user — a user owns budgets rather than belonging to one — so the session names the
        // owner and no ambient budget. The identity is also what keeps the pair intact: without it
        // the email edit would match zero rows and report success, leaving the refusal it is paired
        // with vacuous. With the row reachable, only the column grant decides.
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Act — created_at_utc is an audit fact and immutable by omission. With the identity
        // columns gone from this table it is the only omitted column left, which makes it the one
        // statement that can still tell a real GRANT UPDATE (email) list apart from a table-wide
        // grant: a table-wide grant would let it through. email is the only column on that list,
        // and editing it is what rules out the other way this test could pass — the role holding
        // no UPDATE on users at all.
        PostgresException refusal = await ThrowsPostgresExceptionAsync(
            app, "update users set created_at_utc = @value where id = @id", ForgedInstant, userId);
        int profileEdited = await ExecuteAsync(
            app, "update users set email = @value where id = @id", "edited@example.com", userId);

        // Assert
        await Assert.That(refusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(profileEdited).IsEqualTo(1);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await SelectScalarAsync(
                admin, "select created_at_utc from users where id = @id", userId))
            .IsEqualTo(SeedInstant);
        await Assert.That(await SelectScalarAsync(
                admin, "select email from users where id = @id", userId))
            .IsEqualTo("edited@example.com");
    }

    [Test]
    public async Task Database_AllowsDeletingAUserAndCascadesTheAccountAway()
    {
        // Arrange — one complete account: the user and its default budget, its federated credential,
        // a second passkey credential with the public key and signature counter that hang off it, one
        // session, and one row in each budget-owned table the cascade has to reach.
        await using RepositoryTestHost host = await StartHostAsync();
        RepositoryTestHost.SeededOwner owner =
            await host.SeedOwnerAsync("google-1", "person@example.com");
        Guid userId = owner.UserId;
        Guid budgetId = owner.BudgetId;
        await host.SeedPasskeyAsync(userId, SeededHandle, SeededCoseKey);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid federatedCredentialId = (Guid)(await SelectScalarAsync(
            admin,
            "select id from credentials where user_id = @id and type = 'federated'",
            userId))!;
        await InsertSessionAsync(admin, userId, federatedCredentialId);

        // No transaction is seeded, and the omission is the measurement rather than forgetfulness.
        // budgets → transactions is DeleteBehavior.Restrict, so one transaction would stop this
        // cascade with a foreign-key violation instead of a privilege answer. That restriction is a
        // different rule owned by a later story; this test must not collide with it. Do not "complete"
        // the seeding by adding one.
        await using (BudgetoidDbContext seed = CreateDb(host, budgetId))
        {
            seed.Payees.Add(Payee.Create(budgetId, "Corner Shop", SeedInstant));
            seed.Accounts.Add(Account.Create(
                budgetId, "Checking", AccountType.Checking, 0m, "USD", UsdMinorUnit, SeedInstant));
            CategoryGroup categoryGroup =
                CategoryGroup.Create(budgetId, "Essentials", null, 0, SeedInstant);
            seed.CategoryGroups.Add(categoryGroup);
            seed.Categories.Add(Category.Create(
                budgetId, categoryGroup.Id, "Groceries", null, 0, SeedInstant));
            await seed.SaveChangesAsync();
        }

        // users is policed on the user by user_isolation, so the session names the owner and no
        // ambient budget. An unconfigured connection would not reach the grant at all: the policy
        // reads app.current_user_id as ''::uuid and raises 22P02, which is not the thing under test.
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Every count below the DELETE is "count == 0", and a table that was never seeded satisfies
        // that trivially — so the zeros mean nothing unless the rows were there first. That is the
        // same vacuous-green hazard this test already guards against on the cascade side by scoping
        // each count to this account, reproduced one step earlier on the seeding side: scoping stops
        // an empty database from passing, it does not stop an empty *table* from passing. The hazard
        // is live, not theoretical — SeedPasskeyAsync takes signatureCounter = 0 by default, so a
        // future "only write the counter when it is non-zero" would leave passkey_signature_counters
        // unseeded and its cascade claim asserted by nothing. Reading the same counts here, on the
        // same admin connection and with the same predicates, is what makes each zero a change.
        async Task AssertSeededAsync(string sql, Guid rowId, long expected) =>
            await Assert.That(await SelectScalarAsync(admin, sql, rowId)).IsEqualTo(expected);

        await AssertSeededAsync("select count(*) from users where id = @id", userId, 1L);
        await AssertSeededAsync("select count(*) from budgets where user_id = @id", userId, 1L);

        // Two credentials, and the pair is the point: SeedOwnerAsync writes the federated one and
        // SeedPasskeyAsync the passkey the key and counter hang off. An exact count says both are
        // there, where a non-zero check would pass on either alone.
        await AssertSeededAsync("select count(*) from credentials where user_id = @id", userId, 2L);
        await AssertSeededAsync("select count(*) from sessions where user_id = @id", userId, 1L);
        await AssertSeededAsync(
            "select count(*) from passkey_public_keys where user_id = @id", userId, 1L);
        await AssertSeededAsync(
            "select count(*) from passkey_signature_counters where user_id = @id", userId, 1L);
        await AssertSeededAsync("select count(*) from payees where budget_id = @id", budgetId, 1L);
        await AssertSeededAsync("select count(*) from accounts where budget_id = @id", budgetId, 1L);
        await AssertSeededAsync(
            "select count(*) from category_groups where budget_id = @id", budgetId, 1L);
        await AssertSeededAsync(
            "select count(*) from categories where budget_id = @id", budgetId, 1L);

        // Act — the DELETE is both halves of this file's pairing at once (see the class remarks): it
        // is the permitted write, and its affected count of 1 is what says row-level security really
        // matched the row rather than the statement succeeding against nothing.
        int deleted = await ExecuteAsync(app, "delete from users where id = @id", userId);

        // Assert — the parent is gone and, with it, every owned row, on a role that holds DELETE on
        // users and on no other owned table. That is the whole claim: PostgreSQL runs ON DELETE
        // CASCADE through referential-integrity triggers that execute with the privileges of the
        // referencing table's owner, not of the current role, so the cascade reaches budgets,
        // credentials, sessions, passkey_public_keys, passkey_signature_counters, payees, accounts,
        // category_groups and categories without a grant on any of them. It is why closing an account
        // needs one grant rather than seven. Each count is scoped to this account rather than to the
        // whole table — a global count would go green on an empty database and prove nothing.
        await Assert.That(deleted).IsEqualTo(1);

        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from users where id = @id", userId))
            .IsEqualTo(0L);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from budgets where user_id = @id", userId))
            .IsEqualTo(0L);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from credentials where user_id = @id", userId))
            .IsEqualTo(0L);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from sessions where user_id = @id", userId))
            .IsEqualTo(0L);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from passkey_public_keys where user_id = @id", userId))
            .IsEqualTo(0L);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from passkey_signature_counters where user_id = @id", userId))
            .IsEqualTo(0L);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from payees where budget_id = @id", budgetId))
            .IsEqualTo(0L);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from accounts where budget_id = @id", budgetId))
            .IsEqualTo(0L);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from category_groups where budget_id = @id", budgetId))
            .IsEqualTo(0L);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from categories where budget_id = @id", budgetId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_RefusesEveryUpdateOnACredentialsIdentity_WhileStillAllowingInsertAndDelete()
    {
        // Arrange — one user with the federated credential SeedUserAsync gives it, plus a second
        // real user for the user_id statement below to aim at: if the grant ever leaked user_id,
        // the reassignment would then succeed outright instead of tripping the users foreign key
        // and passing for the wrong reason.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid credentialId = (Guid)(await SelectScalarAsync(
            admin, "select id from credentials where user_id = @id", userId))!;

        // A bare app-role connection — no user, no budget, nothing on the session at all — and
        // unlike the budgets and users tests above that is the point rather than a leftover.
        // credentials is the one table row-level security deliberately and permanently exempts:
        // it is what the sign-in path reads to discover who is asking, so a policy keyed on the
        // identity it resolves would refuse the very query that resolves it. Every statement below
        // going through on an anonymous session is the executable statement of that exemption, and
        // the whole sign-in path depends on it. Do not "tidy" this into a configured connection:
        // the moment this session names a user, the exemption stops being tested anywhere.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        // Act — every identity column of credentials by name: user_id, type, provider, subject,
        // created_at_utc. The rule is "a credential's identity is written whole at registration
        // and has no edit that means anything", and column-for-column is the only shape the
        // absence of an UPDATE grant can be pinned in. Those five are every column the table has
        // today, which is why the absence covers them all; an updatable column arriving later
        // joins the list and leaves these five off it. Each statement is refused on privilege
        // before the row is reached, so none of them ever meets CK_credentials_type_shape.
        PostgresException subjectRefusal = await ThrowsPostgresExceptionAsync(
            app, "update credentials set subject = @value where id = @id", "google-2", credentialId);
        PostgresException providerRefusal = await ThrowsPostgresExceptionAsync(
            app, "update credentials set provider = @value where id = @id", "apple", credentialId);
        PostgresException typeRefusal = await ThrowsPostgresExceptionAsync(
            app, "update credentials set type = @value where id = @id", "passkey", credentialId);
        PostgresException userRefusal = await ThrowsPostgresExceptionAsync(
            app, "update credentials set user_id = @value where id = @id", otherUserId, credentialId);
        PostgresException createdAtRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update credentials set created_at_utc = @value where id = @id",
            ForgedInstant,
            credentialId);

        // Assert
        await Assert.That(subjectRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(providerRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(typeRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(userRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(createdAtRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        // The success half of the pair (see the class remarks) — an INSERT, because credentials is
        // the second table where no UPDATE column exists to pair with. One statement buys three
        // things at once: it is the privilege-layer proof that an account may hold more than one
        // credential, it is the passkey arm of CK_credentials_type_shape (no provider, no subject),
        // and it shows the unique index really is partial — two rows with NULL provider and NULL
        // subject coexist under it because its filter names only federated rows.
        Guid passkeyCredentialId = Guid.CreateVersion7();
        await using NpgsqlCommand insert = new(
            "insert into credentials (id, user_id, type, provider, subject, created_at_utc) " +
            "values (@id, @user_id, 'passkey', null, null, @created_at_utc)",
            app);
        insert.Parameters.AddWithValue("id", passkeyCredentialId);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await Assert.That(await insert.ExecuteNonQueryAsync()).IsEqualTo(1);

        await Assert.That(await SelectScalarAsync(
                admin, "select subject from credentials where id = @id", credentialId))
            .IsEqualTo("google-1");
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from credentials where user_id = @id", userId))
            .IsEqualTo(2L);

        // The second success half, and it is the one grant on this table that has no policy behind
        // it. The role now holds DELETE on credentials so a passkey can be revoked, and credentials
        // stays exempt from row-level security, so nothing in the database narrows this statement to
        // a row the caller owns — the owner-scoped read that precedes it in the application is the
        // only thing that does. See ADR 0014. The row deleted is the passkey inserted above rather
        // than the seeded federated one, so what leaves is a row this test created and the
        // arrangement the refusals above aimed at survives to be read back.
        //
        // The affected count is the assertion, not the absence of an exception: a DELETE matching
        // zero rows raises nothing at all, and on an unpoliced table there is no policy to blame for
        // the miss — so without the count this passes on a statement that removed nothing.
        int revoked = await ExecuteAsync(
            app, "delete from credentials where id = @id", passkeyCredentialId);
        await Assert.That(revoked).IsEqualTo(1);

        // And the account's other credential is still there. That is the control on the count above:
        // it says the statement removed the row it named rather than the table's contents, which is
        // the shape a grant this wide fails in.
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from credentials where id = @id", credentialId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_LetsTheAppRoleDeleteAnyCredential_OnASessionNamingNobody()
    {
        // Arrange — two accounts, each with the federated credential SeedUserAsync gives it. The
        // second one is the target: a delete of the session's own credential would be indistinguishable
        // from a correctly scoped one, and there is no session here to own anything anyway.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid otherCredentialId = (Guid)(await SelectScalarAsync(
            admin, "select id from credentials where user_id = @id", otherUserId))!;

        // A bare app-role connection — no app.current_user_id, no app.current_budget_id, nothing on
        // the session at all — and, as in the credentials test above, that is the measurement rather
        // than a leftover. credentials is permanently exempt from row-level security because it is
        // what the sign-in path reads to discover who is asking, so a policy keyed on the identity it
        // resolves would refuse the query that resolves it. Do not "tidy" this into a configured
        // connection: naming a user here would make the delete look scoped by something.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        // Act
        int deleted = await ExecuteAsync(
            app, "delete from credentials where id = @id", otherCredentialId);

        // Assert — this test asserts a hole, and it is here so that nobody mistakes the hole for an
        // accident. It is the executable form of ADR 0014's premise: DELETE on credentials is the one
        // destructive privilege this role holds that the database scopes by nothing — the grant names
        // the table and no policy names the rows — so a statement sent by a connection that has not
        // said who it is removes any account's credential. Nothing below the application narrows it;
        // what does is the owner-bearing read that produces the entity the delete is issued from, and
        // Revocation_OfAnotherAccountsCredential_IsRefusedAndRemovesNeitherAccountsRows is what
        // notices if that predicate ever leaves. Nothing here is desirable, and none of it is a
        // regression: this test goes red the day somebody succeeds in policing credentials, and that
        // is the day ADR 0014 needs rewriting rather than the day this test needs relaxing.
        //
        // The count of 1 is the assertion rather than the absence of an exception, for the reason it
        // is one test up: a DELETE matching nothing raises nothing, and an unpoliced table offers no
        // policy to blame for the miss. The read-back is what says the row is gone rather than merely
        // reported as affected.
        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from credentials where id = @id", otherCredentialId))
            .IsEqualTo(0L);

        // The first account is untouched, which is not a second opinion on the count: it says the
        // statement is scoped by its own predicate and by nothing else, so a delete naming one row
        // takes one row. A grant this wide is only survivable because the statement that carries it
        // is exact.
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from credentials where user_id = @id", userId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_RefusesEveryUpdateOnASessionsIdentity_WhileStillAllowingRevocation()
    {
        // Arrange — one account, its federated credential, a second passkey credential on the same
        // account, and one session established by the first. The second credential is there for the
        // credential_id statement below to aim at: it belongs to the same user, so if the grant ever
        // leaked that column the relabelling would succeed outright instead of tripping the composite
        // foreign key and passing for the wrong reason. It is seeded with raw SQL because no domain
        // factory mints a passkey yet; (passkey, null, null) is the shape
        // CK_credentials_type_shape permits.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid credentialId = (Guid)(await SelectScalarAsync(
            admin, "select id from credentials where user_id = @id", userId))!;
        Guid otherCredentialId = await InsertPasskeyCredentialAsync(admin, userId);
        Guid sessionId = await InsertSessionAsync(admin, userId, credentialId);

        // sessions is policed on the user, like users and budgets, so the session names the owner and
        // no ambient budget. The identity is what keeps the pair below intact: without it the
        // revocation would match zero rows and still report success, leaving the refusals beside it
        // proving nothing (see the class remarks).
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Act — every identity column of sessions by name: user_id, credential_id, credential_type,
        // kind, created_at_utc, expires_at_utc. Relabelling credential_id would rewrite which key
        // opened the door, which is the fact revocation is decided by; rewriting credential_type or
        // kind would hand budget content to a session a federated credential opened, from either
        // end of CK_sessions_kind_matches_credential.
        //
        // The timestamps are the forged values a leaked grant would land on: both keep expiry after
        // creation, so neither is caught by a CHECK and passes for the wrong reason. The first three
        // cannot be, and unavoidably so — the foreign key is composite on
        // (credential_id, user_id, credential_type), so no value moves any one of them alone, and
        // the equality check refuses every kind this row does not already hold. What still makes
        // each of them a measurement is that the helper demands a refusal: a leaked grant that let
        // the statement through, no-op or not, is reported as the exception that failed to arrive.
        // Real values are named anyway rather than random ones, so a leak reports 23503 for one
        // reason instead of two.
        PostgresException userRefusal = await ThrowsPostgresExceptionAsync(
            app, "update sessions set user_id = @value where id = @id", otherUserId, sessionId);
        PostgresException credentialRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update sessions set credential_id = @value where id = @id",
            otherCredentialId,
            sessionId);
        PostgresException credentialTypeRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update sessions set credential_type = @value where id = @id",
            "passkey",
            sessionId);
        PostgresException kindRefusal = await ThrowsPostgresExceptionAsync(
            app, "update sessions set kind = @value where id = @id", "full", sessionId);
        PostgresException createdAtRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update sessions set created_at_utc = @value where id = @id",
            ForgedSessionCreatedInstant,
            sessionId);
        PostgresException expiresAtRefusal = await ThrowsPostgresExceptionAsync(
            app, "update sessions set expires_at_utc = @value where id = @id", ForgedInstant, sessionId);

        // The success half of the pair, and it is the whole of the UPDATE grant: revoked_at_utc is
        // the one column an edit can legitimately reach. The affected count is load-bearing rather
        // than decorative — without it this pair passes when row-level security matched nothing and
        // the update touched nobody, which is precisely the claim the pairing exists to rule out.
        int revoked = await ExecuteAsync(
            app,
            "update sessions set revoked_at_utc = @value where id = @id",
            RevocationInstant,
            sessionId);

        // Assert
        await Assert.That(userRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(credentialRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(credentialTypeRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(kindRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(createdAtRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(expiresAtRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(revoked).IsEqualTo(1);

        await Assert.That(await SelectScalarAsync(
                admin, "select user_id from sessions where id = @id", sessionId))
            .IsEqualTo(userId);
        await Assert.That(await SelectScalarAsync(
                admin, "select credential_id from sessions where id = @id", sessionId))
            .IsEqualTo(credentialId);
        await Assert.That(await SelectScalarAsync(
                admin, "select credential_type from sessions where id = @id", sessionId))
            .IsEqualTo("federated");
        await Assert.That(await SelectScalarAsync(
                admin, "select kind from sessions where id = @id", sessionId))
            .IsEqualTo("locked");
        await Assert.That(await SelectScalarAsync(
                admin, "select created_at_utc from sessions where id = @id", sessionId))
            .IsEqualTo(SeedInstant);
        await Assert.That(await SelectScalarAsync(
                admin, "select expires_at_utc from sessions where id = @id", sessionId))
            .IsEqualTo(SessionExpiryInstant);
        await Assert.That(await SelectScalarAsync(
                admin, "select revoked_at_utc from sessions where id = @id", sessionId))
            .IsEqualTo(RevocationInstant);
    }

    [Test]
    public async Task Database_RefusesToDeleteASession()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid credentialId = (Guid)(await SelectScalarAsync(
            admin, "select id from credentials where user_id = @id", userId))!;
        Guid sessionId = await InsertSessionAsync(admin, userId, credentialId);

        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Act
        PostgresException deleteRefusal = await ThrowsPostgresExceptionAsync(
            app, "delete from sessions where id = @id", sessionId);

        // Assert — the absent DELETE grant is what keeps revocation a recorded fact rather than a
        // disappearance: the role holds no privilege that can make a session unaccountable, and
        // re-revoking converges instead of failing as a second delete of nothing. A retention sweep
        // of expired and revoked rows is the path that would need this grant, and it is the thing
        // that would have to re-argue the omission rather than quietly delete it. The surviving row
        // is not a second opinion on the SQLSTATE: a refusal that had already removed the row on its
        // way to failing is exactly what this rule exists to rule out.
        await Assert.That(deleteRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(await SelectScalarAsync(
                admin, "select count(*) from sessions where id = @id", sessionId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_RefusesToUpdateAnyColumnOfAPasskeyPublicKey()
    {
        // Arrange — one registered passkey, a second bare passkey credential on the same account for
        // the permitted INSERT below to hang off, and a second real user for the user_id statement to
        // aim at: if the grant ever leaked a column, each statement lands rather than tripping a
        // foreign key and passing for the wrong reason.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, SeededHandle, SeededCoseKey);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid freeCredentialId = await InsertPasskeyCredentialAsync(admin, userId);

        // A bare app-role connection — no user, no budget — and like the credentials test above that
        // is the point rather than a leftover. passkey_public_keys is the second table row-level
        // security deliberately exempts: an assertion has to verify a signature with the stored key
        // before it knows whose account it is, so a policy keyed on that identity would refuse the
        // read that produces it. Every statement below reaching its row on an anonymous session is
        // the executable statement of that exemption.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        // Act — every column of passkey_public_keys by name. The rule is "nothing on this row ever
        // changes", and column-for-column is the only shape the absence of an UPDATE grant can be
        // pinned in. RS256 rather than a number outside the vocabulary, and a well-formed handle and
        // key rather than malformed ones, for the same reason the sessions test forges a lifetime-safe
        // timestamp: a value a CHECK would refuse anyway would let this test pass against a table-wide
        // GRANT UPDATE.
        PostgresException credentialRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update passkey_public_keys set credential_id = @value where credential_id = @id",
            freeCredentialId,
            credentialId);
        PostgresException userRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update passkey_public_keys set user_id = @value where credential_id = @id",
            otherUserId,
            credentialId);
        PostgresException credentialTypeRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update passkey_public_keys set credential_type = @value where credential_id = @id",
            "federated",
            credentialId);
        PostgresException handleRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update passkey_public_keys set webauthn_credential_id = @value where credential_id = @id",
            ForgedHandle,
            credentialId);
        PostgresException keyRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update passkey_public_keys set public_key_cose = @value where credential_id = @id",
            ForgedCoseKey,
            credentialId);
        PostgresException algorithmRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update passkey_public_keys set cose_algorithm = @value where credential_id = @id",
            Rs256,
            credentialId);

        // Assert — rewriting public_key_cose is the whole attack this closes: an assertion is
        // verified against whatever key this column holds, so a role that could edit it could make
        // its own signatures verify as somebody's authenticator. The rest are the same door from
        // other sides — repointing the handle would move an authenticator's identity onto another
        // credential, and repointing user_id would move somebody's key material onto another account.
        await Assert.That(credentialRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(userRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(credentialTypeRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(handleRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(keyRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(algorithmRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        // The success half of the pair (see the class remarks), and here it is an INSERT by design
        // rather than because no updatable column happens to exist today: this table holds no UPDATE
        // grant of any shape, and it is the one table in this file where that is a property rather
        // than a snapshot. Registering a passkey must still work, so the INSERT is the write that
        // rules out the other way every refusal above could pass — the role reaching nothing at all.
        await using NpgsqlCommand insert = new(
            "insert into passkey_public_keys " +
            "(credential_id, user_id, credential_type, webauthn_credential_id, public_key_cose, cose_algorithm) " +
            "values (@credential_id, @user_id, 'passkey', @webauthn_credential_id, @public_key_cose, @cose_algorithm)",
            app);
        insert.Parameters.AddWithValue("credential_id", freeCredentialId);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("webauthn_credential_id", ForgedHandle);
        insert.Parameters.AddWithValue("public_key_cose", ForgedCoseKey);
        insert.Parameters.AddWithValue("cose_algorithm", Rs256);
        await Assert.That(await insert.ExecuteNonQueryAsync()).IsEqualTo(1);

        // And the seeded row is untouched. A SQLSTATE says each statement was rejected; only this
        // says none of them rewrote the row on its way to failing.
        await Assert.That(await SelectScalarAsync(
                admin,
                "select user_id from passkey_public_keys where credential_id = @id",
                credentialId))
            .IsEqualTo(userId);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select credential_type from passkey_public_keys where credential_id = @id",
                credentialId))
            .IsEqualTo("passkey");
        await Assert.That(await SelectScalarAsync(
                admin,
                "select cose_algorithm from passkey_public_keys where credential_id = @id",
                credentialId))
            .IsEqualTo(Es256);
        // The two bytea columns read back through a cast rather than compared as objects: a byte[] is
        // compared by reference otherwise, so an equality assertion on the boxed scalar would fail
        // even when the bytes are identical.
        byte[] storedHandle = await SelectBytesAsync(
            admin,
            "select webauthn_credential_id from passkey_public_keys where credential_id = @id",
            credentialId);
        byte[] storedKey = await SelectBytesAsync(
            admin,
            "select public_key_cose from passkey_public_keys where credential_id = @id",
            credentialId);
        await Assert.That(storedHandle).IsEquivalentTo(SeededHandle);
        await Assert.That(storedKey).IsEquivalentTo(SeededCoseKey);
    }

    [Test]
    public async Task Database_RefusesToDeleteAPasskeyPublicKey()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, SeededHandle, SeededCoseKey);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // The exempt table again, so the anonymous session is deliberate — see the test above.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        // Act
        PostgresException deleteRefusal = await ThrowsPostgresExceptionAsync(
            app, "delete from passkey_public_keys where credential_id = @id", credentialId);

        // Assert — no UPDATE of any shape and no DELETE is what holds this table to the reason it is
        // exempt from row-level security. A role that could remove a key could lock somebody out of
        // their own account with one statement, and because the table is unpoliced it could do it to
        // anybody's. Rows leave here only by the cascade from credentials, and through it from users,
        // which is a deletion somebody asked for rather than one a bug can reach. Revoking a passkey
        // on request now exists, and it changes nothing here: that path deletes the credential and
        // lets the cascade take the key, so this grant stays absent — see ADR 0014.
        await Assert.That(deleteRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select count(*) from passkey_public_keys where credential_id = @id",
                credentialId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_AllowsUpdatingOnlyTheSignatureCounter()
    {
        // Arrange — one registered passkey with its counter, a second bare passkey credential on the
        // same account, and a second real user. Both exist so a leaked grant would land its statement
        // instead of tripping a foreign key and passing for the wrong reason: the free credential has
        // no counter row, so repointing onto it would not collide with the primary key either.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, SeededHandle, SeededCoseKey);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid freeCredentialId = await InsertPasskeyCredentialAsync(admin, userId);

        // Unlike passkey_public_keys, this table is policed on the user — the counter is read only
        // after the assertion's signature has verified, so an identity is on the connection by then.
        // The session therefore names the owner and no ambient budget, and the identity is what keeps
        // the pair intact: without it the permitted update would match zero rows and report success,
        // leaving the refusals beside it vacuous (see the class remarks).
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Act — the three immutable columns by name, then the one column the grant list holds.
        PostgresException credentialRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update passkey_signature_counters set credential_id = @value where credential_id = @id",
            freeCredentialId,
            credentialId);
        PostgresException userRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update passkey_signature_counters set user_id = @value where credential_id = @id",
            otherUserId,
            credentialId);
        PostgresException credentialTypeRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update passkey_signature_counters set credential_type = @value where credential_id = @id",
            "federated",
            credentialId);

        // The success half, and it is the whole of the UPDATE grant: advancing the counter is what
        // every assertion does, so this column has to be writable for sign-in to work at all. The
        // affected count is load-bearing rather than decorative — without it this pair passes when
        // row-level security matched nothing and the update touched nobody.
        int advanced = await ExecuteAsync(
            app,
            "update passkey_signature_counters set signature_counter = @value where credential_id = @id",
            AdvancedCounter,
            credentialId);

        // Assert — repointing credential_id or user_id is the thing the one-column list exists to
        // stop: it would move a counter onto another account's credential, and clone detection would
        // then be comparing one authenticator's numbers against another's.
        await Assert.That(credentialRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(userRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(credentialTypeRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(advanced).IsEqualTo(1);

        await Assert.That(await SelectScalarAsync(
                admin,
                "select user_id from passkey_signature_counters where credential_id = @id",
                credentialId))
            .IsEqualTo(userId);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select credential_type from passkey_signature_counters where credential_id = @id",
                credentialId))
            .IsEqualTo("passkey");
        await Assert.That(await SelectScalarAsync(
                admin,
                "select signature_counter from passkey_signature_counters where credential_id = @id",
                credentialId))
            .IsEqualTo(AdvancedCounter);
    }

    [Test]
    public async Task Database_RefusesToDeleteAPasskeySignatureCounter()
    {
        // Arrange
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, SeededHandle, SeededCoseKey);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Act
        PostgresException deleteRefusal = await ThrowsPostgresExceptionAsync(
            app, "delete from passkey_signature_counters where credential_id = @id", credentialId);

        // Assert — the absent DELETE grant is what makes the counter a floor a clone cannot get under.
        // Deleting the row and re-inserting it at zero is the same thing as rewinding the counter, and
        // that is precisely the move clone detection exists to catch; the UPDATE grant above cannot do
        // it, so the DELETE must not offer a way around. Rows leave by the cascade from credentials.
        await Assert.That(deleteRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select count(*) from passkey_signature_counters where credential_id = @id",
                credentialId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_RefusesEveryUpdateOnARecoveryCodeHash_WhileStillAllowingInsertAndDelete()
    {
        // Arrange — two accounts, one recovery-codes credential each, and one unredeemed code hanging
        // off the first account's. One credential per account and not two, because
        // IX_credentials_user_id_recovery_codes now refuses the second: an account holds at most one
        // issued set, since two sets are two remaining-counts with nothing saying which one binds.
        //
        // That index is what reshaped this arrangement, and the reshaping made it stronger in both
        // places it touched. The permitted INSERT now hangs off the SAME credential the seeded code
        // does, carrying a second, different verifier hash — which is the natural shape rather than a
        // workaround, because a set is many hash rows on one credential. Same account, same type, same
        // credential, so nothing but the grant stands between that statement and the row: a leaked
        // INSERT grant lands it outright.
        //
        // The credential_id statement below aims at the second ACCOUNT's recovery-codes credential,
        // which is the only recovery-codes credential left to aim at. An earlier note here worried
        // that a cross-account target passes for the wrong reason — the composite foreign key over
        // (credential_id, user_id, credential_type) refuses it whatever the grant says. It does, and
        // that worry is already answered two paragraphs down in the Act block: what makes each of
        // these statements a measurement is the SQLSTATE, not whether the statement could have
        // succeeded. A leaked grant RUNS the statement and reports 23503; a refused grant never runs
        // it and reports 42501. The same reasoning already covers user_id and credential_type, neither
        // of which could land either.
        //
        // Both credentials and the code row are seeded with raw SQL on the superuser connection
        // although Credential.CreateRecoveryCodes now exists, for the reason InsertPasskeyCredentialAsync
        // predates: these tests measure the application role's write surface, and seeding through the
        // domain would make the arrangement depend on a write path that is itself under test.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid credentialId = await InsertRecoveryCodesCredentialAsync(admin, userId);
        Guid otherCredentialId = await InsertRecoveryCodesCredentialAsync(admin, otherUserId);
        await InsertRecoveryCodeHashAsync(admin, credentialId, userId, SeededVerifierHash);

        // A bare app-role connection — no user, no budget, nothing on the session at all — and, as in
        // the credentials and passkey_public_keys tests above, that is the point rather than a
        // leftover. recovery_code_hashes is the third table row-level security deliberately exempts: a
        // code is redeemed by an anonymous request that finds the row by the SHA-256 of the verifier
        // the person typed, before anybody has said who they are, so a policy keyed on
        // app.current_user_id would refuse the very query that establishes the identity — and refuse
        // it loudly, because an unset setting reaches the policy as ''::uuid and raises 22P02. Every
        // statement below reaching its row on an anonymous session is the executable statement of that
        // exemption. Do not "tidy" this into a configured connection.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        // Act — every column of recovery_code_hashes by name: verifier_hash, credential_id, user_id,
        // credential_type, created_at_utc. The rule is "a recovery code is consumed by deleting its
        // row, never by stamping it used", and the absence of an UPDATE grant of any shape is the one
        // statement that holds it. Column-for-column, because that is the only shape the absence can
        // be pinned in — a table-wide GRANT UPDATE would let every one of them through, and so would a
        // column list quietly added for a redeemed_at_utc nobody argued for.
        //
        // The forged verifier hash is exactly 32 bytes and differs from the seeded one, for the reason
        // the passkey test forges a well-formed handle: a value CK_recovery_code_hashes_verifier_hash_length
        // would refuse anyway makes its refusal say nothing about the grant. The first three columns
        // cannot all land even with a leak — the foreign key is composite over
        // (credential_id, user_id, credential_type) so no value moves any one of them alone, and
        // CK_recovery_code_hashes_credential_type refuses every spelling but its own. What still makes
        // each a measurement is the SQLSTATE: a leaked grant lets the statement run and reports 23503
        // or 23514, not 42501.
        PostgresException hashRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update recovery_code_hashes set verifier_hash = @value where credential_id = @id",
            ForgedVerifierHash,
            credentialId);
        PostgresException credentialRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update recovery_code_hashes set credential_id = @value where credential_id = @id",
            otherCredentialId,
            credentialId);
        PostgresException userRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update recovery_code_hashes set user_id = @value where credential_id = @id",
            otherUserId,
            credentialId);
        PostgresException credentialTypeRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update recovery_code_hashes set credential_type = @value where credential_id = @id",
            "passkey",
            credentialId);
        PostgresException createdAtRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update recovery_code_hashes set created_at_utc = @value where credential_id = @id",
            ForgedInstant,
            credentialId);

        // Assert — rewriting verifier_hash is the attack the missing grant closes from the front: the
        // row is found by that column and by nothing else, so a role that could edit it could file a
        // code of its own choosing under somebody's account and then redeem it. Repointing
        // credential_id or user_id is the same door from the side — an anonymous redemption adopts the
        // user_id it finds on the row, and no policy is watching this table, so a moved owner is a
        // handover of an account rather than a misfiled row. And an editable created_at_utc would make
        // the issue date of a set — the one fact that says which set is current — a thing the
        // application could rewrite.
        await Assert.That(hashRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(credentialRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(userRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(credentialTypeRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(createdAtRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        // The success half of the pair (see the class remarks), and here it is an INSERT and a DELETE
        // rather than an update of the one permitted column, because there is no permitted column and
        // there is not meant to be one. Issuing a set writes rows and redeeming a code removes one, so
        // both halves of the table's whole lifecycle have to work for a role holding no UPDATE at all
        // — which is what rules out the other way every refusal above could pass, the role reaching
        // nothing here whatsoever.
        //
        // Onto the same credential the seeded code hangs off, with a second, different verifier hash.
        // That is the shape a set actually has — many hash rows on the one credential standing for the
        // issued set — and it is the strongest arrangement available: same account, same type, same
        // credential, so a leaked INSERT grant has nothing else to trip over on the way in.
        await using NpgsqlCommand insert = new(
            "insert into recovery_code_hashes " +
            "(verifier_hash, credential_id, user_id, credential_type, created_at_utc) " +
            "values (@verifier_hash, @credential_id, @user_id, 'recovery_codes', @created_at_utc)",
            app);
        insert.Parameters.AddWithValue("verifier_hash", IssuedVerifierHash);
        insert.Parameters.AddWithValue("credential_id", credentialId);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await Assert.That(await insert.ExecuteNonQueryAsync()).IsEqualTo(1);

        // Redemption, and the affected count is the assertion rather than the absence of an exception:
        // a DELETE matching zero rows raises nothing at all, and on an unpoliced table there is no
        // policy to blame for the miss — so without the count this passes on a statement that removed
        // nothing.
        //
        // By verifier_hash rather than by credential_id, and that is not a workaround for the two rows
        // now sharing a credential — it is the statement redemption actually sends. The row is found by
        // the SHA-256 of the verifier the person typed and by nothing else, which is the whole of what
        // narrows a DELETE grant this table scopes by nothing. A delete by credential_id here would
        // take the entire set, which is revocation, not redemption; the count of exactly 1 against a
        // credential holding two rows is what says this statement took one code and left the set
        // standing, and it is the assertion the old single-row arrangement could not make.
        await using NpgsqlCommand redeem = new(
            "delete from recovery_code_hashes where verifier_hash = @verifier_hash", app);
        redeem.Parameters.AddWithValue("verifier_hash", IssuedVerifierHash);
        await Assert.That(await redeem.ExecuteNonQueryAsync()).IsEqualTo(1);

        // And the seeded row is untouched, column for column. A SQLSTATE says each statement was
        // rejected; only this says none of them rewrote the row on its way to failing, and that the
        // delete above took the row it named rather than the table's contents. Reading verifier_hash
        // back is what turns "one row survives" into "the RIGHT row survives": the two rows on this
        // credential differ in that column alone, so a delete that took the wrong one leaves the same
        // count and a different hash.
        byte[] storedHash = await SelectBytesAsync(
            admin,
            "select verifier_hash from recovery_code_hashes where credential_id = @id",
            credentialId);
        await Assert.That(storedHash).IsEquivalentTo(SeededVerifierHash);
        await Assert.That(await SelectScalarAsync(
                admin, "select user_id from recovery_code_hashes where credential_id = @id", credentialId))
            .IsEqualTo(userId);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select credential_type from recovery_code_hashes where credential_id = @id",
                credentialId))
            .IsEqualTo("recovery_codes");
        await Assert.That(await SelectScalarAsync(
                admin,
                "select created_at_utc from recovery_code_hashes where credential_id = @id",
                credentialId))
            .IsEqualTo(SeedInstant);

        // Exactly one row left on the credential, which is the other half of the redemption claim: the
        // inserted code is gone and the set it belonged to is not. A count of 2 would mean the delete
        // matched nothing despite reporting a row, and a count of 0 would mean it swept the credential
        // rather than the code.
        await Assert.That(await SelectScalarAsync(
                admin,
                "select count(*) from recovery_code_hashes where credential_id = @id",
                credentialId))
            .IsEqualTo(1L);

        // Nothing reached the second account. Every refused statement above named the first account's
        // row, and the credential_id statement aimed at this credential, so a leaked UPDATE grant is
        // the one way a row could have arrived under it — that is what this zero rules out, and it is
        // not the same claim as the SQLSTATE, which says only what the server reported.
        await Assert.That(await SelectScalarAsync(
                admin,
                "select count(*) from recovery_code_hashes where credential_id = @id",
                otherCredentialId))
            .IsEqualTo(0L);
    }

    [Test]
    public async Task Database_LetsTheAppRoleDeleteAnyRecoveryCodeHash_OnASessionNamingNobody()
    {
        // Arrange — two accounts, each with a recovery-code credential and one unredeemed code. The
        // second one is the target: deleting the session's own row would be indistinguishable from a
        // correctly scoped delete, and there is no session here to own anything anyway.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid credentialId = await InsertRecoveryCodesCredentialAsync(admin, userId);
        Guid otherCredentialId = await InsertRecoveryCodesCredentialAsync(admin, otherUserId);
        await InsertRecoveryCodeHashAsync(admin, credentialId, userId, SeededVerifierHash);
        await InsertRecoveryCodeHashAsync(
            admin, otherCredentialId, otherUserId, OtherAccountVerifierHash);

        // A bare app-role connection — no app.current_user_id, no app.current_budget_id, nothing on
        // the session at all — and, as in Database_LetsTheAppRoleDeleteAnyCredential_OnASessionNamingNobody
        // above, that is the measurement rather than a leftover. Naming a user here would make the
        // delete look scoped by something.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        // Act
        int deleted = await ExecuteAsync(
            app, "delete from recovery_code_hashes where credential_id = @id", otherCredentialId);

        // Assert — this test asserts a hole, and it is here so that nobody mistakes the hole for an
        // accident. recovery_code_hashes is exempt from row-level security because the redemption read
        // runs before anybody has said who they are, and the role holds DELETE on it because deleting
        // the row IS the redemption. Put together, that is a destructive privilege the database scopes
        // by nothing: the grant names the table, no policy names the rows, so a statement sent by a
        // connection that has not said who it is removes any account's unredeemed code. What narrows
        // it is the redemption lookup's own predicate — the verifier hash, which nobody can produce
        // without the code — and the application's own owner filter on every read or write of this
        // table that is not that lookup. Nothing beneath the application does.
        //
        // None of this is desirable and none of it is a regression: this test goes red the day
        // somebody succeeds in policing recovery_code_hashes, and that is the day the exemption in
        // RowLevelSecurityCoverage needs rewriting rather than the day this test needs relaxing. It is
        // also the exact control that would go missing if the exemption were ever quietly justified by
        // "the hash is unguessable" instead of by "the request has no identity yet".
        //
        // The count of 1 is the assertion rather than the absence of an exception, for the reason the
        // credentials test gives: a DELETE matching nothing raises nothing, and an unpoliced table
        // offers no policy to blame for the miss. The read-back is what says the row is gone rather
        // than merely reported as affected.
        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select count(*) from recovery_code_hashes where credential_id = @id",
                otherCredentialId))
            .IsEqualTo(0L);

        // The first account's code survives, which is not a second opinion on the count: it says the
        // statement is scoped by its own predicate and by nothing else, so a delete naming one
        // credential takes one credential's rows. A grant this wide is only survivable because the
        // statement that carries it is exact.
        await Assert.That(await SelectScalarAsync(
                admin,
                "select count(*) from recovery_code_hashes where credential_id = @id",
                credentialId))
            .IsEqualTo(1L);
    }

    [Test]
    public async Task Database_RefusesEveryUpdateOnAWrappedAccountKey_WhileStillAllowingInsert()
    {
        // Arrange — one account holding a registered passkey with the account's two keys filed against
        // it, a SECOND bare passkey credential on the same account, and a second real user. Both extras
        // exist so a leaked grant would land its statement rather than trip a foreign key and pass for
        // the wrong reason: the bare credential has no wrapped keys row, so repointing credential_id
        // onto it would not collide with the primary key either.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid otherUserId = await host.SeedUserAsync("google-2", "other@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, SeededHandle, SeededCoseKey);
        await host.SeedWrappedAccountKeysAsync(credentialId, SeededFactorId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid freeCredentialId = await InsertPasskeyCredentialAsync(admin, userId);

        // wrapped_account_keys is policed by user_isolation, so the session names the owner and no
        // ambient budget. The identity is what keeps the pair below intact: on a session with no user id
        // the permitted INSERT would fail its WITH CHECK instead of landing — and the read of the seeded
        // row would raise 22P02 rather than a privilege answer — so a refusal with no permitted write
        // beside it would prove nothing (see the class remarks).
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Act — every column of wrapped_account_keys by name: credential_id, user_id, factor_id,
        // credential_type, wrapped_content_key, wrapped_index_key, created_at_utc. The rule is "no
        // UPDATE of any shape — every column is immutable", and column-for-column is the only shape
        // that absence can be pinned in: a table-wide GRANT UPDATE would let all seven through, and so
        // would a column list quietly added for the key rotation that has not arrived.
        //
        // Each value is one the column itself would accept, which is what keeps every SQLSTATE below
        // about the grant. The forged envelopes are exactly 61 bytes carrying version 1 — the four
        // length and version checks refuse nothing, so a leaked grant lands them — and the forged
        // credential type is 'recovery_codes', a spelling CK_wrapped_account_keys_credential_type
        // accepts. Three of the seven could not land even with a leak: the foreign key is composite over
        // (credential_id, user_id, credential_type), so no value moves user_id or credential_type alone.
        // What still makes each a measurement is the SQLSTATE — a leaked grant RUNS the statement and
        // reports 23503, while a refused grant never runs it and reports 42501.
        PostgresException credentialRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update wrapped_account_keys set credential_id = @value where credential_id = @id",
            freeCredentialId,
            credentialId);
        PostgresException userRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update wrapped_account_keys set user_id = @value where credential_id = @id",
            otherUserId,
            credentialId);
        PostgresException factorRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update wrapped_account_keys set factor_id = @value where credential_id = @id",
            ForgedFactorId,
            credentialId);
        PostgresException credentialTypeRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update wrapped_account_keys set credential_type = @value where credential_id = @id",
            "recovery_codes",
            credentialId);
        PostgresException contentKeyRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update wrapped_account_keys set wrapped_content_key = @value where credential_id = @id",
            ForgedContentKey,
            credentialId);
        PostgresException indexKeyRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update wrapped_account_keys set wrapped_index_key = @value where credential_id = @id",
            ForgedIndexKey,
            credentialId);
        PostgresException createdAtRefusal = await ThrowsPostgresExceptionAsync(
            app,
            "update wrapped_account_keys set created_at_utc = @value where credential_id = @id",
            ForgedInstant,
            credentialId);

        // Assert — rewriting either envelope is the attack the missing grant closes from the front. The
        // database cannot tell a well-formed envelope from a well-formed lie: both columns are 61 bytes
        // of version 1, and what says an envelope is the right one is the associated data it was sealed
        // with, which only a client holding the key-encryption key can check. So an editable envelope
        // column is a way to replace the account's keys with two the operator chose, and the discovery
        // that they do not open happens in the browser, months later, on the day somebody needs them.
        // Repointing credential_id, user_id or factor_id is the same door from the side — the factor id
        // IS the associated data, so editing it invalidates both envelopes in place — and an editable
        // created_at_utc would rewrite the one fact that says which factor was registered when.
        //
        // Registering or revoking a factor writes or removes a whole row, which is why none of this
        // costs the product anything: a content-key rotation is the one operation that would rewrite
        // these two columns, and it must arrive with its own GRANT UPDATE and its own argument.
        await Assert.That(credentialRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(userRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(factorRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(credentialTypeRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(contentKeyRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(indexKeyRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(createdAtRefusal.SqlState)
            .IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);

        // The success half of the pair (see the class remarks), and here it is an INSERT because there
        // is no permitted column to update and there is not meant to be one. Registering a factor writes
        // a new row, so that half of the table's lifecycle has to work for a role holding no UPDATE at
        // all — which is what rules out the other way every refusal above could pass, the role reaching
        // nothing on this table whatsoever. Onto the same account's free credential with its own factor
        // id: same owner, same type, so nothing but the grant and the policy stands between this
        // statement and the row.
        await using NpgsqlCommand insert = new(
            "insert into wrapped_account_keys " +
            "(credential_id, user_id, factor_id, credential_type, " +
            "wrapped_content_key, wrapped_index_key, created_at_utc) " +
            "values (@credential_id, @user_id, @factor_id, 'passkey', " +
            "@wrapped_content_key, @wrapped_index_key, @created_at_utc)",
            app);
        insert.Parameters.AddWithValue("credential_id", freeCredentialId);
        insert.Parameters.AddWithValue("user_id", userId);
        insert.Parameters.AddWithValue("factor_id", InsertedFactorId);
        insert.Parameters.AddWithValue("wrapped_content_key", SeededContentKey);
        insert.Parameters.AddWithValue("wrapped_index_key", SeededIndexKey);
        insert.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await Assert.That(await insert.ExecuteNonQueryAsync()).IsEqualTo(1);

        // And the seeded row is untouched, column for column. A SQLSTATE says each statement was
        // rejected; only this says none of them rewrote the row on its way to failing. Both envelopes are
        // read back as bytes rather than counted, because a swapped or replaced envelope is the one
        // change nothing else in this system could ever notice.
        await Assert.That(await SelectScalarAsync(
                admin, "select user_id from wrapped_account_keys where credential_id = @id", credentialId))
            .IsEqualTo(userId);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select factor_id from wrapped_account_keys where credential_id = @id",
                credentialId))
            .IsEqualTo(SeededFactorId);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select credential_type from wrapped_account_keys where credential_id = @id",
                credentialId))
            .IsEqualTo("passkey");
        await Assert.That(await SelectBytesAsync(
                admin,
                "select wrapped_content_key from wrapped_account_keys where credential_id = @id",
                credentialId))
            .IsEquivalentTo(SeededContentKey);
        await Assert.That(await SelectBytesAsync(
                admin,
                "select wrapped_index_key from wrapped_account_keys where credential_id = @id",
                credentialId))
            .IsEquivalentTo(SeededIndexKey);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select created_at_utc from wrapped_account_keys where credential_id = @id",
                credentialId))
            .IsEqualTo(SeedInstant);
    }

    [Test]
    public async Task Database_RefusesADeleteOnAWrappedAccountKey_WhileTheCascadeFromItsCredentialStillTakesIt()
    {
        // Arrange — one account holding a registered passkey with the account's two keys filed against
        // it. Nothing else: this test needs one parent and one child, and the whole of it is which of
        // the two the role may remove.
        await using RepositoryTestHost host = await StartHostAsync();
        Guid userId = await host.SeedUserAsync("google-1", "person@example.com");
        Guid credentialId = await host.SeedPasskeyAsync(userId, SeededHandle, SeededCoseKey);
        await host.SeedWrappedAccountKeysAsync(credentialId, SeededFactorId);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // The session names the owner, because wrapped_account_keys is policed by user_isolation and the
        // permitted half below has to be a statement that really reaches its row. It costs the refused
        // half nothing: a missing table privilege is answered before any policy is consulted.
        await using NpgsqlConnection app = await host.OpenAppConnectionForUserAsync(userId);

        // Act — the direct delete first.
        PostgresException deleteRefusal = await ThrowsPostgresExceptionAsync(
            app, "delete from wrapped_account_keys where credential_id = @id", credentialId);

        // Assert — 42501, and the row is still there. This is the half that says the absent DELETE grant
        // is a real refusal rather than a privilege nobody happens to use.
        await Assert.That(deleteRefusal.SqlState).IsEqualTo(PostgresErrorCodes.InsufficientPrivilege);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select count(*) from wrapped_account_keys where credential_id = @id",
                credentialId))
            .IsEqualTo(1L);

        // Act — and now the same rows, removed the way the product removes them: by deleting the parent
        // credential, which this role DOES hold DELETE on. Revoking a factor is exactly this statement.
        int credentialsDeleted = await ExecuteAsync(
            app, "delete from credentials where id = @id", credentialId);

        // Assert — the affected count first, because a delete matching nothing raises nothing and would
        // make the zero below mean "there was never a row" rather than "the cascade took it".
        await Assert.That(credentialsDeleted).IsEqualTo(1);
        await Assert.That(await SelectScalarAsync(
                admin,
                "select count(*) from wrapped_account_keys where credential_id = @id",
                credentialId))
            .IsEqualTo(0L);

        // This pair is what makes the missing DELETE grant safe rather than merely narrow, and it is the
        // reason that absence is load-bearing instead of an oversight somebody should tidy. PostgreSQL
        // runs ON DELETE CASCADE through referential-integrity triggers that execute with the privileges
        // of the REFERENCING table's owner, not of the current role — so revoking a factor removes its
        // wrapped keys through the cascade while this role cannot issue that DELETE itself. Nothing the
        // product needs is withheld.
        //
        // What the asymmetry buys is the other direction, which is ADR 0017's argument: with DELETE
        // granted, an EF cascade into rows the change tracker happens to be holding SUCCEEDS SILENTLY,
        // and the account's only way back into its own data leaves by the application instead of by the
        // database, with no SQLSTATE to say so. Without it, the same mistake dies loudly with the 42501
        // above. The concrete consequence lives in GenerateRecoveryCodesHandler, which must never
        // materialise a replaced set's child rows.
    }

    [Test]
    public async Task Database_AllowsInsertingAndDeletingAWebAuthnChallenge()
    {
        // Arrange — nothing to seed beside it. A challenge belongs to a ceremony rather than to a
        // person, so it carries no owner and there is no account for it to hang off.
        await using RepositoryTestHost host = await StartHostAsync();

        // A bare app-role connection, and here it is not an exemption argued around an identity that
        // does not exist yet — it is the only session this table ever sees. The leg that issues an
        // authentication challenge runs before anybody has said who they are.
        await using NpgsqlConnection app = new(host.AppConnectionString);
        await app.OpenAsync();

        Guid challengeId = Guid.CreateVersion7();

        // Act — both halves of the grant, in the order a ceremony performs them.
        await using NpgsqlCommand insert = new(
            "insert into webauthn_challenges (id, challenge, ceremony, created_at_utc, expires_at_utc) " +
            "values (@id, @challenge, 'authentication', @created_at_utc, @expires_at_utc)",
            app);
        insert.Parameters.AddWithValue("id", challengeId);
        insert.Parameters.AddWithValue("challenge", ChallengeBytes);
        insert.Parameters.AddWithValue("created_at_utc", SeedInstant);
        insert.Parameters.AddWithValue("expires_at_utc", ChallengeExpiryInstant);
        int inserted = await insert.ExecuteNonQueryAsync();

        int consumed = await ExecuteAsync(
            app, "delete from webauthn_challenges where id = @id", challengeId);

        // Assert — this is the one table in this file the role may DELETE from, and the exception is
        // deliberate rather than the rule being broken. These rows are nonces: consuming one IS
        // deleting it, which is what makes a challenge single-use, so the grant that looks like a hole
        // everywhere else is the mechanism here. Contrast sessions, where revocation writes a column
        // precisely so the row stays accountable — opposite decisions, because the rows mean opposite
        // things. The affected count on the delete is what says the row was really there to consume: a
        // delete of nothing reports success just as happily.
        await Assert.That(inserted).IsEqualTo(1);
        await Assert.That(consumed).IsEqualTo(1);
    }

    /// <summary>
    /// Fixed UTC instant for rows these tests write. PostgreSQL <c>timestamptz</c> rejects a
    /// non-UTC <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The COSE identifiers of the two algorithms the product verifies. Both are values
    /// <c>CK_passkey_public_keys_cose_algorithm</c> accepts, which is the point: a forged algorithm
    /// outside the pair would be refused by the check rather than by the grant, and the test would go
    /// green against a table-wide <c>GRANT UPDATE</c>.
    /// </summary>
    private const int Es256 = (int)CoseAlgorithm.Es256;

    private const int Rs256 = (int)CoseAlgorithm.Rs256;

    /// <summary>
    /// The counter the permitted update advances to. Distinct from the zero every seeded passkey
    /// starts at, so the read-back cannot pass on a column that was never written.
    /// </summary>
    private const long AdvancedCounter = 42L;

    /// <summary>
    /// The WebAuthn credential id and COSE key the seeded passkey carries, and the pair a leaked
    /// grant would have written over them. Every one of the four is a length the column's own checks
    /// accept, for the reason the algorithms above are both real: a value a CHECK would refuse anyway
    /// makes a refusal say nothing about the grant. The forged pair differs from the seeded pair, or
    /// the read-back asserting the row is unchanged would prove nothing.
    /// </summary>
    private static readonly byte[] SeededHandle = [.. Enumerable.Repeat((byte)0xC1, 32)];

    private static readonly byte[] SeededCoseKey = [0xA5, 0x01, 0x02, 0x03];

    private static readonly byte[] ForgedHandle = [.. Enumerable.Repeat((byte)0xD2, 32)];

    private static readonly byte[] ForgedCoseKey = [0xB6, 0x04, 0x05, 0x06];

    /// <summary>
    /// The four recovery-code verifier hashes these tests write, read back and forge. Every one is
    /// exactly 32 bytes, which is the whole content of
    /// <c>CK_recovery_code_hashes_verifier_hash_length</c> — a value that check would refuse anyway
    /// makes its refusal say nothing about the grant, for the same reason the passkey test forges a
    /// well-formed handle. All four differ from each other, or the read-back asserting the seeded row
    /// is unchanged, and the counts saying the right row left, would prove nothing.
    /// </summary>
    private static readonly byte[] SeededVerifierHash = [.. Enumerable.Repeat((byte)0xA7, 32)];

    private static readonly byte[] ForgedVerifierHash = [.. Enumerable.Repeat((byte)0xB8, 32)];

    private static readonly byte[] IssuedVerifierHash = [.. Enumerable.Repeat((byte)0x94, 32)];

    private static readonly byte[] OtherAccountVerifierHash =
        [.. Enumerable.Repeat((byte)0x6D, 32)];

    /// <summary>
    /// The nonce the challenge test writes. Exactly 32 bytes, which is the whole content of
    /// <c>CK_webauthn_challenges_length</c>.
    /// </summary>
    private static readonly byte[] ChallengeBytes = [.. Enumerable.Repeat((byte)0xE3, 32)];

    /// <summary>
    /// The expiry that challenge carries. Strictly after <see cref="SeedInstant" />, which is the
    /// whole content of <c>CK_webauthn_challenges_lifetime</c>.
    /// </summary>
    private static readonly DateTime ChallengeExpiryInstant =
        new(2026, 6, 12, 13, 19, 15, DateTimeKind.Utc);

    /// <summary>
    /// Expiry of the seeded session. Strictly after <see cref="SeedInstant" />, which is the whole
    /// content of <c>CK_sessions_lifetime</c>.
    /// </summary>
    private static readonly DateTime SessionExpiryInstant =
        new(2026, 6, 13, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// The creation instant a leaked <c>created_at_utc</c> grant would have written. Before
    /// <see cref="SessionExpiryInstant" /> on purpose: a forged value that breached
    /// <c>CK_sessions_lifetime</c> would be refused by the check rather than by the grant, and the
    /// test would go green against a table-wide <c>GRANT UPDATE</c>.
    /// </summary>
    private static readonly DateTime ForgedSessionCreatedInstant =
        new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The instant the permitted revocation writes. Distinct from every other constant here so the
    /// read-back cannot pass on a column that was never written.
    /// </summary>
    private static readonly DateTime RevocationInstant =
        new(2026, 6, 12, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The value an update of an immutable timestamp column would have written had the grant
    /// allowed it. It must differ from <see cref="SeedInstant" />: the read-back asserting the row
    /// still holds <see cref="SeedInstant" /> proves nothing if the two are equal. PostgreSQL
    /// <c>timestamptz</c> rejects a non-UTC <see cref="DateTime" />, so
    /// <see cref="DateTimeKind.Utc" /> is load-bearing here too.
    /// </summary>
    private static readonly DateTime ForgedInstant = new(2031, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    /// <summary>
    /// The two envelopes the seeded wrapped-keys row carries, and the two a refused statement tried to
    /// put in their place. All four are well-formed — exactly
    /// <see cref="WrappedAccountKeys.EnvelopeLength" /> bytes carrying
    /// <see cref="WrappedAccountKeys.EnvelopeVersion" /> — which is the point: an envelope of any other
    /// shape would be refused by one of the four length and version checks rather than by the grant, and
    /// the test would go green against a table-wide <c>GRANT UPDATE</c>. All four differ from one
    /// another, so a read-back cannot pass on the wrong column or on a value nobody wrote.
    /// </summary>
    private static readonly byte[] SeededContentKey =
        RepositoryTestHost.WrappedKeyEnvelope(RepositoryTestHost.SeededContentKeyFiller);

    private static readonly byte[] SeededIndexKey =
        RepositoryTestHost.WrappedKeyEnvelope(RepositoryTestHost.SeededIndexKeyFiller);

    private static readonly byte[] ForgedContentKey = RepositoryTestHost.WrappedKeyEnvelope(0x5A);

    private static readonly byte[] ForgedIndexKey = RepositoryTestHost.WrappedKeyEnvelope(0x6B);

    /// <summary>
    /// The factor identifier the seeded wrapped-keys row carries, the one a refused statement tried to
    /// move it to, and the one the permitted <c>INSERT</c> files its own row under. Three distinct
    /// values, because <c>IX_wrapped_account_keys_factor_id</c> is unique across the whole table: a
    /// shared one would turn the permitted insert into a <c>23505</c> and the read-back into an
    /// assertion that could not tell a refused update from a successful one.
    /// </summary>
    private static readonly Guid SeededFactorId = new("0199f3a1-0000-7000-8000-0000000000a1");

    private static readonly Guid ForgedFactorId = new("0199f3a1-0000-7000-8000-0000000000b2");

    private static readonly Guid InsertedFactorId = new("0199f3a1-0000-7000-8000-0000000000c3");

    /// <summary>
    /// Writes a second credential onto an existing account, on the superuser connection. Raw SQL
    /// although <see cref="Credential.CreatePasskey" /> now exists, because these tests measure the
    /// application role's write surface and seeding through the domain would make the arrangement
    /// depend on a write path that is itself under test; <c>(passkey, null, null)</c> is the shape
    /// <c>CK_credentials_type_shape</c> permits, and the partial unique index on
    /// <c>(provider, subject)</c> names only federated rows, so it does not collide. Unlike a
    /// recovery-codes credential an account may hold several of these — FR-043 — which is why no
    /// per-user index names them.
    /// </summary>
    private static async Task<Guid> InsertPasskeyCredentialAsync(
        NpgsqlConnection connection,
        Guid userId)
    {
        Guid credentialId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            "insert into credentials (id, user_id, type, provider, subject, created_at_utc) " +
            "values (@id, @user_id, 'passkey', null, null, @created_at_utc)",
            connection);
        command.Parameters.AddWithValue("id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await command.ExecuteNonQueryAsync();
        return credentialId;
    }

    /// <summary>
    /// Writes a recovery-codes credential onto an existing account, on the superuser connection, and
    /// returns its id. One credential stands for the whole issued set, and an account holds
    /// <b>at most one</b>: <c>IX_credentials_user_id_recovery_codes</c> is a partial unique index over
    /// <c>user_id</c> filtered to this type, so a second call for the same account raises
    /// <c>23505</c>. Callers needing two of these need two accounts.
    /// </summary>
    /// <remarks>
    /// Raw SQL although <see cref="Credential.CreateRecoveryCodes" /> now exists, for the reason
    /// <see cref="InsertPasskeyCredentialAsync" /> keeps its own: these tests measure the application
    /// role's write surface, so seeding through the domain would make the arrangement depend on a
    /// write path that is itself under test. <c>(recovery_codes, null, null)</c> is the shape
    /// <c>CK_credentials_type_shape</c> permits — the same shape as a passkey row, which is deliberate
    /// and recorded on that constraint — and the <c>(provider, subject)</c> index names only federated
    /// rows, so the two NULLs never collide there.
    /// </remarks>
    private static async Task<Guid> InsertRecoveryCodesCredentialAsync(
        NpgsqlConnection connection,
        Guid userId)
    {
        Guid credentialId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            "insert into credentials (id, user_id, type, provider, subject, created_at_utc) " +
            "values (@id, @user_id, 'recovery_codes', null, null, @created_at_utc)",
            connection);
        command.Parameters.AddWithValue("id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await command.ExecuteNonQueryAsync();
        return credentialId;
    }

    /// <summary>
    /// Writes one unredeemed recovery code onto an existing recovery-codes credential, on the
    /// superuser connection.
    /// </summary>
    /// <remarks>
    /// Raw SQL for a second reason on top of the one above: <c>RecoveryCodeHash</c> deliberately
    /// carries no factory, no hashing helper and no validation method, because each of those is a rule
    /// somebody has to argue for and arrives with the test that demands it. Seeding through the
    /// superuser connection is what lets these tests measure the application role's write surface
    /// without first depending on that role being able to write the arrangement.
    /// </remarks>
    private static async Task InsertRecoveryCodeHashAsync(
        NpgsqlConnection connection,
        Guid credentialId,
        Guid userId,
        byte[] verifierHash)
    {
        await using NpgsqlCommand command = new(
            "insert into recovery_code_hashes " +
            "(verifier_hash, credential_id, user_id, credential_type, created_at_utc) " +
            "values (@verifier_hash, @credential_id, @user_id, 'recovery_codes', @created_at_utc)",
            connection);
        command.Parameters.AddWithValue("verifier_hash", verifierHash);
        command.Parameters.AddWithValue("credential_id", credentialId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Writes one live session established by <paramref name="credentialId" />, on the superuser
    /// connection, and returns its id. Seeded rather than established through the domain because
    /// these tests are about the role's write surface, not about how the row is produced.
    /// </summary>
    /// <remarks>
    /// <c>('federated', 'locked')</c> rather than <c>'full'</c>: every caller passes the account's
    /// federated credential, and <c>CK_sessions_kind_matches_credential</c> refuses a full session
    /// opened by one. A seeding row that the schema rejects would fail these tests before they
    /// reached the grant matrix they are about.
    /// </remarks>
    private static async Task<Guid> InsertSessionAsync(
        NpgsqlConnection connection,
        Guid userId,
        Guid credentialId)
    {
        Guid sessionId = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            "insert into sessions " +
            "(id, user_id, credential_id, credential_type, kind, " +
            "created_at_utc, expires_at_utc, revoked_at_utc) " +
            "values (@id, @user_id, @credential_id, 'federated', 'locked', " +
            "@created_at_utc, @expires_at_utc, null)",
            connection);
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("credential_id", credentialId);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);
        command.Parameters.AddWithValue("expires_at_utc", SessionExpiryInstant);
        await command.ExecuteNonQueryAsync();
        return sessionId;
    }

    /// <summary>
    /// Sends one <c>update … set column = @value where id = @id</c> statement and returns the
    /// refusal. The SQL is a literal at every call site; the values are parameters, as they must
    /// be.
    /// </summary>
    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        NpgsqlConnection connection,
        string sql,
        object value,
        Guid rowId)
    {
        await using NpgsqlCommand command = BuildWrite(connection, sql, value, rowId);
        return await RefusalOfAsync(command);
    }

    /// <summary>
    /// The same expectation for a statement whose only parameter is the row id. A <c>delete</c> sets
    /// no column, so it has no <c>@value</c> to bind, and passing the id twice to satisfy the
    /// overload above would only work because Npgsql ignores a parameter the SQL never names.
    /// </summary>
    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(
        NpgsqlConnection connection,
        string sql,
        Guid rowId)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", rowId);
        return await RefusalOfAsync(command);
    }

    private static async Task<PostgresException> RefusalOfAsync(NpgsqlCommand command)
    {
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected PostgresException.");
    }

    /// <summary>
    /// Sends the same statement shape as <see cref="ThrowsPostgresExceptionAsync" /> but expects
    /// it to go through, returning the affected-row count for the caller to assert.
    /// </summary>
    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        object value,
        Guid rowId)
    {
        await using NpgsqlCommand command = BuildWrite(connection, sql, value, rowId);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The same permitted-write expectation for a statement whose only parameter is the row id, for
    /// the reason the matching <see cref="ThrowsPostgresExceptionAsync" /> overload exists: a
    /// <c>delete</c> sets no column, so it has no <c>@value</c> to bind.
    /// </summary>
    private static async Task<int> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        Guid rowId)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", rowId);
        return await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand BuildWrite(
        NpgsqlConnection connection,
        string sql,
        object value,
        Guid rowId)
    {
        NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("id", rowId);
        return command;
    }

    /// <summary>
    /// Reads one column of one row back so a refusal can be asserted as "nothing changed" and a
    /// permitted write as "this landed" — a rows-affected count alone cannot tell those apart
    /// from a statement that matched no row. A SQL NULL comes back as <see cref="DBNull.Value" />.
    /// </summary>
    /// <summary>
    /// The same read for a <c>bytea</c> column, pattern-matched to the array rather than returned as
    /// an object so a caller compares bytes instead of references.
    /// </summary>
    private static async Task<byte[]> SelectBytesAsync(
        NpgsqlConnection connection,
        string sql,
        Guid rowId) =>
        await SelectScalarAsync(connection, sql, rowId) switch
        {
            byte[] bytes => bytes,
            var unexpected => throw new InvalidOperationException(
                $"Expected a byte string, got '{unexpected ?? "null"}'."),
        };

    private static async Task<object?> SelectScalarAsync(
        NpgsqlConnection connection,
        string sql,
        Guid rowId)
    {
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", rowId);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>
    /// Builds a context bound to an ambient budget, which the budget-isolated <c>Accounts</c> set
    /// this file seeds through requires.
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
