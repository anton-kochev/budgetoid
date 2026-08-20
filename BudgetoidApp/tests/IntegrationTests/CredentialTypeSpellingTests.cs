using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Application.Passkeys;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// That the <c>type</c> a credential leaves <c>GET /api/me/credentials</c> under is the same token the
/// <c>credentials.type</c> column holds for that very row — for a set of recovery codes in particular,
/// and for every member of <see cref="CredentialType" /> in general.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two independent sides, and naming where each comes from is half of what this file is for.</b> The
/// <em>wire</em> side is whatever <c>CredentialEndpoints</c> puts in the <c>type</c> member of a list
/// entry; it is read over real HTTP and never computed here, so a test cannot agree with the endpoint by
/// reproducing its arithmetic. The <em>column</em> side is what the schema stores: read back from the
/// <c>credentials.type</c> column of the same row for the per-row comparison, and produced by
/// <c>CredentialConfiguration</c>'s own value converter — reached through the built EF model rather than
/// copied — wherever a spelling is needed for a member no row carries yet. Those two are the only
/// sources; a literal appears in this file exactly once, in
/// <see cref="Credentials_ASetOfRecoveryCodes_ArrivesAsTheSchemasOwnToken" />, and it is written out
/// there for the reason that test gives.
/// </para>
/// <para>
/// <b>Why this can go wrong at all.</b> <c>Api/Program.cs</c> registers <c>JsonStringEnumConverter</c>
/// with <b>no</b> naming policy, so an enum handed straight to the serializer arrives PascalCase and
/// disagrees with the column, its check constraint and the export document. The endpoint therefore
/// converts the member itself. Doing that with a camel-case naming policy over <c>ToString()</c> agrees
/// with the column only while every member is a single word: the column's vocabulary is snake_case, and
/// camel-casing <c>RecoveryCodes</c> produces <c>recoveryCodes</c> where the column says
/// <c>recovery_codes</c>. Nothing in either suite noticed, because until now no test listed an account
/// that held a set.
/// </para>
/// <para>
/// <b><see cref="Credentials_EveryCredentialTypeArrivesSpelledTheWayItsColumnIsSpelled" /> is driven from
/// the enum rather than from a list of cases</b>, and that is deliberate: the defect above is a property
/// of <em>how</em> the spelling is derived, not of the one member that happens to expose it today, so a
/// pin naming <c>recovery_codes</c> alone would be satisfied by special-casing that member and would say
/// nothing about the fourth. It enumerates <see cref="CredentialType" />, asks the schema's own converter
/// how each member is spelled, and refuses to run unless the account really holds one credential per
/// member — so a member declared tomorrow turns this red the day it is declared rather than the day a
/// client notices.
/// </para>
/// <para>
/// This file names <see cref="CredentialType" /> and the persistence model on purpose, unlike
/// <c>CredentialListEndpointTests</c>, which deliberately names no production type. The subject here
/// <em>is</em> the agreement between a declared enum and a column, and a test that could not name the
/// enum could not enumerate it.
/// </para>
/// <para>
/// Every row is read on <see cref="PostgresTestHost.ConnectionString" /> — the container superuser —
/// never on the application role: <c>credentials</c> is exempt from row-level security, so the expected
/// values have to be read without the endpoint's own owner predicate standing between the query and the
/// rows.
/// </para>
/// </remarks>
public sealed class CredentialTypeSpellingTests
{
    private const string CredentialsPath = "/api/me/credentials";
    private const string RecoveryCodesPath = "/api/me/recovery-codes";

    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";

    /// <summary>The account under test in both tests here.</summary>
    private const string Subject = "google-spelling";

    /// <summary>
    /// The token <c>credentials.type</c> holds for a set of recovery codes, written out rather than read
    /// off <see cref="CredentialType" /> or off the converter.
    /// </summary>
    /// <remarks>
    /// A test taking its expectation from the thing under test agrees with whatever that thing later
    /// decides — which is the whole failure mode here, since the wrong spelling is itself derived from the
    /// member's name. <c>CK_credentials_type</c> and the export document both spell it this way, and this
    /// constant is the executable form of that sentence.
    /// </remarks>
    private const string RecoveryCodesColumnValue = "recovery_codes";

    /// <summary>How many codes an issued set holds, as the generation route requires.</summary>
    private const int RequiredCodeCount = 10;

    /// <summary>The exact width of a verifier, decoded.</summary>
    private const int VerifierLength = 32;

    /// <summary>
    /// An account that really holds a set of recovery codes has that set listed as
    /// <c>recovery_codes</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The narrow, concrete half of this file: one member, one token, and a failure message that names
    /// both spellings side by side. The general claim lives in the test below; this one exists because
    /// the general claim is a comparison between two computed sequences, and a reader meeting a red run
    /// for the first time deserves a line that simply says the wire said one thing and the schema says
    /// another.
    /// </para>
    /// <para>
    /// <b>The set is issued through the real ceremony rather than seeded.</b> Issuing is gated on a fresh
    /// re-authentication, so the arrangement registers a passkey and answers the nonce that gate mints —
    /// which means the row this test reads is the row production writes, down to the type its own factory
    /// chose. A seeded row would still be listed, but it would leave "the endpoint spells a set correctly"
    /// resting on a test's own idea of what a set looks like.
    /// </para>
    /// <para>
    /// The entry is found by the set's <c>credentials.id</c> rather than by position or by its type. By
    /// position, because the list ascends by registration instant and the set is merely last today; by
    /// type, because searching the entries for the token this test is trying to observe would find nothing
    /// and report it as "no set was listed" — the one failure message that would send a reader looking in
    /// the wrong place entirely.
    /// </para>
    /// <para>
    /// The signature counter stays at zero on every ceremony, which is what an authenticator backing a
    /// synced passkey reports; <c>PasskeySignatureCounter.Accept</c> reads a repeated zero as no movement
    /// rather than as a clone.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_ASetOfRecoveryCodes_ArrivesAsTheSchemasOwnToken()
    {
        // Arrange — a signed-in account holding a passkey, then a set issued past the gate that passkey
        // clears.
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        HttpResponseMessage issued = await IssueRecoveryCodesAsync(client, device, userId);

        // The arrangement itself, or every assertion below is a claim about a row nothing wrote.
        await Assert.That(issued.StatusCode).IsEqualTo(HttpStatusCode.OK);
        Guid setId = await ResolveSetCredentialIdAsync(admin, userId);

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert — the status first, so a body that is missing because the request failed reads as the
        // failure it is rather than as an entry that never arrived.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);
        JsonObject[] setEntries =
        [
            .. entries.Select(AsObject).Where(entry => entry["id"]!.GetValue<Guid>() == setId),
        ];

        // That the set is listed at all is asserted before what it is called, so "the endpoint dropped
        // the row" and "the endpoint misspelled the row" are two different red lines.
        await Assert.That(setEntries.Length).IsEqualTo(1);
        await Assert.That(setEntries[0]["type"]!.GetValue<string>()).IsEqualTo(RecoveryCodesColumnValue);
    }

    /// <summary>
    /// For every member of <see cref="CredentialType" />: the <c>type</c> the endpoint emits for a row is
    /// the <c>type</c> that row's column holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The comparison is per row, wire against column, and neither side is written down here.</b> The
    /// expected sequence is <c>id=type</c> pairs read straight out of <c>credentials</c> on the superuser
    /// connection; the arriving sequence is the same pairs as the endpoint emitted them. Pairing by id
    /// rather than comparing two sets of spellings is what makes a failure attributable — it says which
    /// credential was renamed, not merely that the list of spellings moved — and it also catches an entry
    /// that arrived under an id the account does not own.
    /// </para>
    /// <para>
    /// <b>The enum, not a list of cases, decides what has to be covered.</b> The arrangement seeds one
    /// credential for every member the account does not already hold, spelling each through the schema's
    /// own converter, and then <em>asserts in the arrangement</em> that the account's stored spellings are
    /// exactly the set of members' spellings. A fourth member therefore cannot be quietly uncovered: the
    /// day it is declared, either the seed writes a row for it and the comparison speaks for it, or the
    /// arrangement's own check goes red naming it. Nothing here mentions <c>passkey</c>,
    /// <c>federated</c> or <c>recovery_codes</c>, which is what stops the fix being a special case.
    /// </para>
    /// <para>
    /// <b>The seeded rows are raw SQL on the superuser connection, and their shape is asked of the schema
    /// rather than declared.</b> <c>CK_credentials_type_shape</c> says what a row of a given type may look
    /// like, and it says different things for a federated row than for a self-contained one — so
    /// <see cref="InsertCredentialAsync" /> offers the issuer-less shape first and the issuer-bearing one
    /// only if the constraint refuses. That keeps the seed free of per-member knowledge, which is the same
    /// property the assertion has and would otherwise lose.
    /// </para>
    /// <para>
    /// The two converters on <c>PasskeySignatureCounterConfiguration</c> and
    /// <c>PasskeyPublicKeyConfiguration</c> are deliberately absent from all of this. They accept
    /// <c>passkey</c> and <c>federated</c> only and throw on anything else, because a signature counter
    /// cannot hang off a set of recovery codes — that restriction is a rule of its own and nothing here
    /// may be read as pressure to widen it. The converter this test reads is the one on
    /// <c>credentials.type</c> itself, which is the column the wire value has to agree with.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Credentials_EveryCredentialTypeArrivesSpelledTheWayItsColumnIsSpelled()
    {
        // Arrange — an established account (which mints the federated row) holding a passkey, then one
        // row for every remaining member of the enum.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        await ApiFactory.EstablishAccountAsync(client);
        await RegisterPasskeyAsync(client, SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId));

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid userId = await ResolveUserIdAsync(admin, Subject);
        await SeedEveryMissingCredentialTypeAsync(admin, userId);

        // The arrangement itself: the account holds one credential per declared member, so the
        // comparison below really speaks for the whole enum rather than for whichever members a
        // registration happened to produce.
        IReadOnlyList<(Guid Id, string Type)> stored = await StoredCredentialsAsync(admin, userId);
        await Assert.That(JoinTypes(stored)).IsEqualTo(EveryMemberSpelling());

        // Act
        HttpResponseMessage response = await client.GetAsync(CredentialsPath);

        // Assert — the status first, for the reason its neighbour above gives.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonArray entries = await ReadArrayAsync(response);

        // Ordered on both sides and joined whole, so the failure prints the pair that disagrees rather
        // than reporting that one element of a sequence differs.
        string arrived = JoinPairs(entries
            .Select(AsObject)
            .Select(entry => (entry["id"]!.GetValue<Guid>(), entry["type"]!.GetValue<string>())));
        string expected = JoinPairs(stored);

        await Assert.That(arrived).IsEqualTo(expected);
    }

    /// <summary>
    /// The <c>credentials.type</c> spelling <c>CredentialConfiguration</c> writes for
    /// <paramref name="type" />.
    /// </summary>
    /// <remarks>
    /// Reached through the built EF model rather than copied into this file, because a copy is a second
    /// opinion about the column and the whole subject here is that two opinions have diverged. Building
    /// the model opens no connection — the connection string exists only because the provider insists on
    /// one — so this costs nothing and needs no database.
    /// </remarks>
    private static string ColumnSpellingOf(CredentialType type) =>
        TypeColumnConverter.ConvertToProvider(type) as string
        ?? throw new InvalidOperationException(
            $"The credentials.type converter produced no string for {nameof(CredentialType)}.{type}.");

    /// <summary>Every declared member's column spelling, ordered and joined.</summary>
    private static string EveryMemberSpelling() =>
        string.Join(", ", Enum.GetValues<CredentialType>().Select(ColumnSpellingOf).Order(StringComparer.Ordinal));

    private static string JoinTypes(IEnumerable<(Guid Id, string Type)> rows) =>
        string.Join(", ", rows.Select(row => row.Type).Order(StringComparer.Ordinal));

    /// <summary>
    /// <c>id=type</c> for each row, ordered by the rendered pair so both sides of a comparison agree on
    /// sequence without either promising one.
    /// </summary>
    private static string JoinPairs(IEnumerable<(Guid Id, string Type)> rows) =>
        string.Join(
            ", ",
            rows
                .Select(row => $"{row.Id.ToString("D", CultureInfo.InvariantCulture)}={row.Type}")
                .Order(StringComparer.Ordinal));

    /// <summary>
    /// The value converter <c>credentials.type</c> is configured with, taken from the built model.
    /// </summary>
    private static readonly ValueConverter TypeColumnConverter = BuildTypeColumnConverter();

    private static ValueConverter BuildTypeColumnConverter()
    {
        // Never connected to. The provider needs a syntactically valid string to build a model at all,
        // and nothing below opens a command.
        using BudgetoidDbContext db = new(
            new DbContextOptionsBuilder<BudgetoidDbContext>()
                .UseNpgsql("Host=localhost;Port=5432;Database=budgetoid;Username=postgres;Password=postgres")
                .Options);

        IProperty type = db.Model.FindEntityType(typeof(Credential))!.FindProperty(nameof(Credential.Type))!;

        return type.GetValueConverter()
            ?? throw new InvalidOperationException(
                "credentials.type carries no value converter, so nothing here can ask the schema how it "
                + "spells a CredentialType member.");
    }

    /// <summary>
    /// Writes one credential for every member of <see cref="CredentialType" /> the account does not
    /// already hold.
    /// </summary>
    /// <remarks>
    /// Members already present are skipped rather than duplicated, and that is a rule rather than an
    /// optimisation: an account holds at most one federated credential and at most one set of recovery
    /// codes, each owned by a partial unique index, so a second row of either type is refused by the
    /// database and the seed would fail for a reason that has nothing to do with spelling.
    /// </remarks>
    private static async Task SeedEveryMissingCredentialTypeAsync(NpgsqlConnection admin, Guid userId)
    {
        HashSet<string> present =
        [
            .. (await StoredCredentialsAsync(admin, userId)).Select(row => row.Type),
        ];

        foreach (CredentialType type in Enum.GetValues<CredentialType>())
        {
            string columnValue = ColumnSpellingOf(type);
            if (!present.Add(columnValue))
            {
                continue;
            }

            await InsertCredentialAsync(admin, userId, columnValue);
        }
    }

    /// <summary>
    /// Writes a bare credential of <paramref name="columnValue" /> onto an existing account, in whichever
    /// of the two shapes <c>CK_credentials_type_shape</c> permits for it.
    /// </summary>
    /// <remarks>
    /// The issuer-less shape is offered first and the issuer-bearing one only when the constraint refuses
    /// it, so this method carries no opinion about which types need a provider — the schema is asked
    /// instead. Statements travel in autocommit, so the refused attempt leaves nothing behind and the
    /// connection is usable immediately afterwards. Any refusal that is not the shape check propagates:
    /// a unique-index collision or a vocabulary breach means the caller asked for a row the account
    /// cannot hold, and swallowing it here would turn a broken arrangement into a silent one.
    /// </remarks>
    private static async Task InsertCredentialAsync(NpgsqlConnection admin, Guid userId, string columnValue)
    {
        try
        {
            await ExecuteCredentialInsertAsync(admin, userId, columnValue, provider: null, subject: null);
        }
        catch (PostgresException refusal) when (refusal.SqlState == PostgresErrorCodes.CheckViolation
                                                && refusal.ConstraintName == TypeShapeConstraintName)
        {
            // A subject unique to this row, so the partial index over (provider, subject) is never what
            // decides whether the seed lands.
            await ExecuteCredentialInsertAsync(
                admin,
                userId,
                columnValue,
                Credential.GoogleProvider,
                $"seeded-{columnValue}-{Guid.CreateVersion7():N}");
        }
    }

    /// <summary>
    /// The constraint that says what shape a credential of a given type may take. Restated here rather
    /// than referenced, so that one defect reports one name: a test reading the same constant the schema
    /// was rendered from would agree with itself no matter what either said.
    /// </summary>
    private const string TypeShapeConstraintName = "CK_credentials_type_shape";

    private static async Task ExecuteCredentialInsertAsync(
        NpgsqlConnection admin,
        Guid userId,
        string columnValue,
        string? provider,
        string? subject)
    {
        await using NpgsqlCommand command = new(
            """
            insert into credentials (id, user_id, type, provider, subject, created_at_utc)
            values (@id, @user_id, @type, @provider, @subject, @created_at_utc)
            """,
            admin);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("type", columnValue);
        command.Parameters.AddWithValue("provider", (object?)provider ?? DBNull.Value);
        command.Parameters.AddWithValue("subject", (object?)subject ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at_utc", SeedInstant);

        if (await command.ExecuteNonQueryAsync() is not 1)
        {
            throw new InvalidOperationException($"Seeding a '{columnValue}' credential wrote no row.");
        }
    }

    /// <summary>
    /// Fixed UTC instant for the rows this file seeds. PostgreSQL <c>timestamptz</c> rejects a non-UTC
    /// <see cref="DateTime" />, so <see cref="DateTimeKind.Utc" /> is load-bearing.
    /// </summary>
    private static readonly DateTime SeedInstant = new(2026, 6, 12, 13, 14, 15, DateTimeKind.Utc);

    /// <summary>
    /// Every credential of one account as an <c>(id, type)</c> pair, read on the container superuser so
    /// that the endpoint's own owner predicate is not what produced the expectation.
    /// </summary>
    private static async Task<IReadOnlyList<(Guid Id, string Type)>> StoredCredentialsAsync(
        NpgsqlConnection admin,
        Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select id, type from credentials where user_id = @userId",
            admin);
        command.Parameters.AddWithValue("userId", userId);

        List<(Guid, string)> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>
    /// Reads back the user provisioning minted for <paramref name="subject" />. Nothing the API returns
    /// names it, so the lookup goes through the credential the middleware resolved on.
    /// </summary>
    private static async Task<Guid> ResolveUserIdAsync(NpgsqlConnection admin, string subject)
    {
        await using NpgsqlCommand command = new(
            "select user_id from credentials where provider = 'google' and subject = @subject",
            admin);
        command.Parameters.AddWithValue("subject", subject);

        return await command.ExecuteScalarAsync() switch
        {
            Guid userId => userId,
            var unexpected => throw new InvalidOperationException(
                $"Provisioning wrote no account for subject '{subject}', got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// The <c>credentials.id</c> of the row standing for an account's set of recovery codes. Found by the
    /// column's own token, which is the one place in this file that is allowed to name it: the query is
    /// asking the database a question in the database's vocabulary, not asserting anything.
    /// </summary>
    private static async Task<Guid> ResolveSetCredentialIdAsync(NpgsqlConnection admin, Guid userId)
    {
        await using NpgsqlCommand command = new(
            "select id from credentials where user_id = @userId and type = @type",
            admin);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("type", RecoveryCodesColumnValue);

        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"No recovery-code set is filed under that account, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Runs the whole issuing ceremony: the re-authentication options leg, <paramref name="device" />
    /// answering the nonce it issued, and the generation post.
    /// </summary>
    /// <remarks>
    /// Written out here rather than shared with <c>RecoveryCodeGenerationTests</c>, which keeps its own
    /// copy private for the same reason <c>CredentialRevocationTests</c> and
    /// <c>CredentialListEndpointTests</c> each keep theirs: a ceremony helper shared across files becomes
    /// a second place a route's contract is described.
    /// </remarks>
    private static async Task<HttpResponseMessage> IssueRecoveryCodesAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);

        return await client.PostAsJsonAsync(RecoveryCodesPath, new
        {
            codes = SubmissionsOf(Verifiers()),
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
    }

    /// <summary>
    /// One whole submission per verifier, as the route spells a set: the verifier, a factor of its own,
    /// and the pair of envelopes sealed under that code's key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ten submissions and never ten verifiers beside one factor and one pair.</b> A set is ten
    /// separate secrets under a single <c>credentials</c> row and the client derives a key-encryption
    /// key from each <em>code</em>, so one pair for the whole set would seal the account under whichever
    /// code that pair belonged to and nine of the ten would open nothing.
    /// </para>
    /// <para>
    /// Nothing in this file asserts anything about an envelope — every assertion here is about the token
    /// a <c>type</c> column and a <c>type</c> member spell the credential with — so what these have to
    /// be is well-formed, and fresh, because two codes of one set repeating a factor identifier is a
    /// refusal of its own and <c>factor_id</c> is the table's primary key — <c>PK_wrapped_account_keys</c>
    /// — so it is unique across the whole table.
    /// </para>
    /// </remarks>
    private static object[] SubmissionsOf(IReadOnlyList<string> verifiers) =>
    [
        .. verifiers.Select(verifier =>
        {
            WrappedKeyFixture keys = WrappedKeyFixture.Mint();

            return new
            {
                verifier,
                factorId = keys.FactorId,
                wrappedContentKey = keys.WrappedContentKey,
                wrappedIndexKey = keys.WrappedIndexKey,
            };
        }),
    ];

    /// <summary>
    /// A well-formed set: <see cref="RequiredCodeCount" /> distinct verifiers of
    /// <see cref="VerifierLength" /> bytes each, base64url encoded exactly as a browser would send them.
    /// </summary>
    /// <remarks>
    /// Random rather than fixed vectors, which costs nothing here: no assertion in this file depends on
    /// the value of a verifier, only on the credential row issuing one produces.
    /// </remarks>
    private static string[] Verifiers() =>
    [
        .. Enumerable
            .Range(0, RequiredCodeCount)
            .Select(_ => Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength))),
    ];

    /// <summary>
    /// Runs both authenticated legs of a registration, so the account really holds a passkey a signature
    /// answers to rather than material seeded out of band.
    /// </summary>
    /// <remarks>
    /// The account already exists when this runs, and the two ways it got there are both above the call:
    /// <see cref="ApiFactory.CreateSignedInClientAsync" /> seeds the whole account behind the client it
    /// hands out, and the test still reaching the route with a provider bearer establishes its own on the
    /// line above. Neither passkey leg mints one and neither does <c>/api/me/*</c>, so a client arriving
    /// here without an account is refused with a 401 for a reason no test here is about.
    /// </remarks>
    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);

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

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    private static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.PostAsync(path, content: null);
        response.EnsureSuccessStatusCode();
        JsonNode options = (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!;
        return Base64UrlText.Decode(options["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// One array element as an object, refusing anything that is not one. Checked rather than forgiven:
    /// an element that is a bare string would read through a lenient parse as an entry with no members,
    /// which is a red nobody could interpret.
    /// </summary>
    private static JsonObject AsObject(JsonNode? entry) =>
        entry as JsonObject
        ?? throw new InvalidOperationException("A list entry was something other than a JSON object.");

    /// <summary>
    /// The response body as a JSON <b>array</b>, refusing anything that is not one — the contract is an
    /// array rather than an envelope carrying one.
    /// </summary>
    private static async Task<JsonArray> ReadArrayAsync(HttpResponseMessage response) =>
        await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()) as JsonArray
        ?? throw new InvalidOperationException("The endpoint answered something other than a JSON array.");

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// A host whose factory leaves the application's own authentication standing, because the test above
    /// authenticates from a session cookie rather than from a provider bearer.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="StartHostAsync" /> rather than replacing it, for
    /// <see cref="Credentials_EveryCredentialTypeArrivesSpelledTheWayItsColumnIsSpelled" />: its
    /// arrangement asserts the account holds exactly one credential per declared member, and a seeded
    /// sign-in writes a passkey of its own beside the one the ceremony registers. Two passkeys is a
    /// second row for one member, so moving that test would be a decision about what the comparison
    /// should say rather than a change of client.
    /// </remarks>
    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }
}
