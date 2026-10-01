using System.Net;
using System.Text.Json.Nodes;
using Domain.Sessions;
using Domain.Users;
using Npgsql;

namespace IntegrationTests;

/// <summary>
/// That the signed-in-client harness on <see cref="ApiFactory" /> hands back a client the application
/// really authenticates — and says out loud why, on a host where it cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>A test harness carrying tests of its own is proportion here, not ceremony.</b> Roughly three
/// hundred call sites in this assembly are about to reach the application through
/// <see cref="ApiFactory.CreateSignedInClientAsync" /> or <see cref="ApiFactory.RegisterAccountAsync" />,
/// and not one of them is <em>about</em> the harness: each arranges a session so it can ask a question
/// somewhere else entirely. A harness that stopped working would therefore be discovered as three
/// hundred reds none of which names it — the shape of failure a fixture is worst at surviving, because
/// the first instinct on reading it is that the application broke.
/// </para>
/// <para>
/// <b>Its worst failure mode is silent, and <see cref="CreateSignedInClientAsync_OnAHostNamingTheTestScheme_ThrowsNamingTheFlagThatFixesIt" />
/// is the one thing closing it.</b> Building on a host that named <see cref="TestAuthHandler" /> as the
/// default authenticate scheme takes the cookie handler off the path altogether, so a cookie-carrying
/// client answers 401 on every request with nothing in the response, the log or the assertion naming the
/// cause. The throw is what turns that into a sentence naming the flag that fixes it, and this file is
/// the only thing holding the throw.
/// </para>
/// <para>
/// <b>The locked and the full client are a negative and a positive control, not one assertion written
/// twice.</b> A harness that handed out a client the application refused outright would satisfy "a
/// locked session is refused budget content" perfectly. The full arm — same host, same route, same call
/// with one argument changed — is what makes the refusal a verdict on the kind that was asked for rather
/// than on the harness being broken. It is the argument <see cref="LockedSessionTests" /> makes about the
/// gate, applied one level down to the thing that seeds it.
/// </para>
/// <para>
/// <b>What is claimed here is the harness, not the rules it walks through.</b> FR-109's gate belongs to
/// <see cref="LockedSessionTests" /> and the ceremony to <see cref="AccountRegistrationTests" />; the
/// routes below are reached because they are the sharpest available reading of "this client is
/// authenticated, as who it says, with a budget", and restating either file's claims here would be a
/// second opinion about a rule that already has an owner.
/// </para>
/// </remarks>
public sealed class SignedInClientHarnessTests
{
    /// <summary>
    /// That <see cref="ApiFactory.RegisterAccountAsync" /> drives both real legs and hands back a client
    /// already presenting the session the finish leg opened, beside the two ids no response names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two routes rather than one, because they fail for different reasons.</b> <c>/api/me</c> answers
    /// on the cookie alone, so it reads the authentication and nothing else; <c>/api/accounts</c> also
    /// needs an ambient budget and a session whose kind may reach budget content, so it reads the whole
    /// shape a migrated call site depends on. A harness that resolved an identity but published no budget
    /// passes the first and fails the second.
    /// </para>
    /// <para>
    /// The email is read rather than the status alone: a 200 from <c>/api/me</c> proves somebody is signed
    /// in, and only the address proves it is the account this call was asked to register.
    /// </para>
    /// <para>
    /// Both ids are checked against <see cref="Guid.Empty" />, which is what an unfilled member of the
    /// returned record reads as — a harness that forgot either would hand back a value every later query
    /// matches nothing on, silently, with no exception thrown anywhere.
    /// </para>
    /// </remarks>
    [Test]
    public async Task RegisterAccountAsync_ReturnsAClientCarryingTheSessionTheCeremonyOpened()
    {
        // Arrange — both authentication flags on, which is what the ceremony needs: a test principal on
        // the provider scheme to drive the options leg, and the application's own cookie handler left
        // standing to read the session the finish leg sets.
        await using PostgresTestHost host = new(
            usesApplicationAuthentication: true, repointsProviderSchemeToTestHandler: true);
        await host.StartAsync();

        // Act
        ApiFactory.SignedInClient signedIn =
            await host.Factory.RegisterAccountAsync("scratch-registering");

        HttpResponseMessage me = await signedIn.Client.GetAsync("/api/me");
        HttpResponseMessage accounts = await signedIn.Client.GetAsync("/api/accounts");
        string body = await me.Content.ReadAsStringAsync();

        // Assert
        await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(accounts.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(JsonNode.Parse(body)!["email"]!.GetValue<string>())
            .IsEqualTo("scratch-registering@example.com");
        await Assert.That(signedIn.UserId).IsNotEqualTo(Guid.Empty);
        await Assert.That(signedIn.BudgetId).IsNotEqualTo(Guid.Empty);
    }

    /// <summary>
    /// That the kind asked for is the kind seeded: a locked client is refused budget content and a full
    /// one is served it, off one host and one route.
    /// </summary>
    /// <remarks>
    /// <b>The second assertion is the control, and the test is worthless without it.</b> A harness whose
    /// clients the application refused for any reason at all — a cookie it never set, a session it never
    /// wrote, a handler never asked — would pass the locked half exactly as written. Only the full arm,
    /// which differs from the locked one by the single <c>kind</c> argument, makes the 403 above a verdict
    /// on the session's kind rather than on the harness.
    /// </remarks>
    [Test]
    public async Task CreateSignedInClientAsync_SeedsTheKindItIsAskedFor()
    {
        // Arrange — one host, the application's own authentication standing, and two clients differing
        // only in the kind of session seeded behind them.
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();

        // Act
        ApiFactory.SignedInClient locked = await host.Factory.CreateSignedInClientAsync(
            "scratch-locked", kind: SessionKind.Locked);
        ApiFactory.SignedInClient full = await host.Factory.CreateSignedInClientAsync("scratch-full");

        // Assert
        await Assert.That((await locked.Client.GetAsync("/api/accounts")).StatusCode)
            .IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await full.Client.GetAsync("/api/accounts")).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// That a full session asked for over a set of recovery codes is a full session, and that seeding it
    /// files no passkey of any kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves, because either alone is satisfied by the arm this one replaces.</b> The default
    /// full client also reaches <c>/api/accounts</c>, so the status on its own says nothing about which
    /// credential opened the session; and a client that authenticated as nobody would file no passkey
    /// just as truthfully. Together they are the property every caller of this argument depends on: the
    /// session is <see cref="SessionKind.Full" /> and the account holds not one passkey row.
    /// </para>
    /// <para>
    /// <b>The counts are unscoped and on the container superuser connection.</b> What the callers assert
    /// is that a <em>refused</em> ceremony filed nothing anywhere, so the claim this harness owes them is
    /// about the whole database rather than about one account — and <c>passkey_signature_counters</c>
    /// carries <c>user_isolation</c>, which is <c>FOR ALL</c>, so a policed connection would report zero
    /// for a row that is there exactly as it does for one that is not.
    /// </para>
    /// <para>
    /// The credential row itself is counted too, and it is what tells this arm from one that seeded no
    /// credential at all: exactly one set, under this account.
    /// </para>
    /// </remarks>
    [Test]
    public async Task CreateSignedInClientAsync_OverRecoveryCodes_OpensAFullSessionAndFilesNoPasskey()
    {
        // Arrange
        await using PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();

        // Act
        ApiFactory.SignedInClient signedIn = await host.Factory.CreateSignedInClientAsync(
            "scratch-recovery-codes", opensWith: CredentialType.RecoveryCodes);
        HttpResponseMessage accounts = await signedIn.Client.GetAsync("/api/accounts");

        // Assert — a full session's reach, and not a passkey row anywhere in the database.
        await Assert.That(accounts.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await CountAsync(host, "select count(*) from passkey_public_keys")).IsEqualTo(0L);
        await Assert.That(await CountAsync(host, "select count(*) from passkey_signature_counters"))
            .IsEqualTo(0L);
        await Assert.That(await CountAsync(host, "select count(*) from credentials where type = 'passkey'"))
            .IsEqualTo(0L);
        await Assert.That(await CountAsync(
                host, $"select count(*) from credentials where type = 'recovery_codes' and user_id = '{signedIn.UserId}'"))
            .IsEqualTo(1L);
    }

    /// <summary>
    /// That asking for a cookie-carrying client on a host that named <see cref="TestAuthHandler" /> as its
    /// default scheme throws, naming the flag that fixes it.
    /// </summary>
    /// <remarks>
    /// <b>This is the test the whole file exists for.</b> Such a host never asks the cookie handler
    /// anything, so every request the returned client made would answer 401 with nothing anywhere naming
    /// the cause — and the author, whose test is about something else entirely, would go looking in the
    /// application. Failing at the call that built the client, in a sentence that names the constructor
    /// argument, is what turns a day into a line.
    /// </remarks>
    [Test]
    public async Task CreateSignedInClientAsync_OnAHostNamingTheTestScheme_ThrowsNamingTheFlagThatFixesIt()
    {
        // Arrange — the default host shape: TestAuthHandler named as the default authenticate scheme,
        // which is what every existing call site in this assembly builds.
        await using PostgresTestHost host = new();
        await host.StartAsync();

        // Act
        Exception? thrown = null;
        try
        {
            await host.Factory.CreateSignedInClientAsync("scratch-refused");
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }

        // Assert — on the remedy rather than on the exception type. A throw a reader cannot act on is
        // barely better than the 401 storm it replaced, so what is pinned is that the message hands back
        // the argument to change.
        await Assert.That(thrown?.Message).Contains("usesApplicationAuthentication: true");
    }

    /// <summary>
    /// Runs a counting query on the container superuser connection, refusing anything that is not a count.
    /// </summary>
    private static async Task<long> CountAsync(PostgresTestHost host, string sql)
    {
        await using NpgsqlConnection connection = new(host.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);

        return await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{sql}', got '{unexpected ?? "null"}'."),
        };
    }
}
