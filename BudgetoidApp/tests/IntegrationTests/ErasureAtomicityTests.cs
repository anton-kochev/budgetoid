using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Domain.Budgets;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Provisioning;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Erasure is one transaction: it either takes every row it covers or leaves every one of them
/// exactly as it found them. These tests drive the real HTTP pipeline rather than the handler
/// directly, so the guarantee is measured over a real database, a real connection and the real
/// execution strategy — the only place a rollback is a rollback rather than a list that was never
/// mutated.
/// </summary>
/// <remarks>
/// <para>
/// The fault is injected at the <b>second</b> of the handler's two saves,
/// <see cref="IUserRepository.DeleteAsync" />, after
/// <c>ITransactionRepository.DeleteAllForAmbientBudgetAsync</c> has already saved. That position is
/// what makes one test prove all three requirements at once: if the two saves ran in separate
/// transactions, the transactions delete would have committed and those rows would be gone. Failing
/// the <i>first</i> save would prove nothing, because nothing had been written yet.
/// </para>
/// <para>
/// Every count is read on <see cref="PostgresTestHost.ConnectionString" /> — the container superuser
/// — and never on the application role. Both isolation policies are <c>FOR ALL</c>, so a policed
/// connection reports zero rows for a row that is still there exactly as it does for one that is
/// gone: read on the app role, "nothing moved" could not fail.
/// </para>
/// <para>
/// The enumeration is the whole database rather than a written list of owned tables, and it comes
/// from <see cref="RowLevelSecurityCoverage.DiscoverAsync" /> — the same catalog reader the deploy
/// verifier uses. <c>currencies</c> and <c>__EFMigrationsHistory</c> stay in: a failed erasure must
/// not move those either, and after a successful one they are the two counts that are still
/// non-zero, which is what tells a live measurement from an empty list.
/// </para>
/// <para>
/// This is not <c>AccountErasureEndpointTests</c> restated. That file asserts what a <b>successful</b>
/// erasure removes, scoped to one account's ids; this one asserts that a <b>failed</b> one moves
/// nothing at all, anywhere.
/// </para>
/// <para>
/// <b>Nothing here counts transactions.</b> An EF interceptor tallying <c>BeginTransaction</c> would
/// pin the mechanism instead of the consequence, and would stay green for two transactions that merely
/// look like one. Revoking the app role's <c>DELETE</c> grant to force a real <c>42501</c> was the
/// other candidate, and it produces an error the execution strategy replays — turning this file into a
/// test about retries.
/// </para>
/// </remarks>
public sealed class ErasureAtomicityTests
{
    /// <summary>
    /// The Google subject every test here authenticates as. Named rather than left to the factory's
    /// default because the furnishing helper resolves the seeded ids by looking the credential up on
    /// it — a client and a lookup that disagreed would furnish one account and assert about another.
    /// </summary>
    private const string Subject = "google-erasing";

    /// <summary>
    /// The tables this file's counting helper leaves out, and the only one there is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The exclusion is the point, not a workaround.</b> <c>webauthn_challenges</c> is the one table
    /// whose <b>row count</b> the re-authentication gate moves outside the transaction boundary —
    /// <c>ConsumeAsync</c> deletes the spent nonce on its own save, deliberately, so that a rolled-back
    /// erasure cannot restore it and make the assertion replayable. The gate writes outside the boundary
    /// twice, not once: it also advances <c>passkey_signature_counters.signature_counter</c>, which moves
    /// a value and no count, which is why that table stays in the enumeration and is pinned by value in
    /// <see cref="Erasure_WhenTheUserDeleteFails_SpendsTheAssertionAnyway" /> instead. See
    /// <c>docs/business-logic/erasure.md</c>. Naming this one here with its reason is what makes the
    /// boundary visible in the test rather than only in prose.
    /// </para>
    /// <para>
    /// The tempting alternative — snapshot before the options leg, so the nonce is inserted and then
    /// deleted between the two reads — is refused on purpose. That count would balance by a coincidence
    /// of cardinality rather than by atomicity, and it would hide the boundary instead of naming it.
    /// </para>
    /// </remarks>
    private static readonly string[] TablesOutsideTheTransactionBoundary =
    [
        "webauthn_challenges", // written by the gate, which runs before the transaction opens
    ];

    /// <summary>
    /// The two tables a <b>successful</b> erasure leaves populated: reference data and EF's own
    /// bookkeeping, neither of which belongs to any account.
    /// </summary>
    private static readonly string[] TablesAnErasureDoesNotOwn =
    [
        "currencies",
        "__EFMigrationsHistory",
    ];

    /// <summary>
    /// The story: FR-019, FR-020 and NFR-017 in one test.
    /// </summary>
    /// <remarks>
    /// The fault type is <see cref="InvalidOperationException" /> rather than an
    /// <see cref="NpgsqlException" />, and that is load-bearing: the transactional delegate runs under
    /// <c>NpgsqlRetryingExecutionStrategy</c>, which treats a transient PostgreSQL failure as a reason
    /// to replay the whole delegate. A transient type here would quietly turn this into a retry test.
    /// </remarks>
    [Test]
    public async Task Erasure_WhenTheUserDeleteFails_LeavesEveryRowCountUnchanged()
    {
        // Arrange — a second factory over the same database, with the user delete failed. The
        // authenticated client comes from that factory rather than from host.Factory, or the request
        // under test would be served by a host the decorator was never applied to.
        await using PostgresTestHost host = await StartHostAsync();
        DeleteAttempts attempts = new();
        await using ApiFactory factory = host.CreateFactory(configureServices: FailTheUserDelete(attempts));
        (HttpClient client, Guid userId, _) = await factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await FurnishAccountAsync(host, client, Subject);
        await RegisterPasskeyAsync(client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — the options leg first, then the signature, and only then the snapshot. Taken before
        // seeding, every count becomes a seeding assertion rather than an atomicity one.
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));

        IReadOnlyDictionary<string, long> before = await CountEveryTableAsync(admin);
        HttpResponseMessage response = await PostErasureAsync(client, assertion);
        IReadOnlyDictionary<string, long> after = await CountEveryTableAsync(admin);

        // Assert — the probe first, because every claim below is also satisfied by a request that was
        // refused before the handler ran: a 404 is not a success, an untouched database has not drifted,
        // and a route deleted outright would produce both. This says the second save was reached. It is
        // pinned to exactly 1, which is what holds the decorator's choice of a non-transient exception
        // in place: a transient type would be replayed by NpgsqlRetryingExecutionStrategy and this would
        // read higher.
        await Assert.That(attempts.Count).IsEqualTo(1);

        // The exact status is deliberately unasserted. The stub's exception type is a detail of this
        // test, and pinning the status would make the test about error mapping.
        await Assert.That(response.IsSuccessStatusCode).IsFalse();

        // The non-vacuity guard, and it is not decoration: "every after-count equals its before-count"
        // is satisfied perfectly by an empty dictionary, and satisfied nearly as well by a database the
        // seeding never reached. Both halves are asserted — that the enumeration found tables at all,
        // and that every one of them held rows before the act.
        //
        // It will fire on the first table anyone adds, and the remedy then is one of two: seed the new
        // table in FurnishAccountAsync or SeedIdentityRowsAsync, or — only if it genuinely cannot be
        // seeded — name it in a list beside TablesOutsideTheTransactionBoundary with its reason on the
        // line. Deleting the guard is not one of them.
        await Assert.That(before).IsNotEmpty();
        await Assert.That(NamesOf(before.Where(table => table.Value == 0))).IsEmpty();

        // Nothing moved, anywhere, compared table by table rather than in aggregate.
        await Assert.That(DescribeDrift(before, after)).IsEmpty();

        // FR-019, stated on the one count that carries it. This is the second save's failure, so the
        // transactions delete had already run and saved: if the two saves were in separate
        // transactions, that delete would have committed and these rows would be gone. They are still
        // there, so the two saves shared one transaction. Redundant with the loop above by
        // construction, and kept anyway — the loop proves "nothing moved", and only this line says
        // which row count is the evidence for a single transaction.
        await Assert.That(after["transactions"]).IsEqualTo(before["transactions"]);
    }

    /// <summary>
    /// The counterweight, through the same enumeration the test above uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keep this test.</b> It looks redundant next to
    /// <c>AccountErasureEndpointTests.Erase_ForAFullyFurnishedAccount_LeavesNoRowInAnyTable</c>, which
    /// already proves a successful erasure empties every owned table. That is not what this one is for.
    /// What only this test shows is that the whole-database enumeration is a <b>live measurement</b>:
    /// run against the same helper, a successful erasure drives every counted table to zero except the
    /// two nobody owns. Without it, an enumeration that came back with an empty list — a filter that
    /// stopped matching, a catalog query that changed shape — would make the atomicity test's loop
    /// vacuously green and nothing in the suite would object.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Erasure_WhenNothingFails_RemovesEveryOwnedRow()
    {
        // Arrange — no decorator here; this is the ordinary path through the real repository, on the
        // host's own factory.
        await using PostgresTestHost host = await StartHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await FurnishAccountAsync(host, client, Subject);
        await RegisterPasskeyAsync(client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — the same shape as the test above, so the two are comparable line for line.
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));

        IReadOnlyDictionary<string, long> before = await CountEveryTableAsync(admin);
        HttpResponseMessage response = await PostErasureAsync(client, assertion);
        IReadOnlyDictionary<string, long> after = await CountEveryTableAsync(admin);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        // The same non-vacuity guard, for the same reason and with the same remedy: the zeros below are
        // what an unseeded database looks like too.
        await Assert.That(before).IsNotEmpty();
        await Assert.That(NamesOf(before.Where(table => table.Value == 0))).IsEmpty();
        // Down to baseline, in both directions. Every owned table is empty…
        await Assert.That(NamesOf(after.Where(table =>
                !TablesAnErasureDoesNotOwn.Contains(table.Key, StringComparer.Ordinal)
                && table.Value != 0)))
            .IsEmpty();

        // …and the two tables no account owns are still populated. This half is what separates a working
        // erasure from a truncated database. It walks the two names rather than filtering `after`,
        // which is what lets it also catch an enumeration that stopped returning them: a filter over
        // `after` matches nothing when the key is missing and passes, while GetValueOrDefault reads the
        // absent key as zero and fails on it by name.
        await Assert.That(NamesOf(TablesAnErasureDoesNotOwn
                .Select(name => KeyValuePair.Create(name, after.GetValueOrDefault(name)))
                .Where(table => table.Value == 0)))
            .IsEmpty();
    }

    /// <summary>
    /// The boundary pin: a failed erasure still spends the assertion that authorized it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two-sided by construction, which is what makes it a guard rather than a description. It goes red
    /// if the gate is ever moved <b>inside</b> the transaction — the rollback would restore the spent
    /// nonce and rewind the counter, so the first two assertions would read 1 and 0 — and it goes red
    /// if the gate stops consuming at all, because the replay would then be accepted rather than
    /// refused.
    /// </para>
    /// <para>
    /// The counter is pinned by <b>value</b> and not by a row count, and that is the one thing this
    /// file's counting helper structurally cannot see: <c>SaveCounterAsync</c> advances a column
    /// without moving a count. The device registers at <c>0</c> and
    /// <see cref="SyntheticAuthenticator.Authenticate" /> signs at <c>1</c>, so <c>1</c> is the value an
    /// accepted assertion leaves behind.
    /// </para>
    /// <para>
    /// This is a different cause from
    /// <c>ErasureReauthenticationTests.Erasure_WhenVerificationFails_StillConsumesTheChallenge</c>,
    /// which spends the nonce on an assertion that <i>failed to verify</i>. Here the assertion verifies
    /// completely and the <i>erasure</i> is what fails, which is the only path on which "the gate
    /// committed and the erasure did not" is observable at all.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Erasure_WhenTheUserDeleteFails_SpendsTheAssertionAnyway()
    {
        // Arrange — the same decorator and the same seeding as the atomicity test.
        await using PostgresTestHost host = await StartHostAsync();
        DeleteAttempts attempts = new();
        await using ApiFactory factory = host.CreateFactory(configureServices: FailTheUserDelete(attempts));
        (HttpClient client, Guid userId, _) = await factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await FurnishAccountAsync(host, client, Subject);
        await RegisterPasskeyAsync(client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // Act — one ceremony, answered twice. The second post replays the identical body rather than
        // beginning another options leg, so the only two things that can refuse it are the writes the
        // gate already committed: the nonce the first attempt spent, and the counter it advanced, which
        // sends PasskeySignatureCounter.Accept(1) against a stored 1 down its reported <= Value branch.
        // Either refusal produces the same 401, so the status below does not tell them apart — the two
        // assertions above it do, one per write.
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));

        HttpResponseMessage failed = await PostErasureAsync(client, assertion);
        HttpResponseMessage replayed = await PostErasureAsync(client, assertion);

        // Assert — the probe first, so the claims below are about a request that reached the second save
        // rather than one refused ahead of the handler. Exactly 1 across both posts, which is also the
        // replay never arriving there.
        await Assert.That(attempts.Count).IsEqualTo(1);

        // Then the failure, so the three claims below are about a request that really did fail. Its
        // status stays unasserted for the same reason as in the atomicity test.
        await Assert.That(failed.IsSuccessStatusCode).IsFalse();

        // The spent nonce did not come back with the rollback. Counted unscoped, because the point is
        // that the table is empty rather than that one particular row went.
        await Assert.That(await ScalarAsync(admin, "select count(*) from webauthn_challenges"))
            .IsEqualTo(0L);

        // And the clone detector did not rewind. Read for this device's own credential, because the
        // account also holds a passkey seeded out of band whose counter no ceremony ever touches.
        await Assert.That(await ReadSignatureCounterAsync(admin, device.CredentialId)).IsEqualTo(1L);

        // The replay is refused, which is what the two assertions above amount to for a caller: a
        // failed erasure costs the person one ceremony, and they have to repeat it. Correct rather than
        // a defect — see docs/business-logic/erasure.md.
        await Assert.That(replayed.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Fails the second of the erasure's two saves, leaving everything before it to run for real.
    /// </summary>
    /// <remarks>
    /// A <b>decorator</b> over the real repository rather than a bare stub, and the reason is that
    /// every member but the one under test has to keep working for the request to arrive at all. The
    /// erasure path reads <see cref="IUserRepository.FindUserIdByFederatedCredentialAsync" />, and a
    /// stub answering <see langword="null" /> there refuses the request before the handler is ever
    /// reached — whereupon the test reads as a broken fixture rather than as an atomicity failure.
    /// </remarks>
    /// <param name="attempts">
    /// The tally the refused calls are recorded on. It is a parameter, and so an object the test owns
    /// and keeps a reference to, rather than a field on the decorator: <c>ActivatorUtilities</c> builds
    /// the repository once per scope, so a field would be discarded with the scope that produced it and
    /// the test would have nothing left to read.
    /// </param>
    private static Action<IServiceCollection> FailTheUserDelete(DeleteAttempts attempts) =>
        services => services.Replace(ServiceDescriptor.Scoped<IUserRepository>(provider =>
            // The real repository underneath, resolved rather than newed up so this test does not own
            // its constructor.
            new FailingUserRepository(
                ActivatorUtilities.CreateInstance<UserRepository>(provider),
                attempts)));

    /// <summary>
    /// How many times the erasure asked for the user delete, counted rather than flagged so a single
    /// attempt can be told from a replayed one.
    /// </summary>
    private sealed class DeleteAttempts
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Record() => Interlocked.Increment(ref count);
    }

    /// <summary>
    /// Throws on the user delete and on nothing else; both other members are the real thing.
    /// </summary>
    private sealed class FailingUserRepository(IUserRepository inner, DeleteAttempts attempts) : IUserRepository
    {
        // Delegated for real. This is on the erasure request's OWN path — see FailTheUserDelete.
        public Task<Guid?> FindUserIdByFederatedCredentialAsync(
            string provider,
            string subject,
            CancellationToken cancellationToken = default) =>
            inner.FindUserIdByFederatedCredentialAsync(provider, subject, cancellationToken);

        // Non-transient on purpose. An NpgsqlException would be replayed by
        // NpgsqlRetryingExecutionStrategy, which runs the whole transactional delegate again and turns
        // this into a test about retries — and the tally, pinned to exactly 1, is what keeps that choice
        // honest rather than merely stated.
        public Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            // Recorded before the throw, so a call that was made is a call that is counted.
            attempts.Record();

            throw new InvalidOperationException(
                "Simulated failure after the transactions delete was saved and before the user was.");
        }
    }

    private const string ErasurePath = "/api/me/erasure";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";

    /// <summary>
    /// Counts every relation in the database that can hold rows of its own, discovered rather than
    /// listed, minus the two kinds that would only count rows already counted elsewhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RowLevelSecurityCoverage.DiscoverAsync" /> is the production catalog reader the deploy
    /// verifier uses; it already reads every row-bearing relation in <c>public</c> and already owns the
    /// decision about which <c>relkind</c> values matter. Reusing it means a table added tomorrow is
    /// counted tomorrow, with nothing here to remember to update.
    /// </para>
    /// <para>
    /// The two exclusions are <b>double-counting</b> and nothing else, which is why they are written as
    /// a refusal of two kinds rather than as a filter to one. A <see cref="RelationKind.View" /> reports
    /// rows that live in the tables underneath it, and a <see cref="RelationKind.PartitionedTable" />
    /// parent reports the rows of its partitions — each of which is a
    /// <see cref="RelationKind.OrdinaryTable" /> counted in its own right. Counting either would say a
    /// row moved twice, or that a table nobody wrote to drifted. Written this way round, a relation kind
    /// added to <see cref="RelationKind" /> later is counted rather than silently skipped.
    /// </para>
    /// <para>
    /// <b>A <see cref="RelationKind.MaterializedView" /> is counted, and "nothing in a request writes to
    /// it" is not a reason to leave it out — it is the reason to put it in.</b> Its rows are stored, so
    /// an erasure does not reach them: a reporting matview over <c>transactions</c> keeps an erased
    /// budget's money movement until somebody refreshes it. Left uncounted it clears every gate at once
    /// — this file's counts skipped it, <c>ErasureRemnantVocabulary</c> reads only its <i>name</i>, and
    /// the one red it does raise is <c>RowLevelSecurityCoverage.Classify</c> putting it in
    /// <see cref="SchemaClassification.Unpoliceable" />, whose exemption conversation is about
    /// isolation and never about erasure. An exemption written there for the right isolation reason
    /// would leave the remnant standing.
    /// </para>
    /// <para>
    /// A <see cref="RelationKind.ForeignTable" /> is counted by the same <c>select count(*)</c>, and the
    /// honest limit is that <b>no test here exercises one</b>: a foreign table needs a foreign-data
    /// wrapper and a server, and this database has neither. Measured against <c>postgres:17</c> outside
    /// this suite, the count returns a number over a wrapper with a handler and a reachable source
    /// (<c>file_fdw</c>), and fails outright with <c>foreign-data wrapper … has no handler</c> over one
    /// declared without a handler. Both directions are acceptable here and neither is silent: a number
    /// is compared like any other, and a failure stops the test rather than reporting a relation as
    /// having moved no rows.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, long>> CountEveryTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DiscoveredTable> discovered =
            await RowLevelSecurityCoverage.DiscoverAsync(connection, cancellationToken);
        Dictionary<string, long> counts = new(StringComparer.Ordinal);

        foreach (DiscoveredTable table in discovered)
        {
            if (table.Kind is RelationKind.View or RelationKind.PartitionedTable
                || TablesOutsideTheTransactionBoundary.Contains(table.Name, StringComparer.Ordinal))
            {
                continue;
            }

            // The name comes from pg_class rather than from anything a caller supplies, and it is
            // quoted because __EFMigrationsHistory is stored exactly as EF quoted it — unquoted, an
            // identifier folds to lower case and the table is not found. Schema-qualified because the
            // discovery reads public and only public, and an unqualified name resolves through
            // search_path instead.
            counts[table.Name] = await ScalarAsync(
                connection,
                $"select count(*) from public.\"{table.Name}\"",
                cancellationToken);
        }

        return counts;
    }

    /// <summary>
    /// One sentence per table whose count moved, so a failure says which table drifted and by how much
    /// rather than only that something did. An empty list is the whole claim holding.
    /// </summary>
    private static IReadOnlyList<string> DescribeDrift(
        IReadOnlyDictionary<string, long> before,
        IReadOnlyDictionary<string, long> after) =>
    [
        .. before
            .Where(table => !after.TryGetValue(table.Key, out long count) || count != table.Value)
            .Select(table => after.TryGetValue(table.Key, out long count)
                ? $"{table.Key}: {table.Value} -> {count}"
                : $"{table.Key}: {table.Value} -> the table is no longer discovered"),

        // The other direction too: a table that appeared between the two reads is a row count that
        // moved from nothing, and comparing only the before-set would walk straight past it.
        .. after.Keys
            .Where(name => !before.ContainsKey(name))
            .Select(name => $"{name}: not discovered before -> {after[name]}"),
    ];

    /// <summary>
    /// The table names out of a count sequence, so an assertion failure names the offenders instead of
    /// reporting a number.
    /// </summary>
    private static IReadOnlyList<string> NamesOf(IEnumerable<KeyValuePair<string, long>> tables) =>
        [.. tables.Select(table => $"{table.Key} = {table.Value}").Order(StringComparer.Ordinal)];

    /// <summary>
    /// Reads the counter one registered authenticator's credential is filed under.
    /// </summary>
    /// <remarks>
    /// Joined through <c>passkey_public_keys.webauthn_credential_id</c> because that is the only column
    /// naming the device: the account here also holds a passkey seeded out of band, whose counter no
    /// ceremony ever advances, so an unscoped read could pass on the wrong row.
    /// </remarks>
    private static async Task<long> ReadSignatureCounterAsync(
        NpgsqlConnection connection,
        byte[] webAuthnCredentialId)
    {
        await using NpgsqlCommand command = new(
            """
            select counters.signature_counter
            from passkey_signature_counters counters
            join passkey_public_keys keys on keys.credential_id = counters.credential_id
            where keys.webauthn_credential_id = @handle
            """,
            connection);
        command.Parameters.AddWithValue("handle", webAuthnCredentialId);

        return await ReadLongAsync(command, "passkey_signature_counters.signature_counter");
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return await ReadLongAsync(command, sql, cancellationToken);
    }

    /// <summary>
    /// Reads a <see cref="long" />, refusing anything else. Pattern-matched rather than
    /// cast-and-null-forgive: a null or unexpected scalar means the query changed shape or matched no
    /// row, and that should fail loudly here instead of at the assertion.
    /// </summary>
    private static async Task<long> ReadLongAsync(
        NpgsqlCommand command,
        string source,
        CancellationToken cancellationToken = default) =>
        await command.ExecuteScalarAsync(cancellationToken) switch
        {
            long value => value,
            var unexpected => throw new InvalidOperationException(
                $"Expected a number from '{source}', got '{unexpected ?? "null"}'."),
        };

    /// <summary>
    /// Runs both authenticated legs of a registration so the account holds a passkey a signature can
    /// actually be verified against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real ceremony rather than seeded rows: <see cref="SeedIdentityRowsAsync" /> writes four bytes
    /// of stand-in key material that no private key answers to, so an assertion checked against it
    /// could never verify and every test here would be refused before it reached the handler.
    /// </para>
    /// <para>
    /// This mints no account and needs none minted here: every caller is handed its client by
    /// <see cref="ApiFactory.CreateSignedInClientAsync" />, which seeds the whole account behind it.
    /// Neither passkey leg provisions anything, so a client arriving without an account is refused with
    /// a 401 for a reason no test here is about.
    /// </para>
    /// </remarks>
    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(challenge, ApiFactory.PasskeyOrigin, prfEnabled: true);
        WrappedKeyFixture keys = WrappedKeyFixture.Mint();
        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedContentKey = keys.WrappedContentKey,
            wrappedIndexKey = keys.WrappedIndexKey,
        });
        response.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> PostErasureAsync(HttpClient client, AssertionResult assertion) =>
        client.PostAsJsonAsync(ErasurePath, new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        JsonNode options = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;

        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// Writes one row into every table an account can own and returns the ids the assertions key on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The money data goes in over HTTP, so every row is one the application itself could really have
    /// written — through the same validation, the same repositories and the same least-privilege role a
    /// request uses. The identity rows have no endpoint that creates them yet, so they are written out
    /// of band on the container superuser, through the domain factories rather than raw SQL.
    /// </para>
    /// <para>
    /// Duplicated from <c>AccountErasureEndpointTests</c> rather than extracted, which is the local
    /// convention: <c>ErasureReauthenticationTests</c> already carries its own copy, and a drive-by
    /// extraction across three files is a change to those files rather than to this one.
    /// </para>
    /// </remarks>
    private static async Task<(Guid UserId, Guid BudgetId)> FurnishAccountAsync(
        PostgresTestHost host,
        HttpClient client,
        string subject)
    {
        Guid accountId = await CreateAsync(client, "/api/accounts", new
        {
            // Sealed, indexed and identified through SealedNarrative rather than sent as the word
            // "Checking": accounts.name is an AEAD envelope and accounts.name_key a blind index, so a flat
            // name is a 400 from CreateAccountHandler and this seeding would never reach the subject of
            // the test. The id is on the body because the client mints it — it is the associated data the
            // name was sealed against, so this API has to hand back the spelling it was sent.
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Checking"),
            nameKey = SealedNarrative.EncodedIndex("Checking"),
            type = "Checking",
            openingBalance = 0m,
            currencyCode = "USD",
        });
        Guid categoryGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            name = "Essentials",
            description = (string?)null,
        });
        Guid categoryId = await CreateAsync(client, "/api/categories", new
        {
            name = "Groceries",
            description = (string?)null,
            categoryGroupId,
        });

        // The category is what makes the transactions → categories RESTRICT edge live.
        // The payee is a request of its own now: POST /api/transactions takes an identifier, and the
        // server can no longer resolve a name into a row — payees.name is an AEAD envelope drawn under
        // a fresh nonce, so two seals of one name are different bytes. Seeded here rather than dropped
        // because a budget with no payee row would leave this file measuring one relation fewer than
        // its name claims, silently.
        Guid payeeId = await CreateAsync(client, "/api/payees", new
        {
            id = Guid.CreateVersion7().ToString("D"),
            name = SealedNarrative.EncodedName("Starbucks"),
            nameKey = SealedNarrative.EncodedIndex("Starbucks"),
        });
        await CreateAsync(client, "/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = "Coffee",
            payeeId,
            categoryId,
        });

        (Guid userId, Guid budgetId) = await ResolveOwnerAsync(host, subject);
        await SeedIdentityRowsAsync(host, userId);
        return (userId, budgetId);
    }

    /// <summary>
    /// Reads back the user and default budget that provisioning minted for
    /// <paramref name="subject" />. Nothing the API returns names either id, so the lookup goes through
    /// the credential the middleware resolved the request on.
    /// </summary>
    private static async Task<(Guid UserId, Guid BudgetId)> ResolveOwnerAsync(
        PostgresTestHost host,
        string subject)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            select credentials.user_id, budgets.id
            from credentials
            join budgets on budgets.user_id = credentials.user_id
            where credentials.provider = 'google' and credentials.subject = @subject
            """,
            connection);
        command.Parameters.AddWithValue("subject", subject);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"Provisioning wrote no account for subject '{subject}'.");
        }

        (Guid userId, Guid budgetId) = (reader.GetGuid(0), reader.GetGuid(1));

        // A second row would mean two budgets, which the product cannot produce — and would silently
        // scope the seeding to whichever one came back first.
        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"Subject '{subject}' owns more than one budget; the seeding assumes exactly one.");
        }

        return (userId, budgetId);
    }

    /// <summary>
    /// Adds the passkey material, the session row and the handle it is presented by, the set of
    /// recovery codes and the wrapped account keys, so the whole-database enumeration has something to
    /// find in every user-owned table rather than only in the two provisioning fills. Without it the
    /// non-vacuity guard fails on <c>sessions</c>, on <c>session_tokens</c> and on
    /// <c>recovery_code_hashes</c>, which is the point of the guard.
    /// <c>wrapped_account_keys</c> would survive it — <see cref="RegisterPasskeyAsync" /> writes a row
    /// of that route's own — and the seeded row is kept beside it for the reason below.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written out of band on the container superuser, through the domain factories rather than
    /// through the routes that will one day mint these rows — and that stays the right choice after
    /// those routes exist. What this file measures is erasure's transaction boundary, and what it
    /// needs from an arrangement is a row in every counted table. Reaching them through an issuance
    /// route would tie every test here to that route's request shape, its own authorization and its
    /// own rules about how large a set is, so a change to any of them would redden an erasure test for
    /// a reason that has nothing to do with erasure. The factories are what keep the seeded rows the
    /// shape production writes; the superuser connection is what keeps the arrangement independent of
    /// the application role's write surface, which <c>AppRoleGrantsTests</c> measures on its own.
    /// </para>
    /// </remarks>
    private static async Task SeedIdentityRowsAsync(PostgresTestHost host, Guid userId)
    {
        await using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql(host.ConnectionString)
                .Options);

        Credential passkey = Credential.CreatePasskey(userId, SeedInstant);
        db.Credentials.Add(passkey);
        db.PasskeyPublicKeys.Add(PasskeyPublicKey.Register(
            passkey, WebAuthnCredentialIdFor(userId), CoseKey, CoseAlgorithm.Es256));
        db.PasskeySignatureCounters.Add(PasskeySignatureCounter.Start(passkey, 0));

        // The account's two keys as this passkey factor holds them, in wrapped_account_keys — a table
        // the enumeration discovers, and one the non-vacuity guard covers like any other. The
        // registration route writes a row of its own now, so this one is no longer the only thing
        // standing between that guard and a zero; it is kept for the reason the remarks give, which is
        // that this file's arrangement stays independent of any issuance route's shape. Filed against
        // the passkey seeded just above rather than against the registered credential for that same
        // reason and no other: a second row hung off that credential is perfectly storable — the key is
        // factor_id, credential_id is an ordinary non-unique column, and a credential carries as many
        // rows as it has factors — but naming that credential here would make this seed depend on the
        // registration route having run, which is exactly the tie the remarks refuse. A passkey
        // rather than the recovery-code set below because a passkey is the factor whose PRF output
        // derives the key-encryption key in production; either is legal here, the federated credential
        // is not.
        db.WrappedAccountKeys.Add(WrappedAccountKeys.For(
            passkey,

            // Minted here rather than derived from the owner, which is what production does: the value
            // is chosen by the client and it is the table's primary key, so its uniqueness is global
            // rather than per account. Nothing asserts on it, and a fresh one per call is what keeps two
            // seeded accounts from colliding on that key.
            Guid.CreateVersion7(),
            Envelope(0xC0),
            Envelope(0x1D),
            SeedInstant));

        // Established against the passkey rather than the federated credential because
        // CK_sessions_kind_matches_credential ties the two together.
        Session session = Session.Establish(passkey, SeedInstant, SeedInstant.AddDays(14));
        db.Sessions.Add(session);

        // The handle that session is presented by, in session_tokens — a table the enumeration
        // discovers and the non-vacuity guard covers like any other, and one the guard was already
        // reporting as a zero. Nothing in the product issues a token yet, which is exactly why the row
        // has to be seeded here: a table that only ever holds zero rows would make every "nothing
        // moved" and every "everything went" assertion about it vacuously true, and the guard refusing
        // to count an empty relation is that refusal working rather than an obstacle.
        //
        // Through SessionToken.For and in the same SaveChangesAsync as the session, for the reason the
        // remarks above give about every other row here: the factory is what keeps a seeded row the
        // shape production writes, and a token committed without its session — or the reverse — is a
        // state no write path can produce. The factory reads both ids off the loaded session, so
        // nothing here can file a handle against another account's sign-in.
        db.SessionTokens.Add(SessionToken.For(session, SessionTokenFor(userId)));

        // One credential for the whole set — IX_credentials_user_id_recovery_codes admits no second
        // one — carrying SeededRecoveryCodeCount codes rather than one. See that constant for why the
        // count is what makes the erasure observable as a set going rather than as a row going.
        Credential recoveryCodes = Credential.CreateRecoveryCodes(userId, SeedInstant);
        db.Credentials.Add(recoveryCodes);

        for (int ordinal = 0; ordinal < SeededRecoveryCodeCount; ordinal++)
        {
            db.RecoveryCodeHashes.Add(RecoveryCodeHash.From(
                recoveryCodes, RecoveryCodeVerifierFor(userId, ordinal), SeedInstant));
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The token the seeded session is presented by: the owner's id, then zero padding, at the exact
    /// width <see cref="SessionToken.TokenLength" /> names.
    /// </summary>
    /// <remarks>
    /// Derived from the owner rather than random, so two accounts seeded in one database cannot hash
    /// alike — <c>token_hash</c> is the primary key, and a shared token would make the second seeding
    /// fail with a <c>23505</c> that has nothing to do with erasure. The width is read off the domain
    /// because <see cref="SessionToken.For" /> refuses any other, from both sides.
    /// </remarks>
    private static byte[] SessionTokenFor(Guid userId)
    {
        byte[] token = new byte[SessionToken.TokenLength];

        // The bool is the one failure this call has: a TokenLength shortened below the sixteen bytes of
        // a Guid would leave a token with no owner in it, and two accounts would collide on the key.
        if (!userId.TryWriteBytes(token))
        {
            throw new InvalidOperationException(
                $"A token of {SessionToken.TokenLength} bytes has no room for an owner id.");
        }

        return token;
    }

    /// <summary>
    /// How many unredeemed codes the seeded set holds, and it is deliberately more than one.
    /// </summary>
    /// <remarks>
    /// A set of one cannot tell "the erasure took the set" from "the erasure took a row" — both leave
    /// the table at zero. One <see cref="Credential" /> per set and one row per code is the whole
    /// shape of <c>recovery_code_hashes</c>, and it is what the cascade from <c>credentials</c> has to
    /// carry away wholesale.
    /// </remarks>
    private const int SeededRecoveryCodeCount = 3;

    /// <summary>
    /// One verifier of the seeded set: the owner's id, zero padding, and the code's ordinal in the
    /// last byte.
    /// </summary>
    /// <remarks>
    /// Distinct in both directions, and both are load-bearing. <c>verifier_hash</c> is the primary
    /// key, so two codes of one set derived from the same bytes would hash alike and be one row —
    /// which is the ordinal — and two accounts seeded alike would make the second one unstorable,
    /// which is the owner's id. The width is <see cref="RecoveryCodeHash.VerifierLength" /> because
    /// <see cref="RecoveryCodeHash.From" /> refuses any other, from both sides.
    /// </remarks>
    private static byte[] RecoveryCodeVerifierFor(Guid userId, int ordinal)
    {
        byte[] verifier = new byte[RecoveryCodeHash.VerifierLength];

        // Written straight into the buffer rather than through ToByteArray().CopyTo, so there is no
        // intermediate array — and the bool is the one failure this call has: a VerifierLength
        // shortened below the sixteen bytes of a Guid, which would otherwise leave a verifier with no
        // owner in it and make two accounts collide on the primary key.
        if (!userId.TryWriteBytes(verifier))
        {
            throw new InvalidOperationException(
                $"A verifier of {RecoveryCodeHash.VerifierLength} bytes has no room for an owner id.");
        }

        // checked, so a SeededRecoveryCodeCount raised past a byte overflows here rather than wrapping
        // to an ordinal already used — a repeated ordinal is a repeated verifier, which is one row
        // where the seeding meant two, and the guard this whole change exists for would still pass.
        verifier[^1] = checked((byte)ordinal);

        return verifier;
    }

    /// <summary>
    /// Posts <paramref name="body" /> and returns the id of the row it created, failing loudly on any
    /// status other than success — a furnishing step that quietly did nothing would make the
    /// non-vacuity guard the only thing to notice, one assertion too late.
    /// </summary>
    private static async Task<Guid> CreateAsync(HttpClient client, string path, object body)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        JsonNode json = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return json["id"]!.GetValue<Guid>();
    }

    /// <summary>
    /// Fixed UTC instant for the out-of-band rows. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// A 32-byte authenticator handle, derived from the owner so two seeded accounts never share one.
    /// The length satisfies <c>CK_passkey_public_keys_webauthn_credential_id_length</c> and the
    /// derivation satisfies the unique index over the column.
    /// </summary>
    private static byte[] WebAuthnCredentialIdFor(Guid userId) =>
        [.. userId.ToByteArray(), .. userId.ToByteArray()];

    /// <summary>
    /// Four bytes of stand-in key material. Nothing verifies a signature against this row — the tests
    /// here verify against a registered device — and the only rule the column holds is a length.
    /// </summary>
    private static readonly byte[] CoseKey = [0xA5, 0x01, 0x02, 0x03];

    /// <summary>
    /// A well-formed wrapped-key envelope: the one version byte the contract defines, then filler.
    /// </summary>
    /// <remarks>
    /// The filler is neither a nonce nor a ciphertext, and nothing here opens either — no unlock path
    /// exists and this server holds no value that could. What the row has to satisfy is the width and
    /// the version, which <see cref="WrappedAccountKeys.For" /> and two check constraints per column
    /// both refuse to bend. The two callers pass different fillers so the columns can be told apart by
    /// eye in a failure message.
    /// </remarks>
    private static byte[] Envelope(byte filler)
    {
        byte[] envelope = new byte[WrappedAccountKeys.EnvelopeLength];
        Array.Fill(envelope, filler);
        envelope[0] = WrappedAccountKeys.EnvelopeVersion;

        return envelope;
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because every request
    /// in this file authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    /// <remarks>
    /// The whole-database enumeration is unaffected by what the seeding adds: every count here is read
    /// twice and compared against its own earlier value, or — in
    /// <see cref="Erasure_WhenNothingFails_RemovesEveryOwnedRow" /> — against zero, which the seeded
    /// session and its handle reach through the cascade from <c>credentials</c> like everything else the
    /// account owns.
    /// </remarks>
    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
