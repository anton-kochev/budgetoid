using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Api.Infrastructure;
using Application.Passkeys;
using Domain.Sessions;
using Domain.Users;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// Erasure is one action that removes an account and everything owned beneath it. These tests drive
/// the real HTTP pipeline rather than the handler directly, so they run through the least-privilege
/// role, the row-level security policies and the referential cascade exactly as a request does.
/// </summary>
/// <remarks>
/// <para>
/// Every after-the-fact count is read on <see cref="PostgresTestHost.ConnectionString" /> — the
/// container superuser — and never on the application role. Both policies are <c>FOR ALL</c>, so a
/// policed connection reports zero rows for a row that is still there exactly as it does for one
/// that is gone. Verified on the app role, the central assertion of this file could not fail.
/// </para>
/// <para>
/// Every count asserted zero after the erasure is asserted non-zero before it, against the same
/// predicate on the same connection. Without that half, a suite whose every assertion is "no rows"
/// passes just as happily against a database where the seeding never worked.
/// </para>
/// <para>
/// Every test here now performs a real WebAuthn ceremony first, because erasure is authorized by a
/// fresh assertion rather than by the bearer token. That is why each one registers a
/// <see cref="SyntheticAuthenticator" /> over HTTP instead of relying on the passkey material
/// <see cref="SeedIdentityRowsAsync" /> writes out of band: the seeded key answers to no private key,
/// so no signature could ever verify against it. The seeded rows stay, and are still what puts a row
/// in <c>sessions</c> — a table no endpoint writes to yet.
/// </para>
/// <para>
/// What this file is about is unchanged: the deletion order, the post-condition, and the tables the
/// cascade reaches. The gate itself — which nonce pool authorizes an erasure, and whose credential
/// has to answer it — is measured in <c>ErasureReauthenticationTests</c>.
/// </para>
/// </remarks>
public sealed class AccountErasureEndpointTests
{
    /// <summary>
    /// Which id a table files its owner under. The distinction is not cosmetic: an enumeration that
    /// guessed from the column name would keep working right up until a table carried both.
    /// </summary>
    private enum OwnedBy
    {
        /// <summary>The row names the erased user, directly or through a credential of theirs.</summary>
        User,

        /// <summary>The row names a budget the erased user owns.</summary>
        Budget,
    }

    /// <summary>
    /// One table an account owns, with the column naming its owner and which id that column holds.
    /// </summary>
    private readonly record struct OwnedTable(string Name, string OwnerColumn, OwnedBy Owner);

    /// <summary>
    /// The tables an account owns. Held as one list because the point of the FR-025 assertion is
    /// that <b>no</b> table keeps a row, and a test that enumerated its tables inline would silently
    /// stop covering the one added next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>webauthn_challenges</c> is absent on purpose rather than by oversight: a challenge belongs
    /// to a ceremony rather than to a person, carries neither <c>user_id</c> nor <c>budget_id</c>,
    /// and so satisfies "no row references the erased user" vacuously. <c>currencies</c> is global
    /// reference data and is owned by nobody.
    /// </para>
    /// <para>
    /// <c>passkey_public_keys</c> and <c>passkey_signature_counters</c> are keyed on
    /// <c>credential_id</c> and carry <c>user_id</c> beside it, which is the column asserted on here:
    /// it is the one that says whose material this is, and it is what a stray row would still be
    /// naming after the account it belongs to is gone.
    /// </para>
    /// </remarks>
    private static readonly OwnedTable[] OwnedTables =
    [
        new("users", "id", OwnedBy.User),
        new("credentials", "user_id", OwnedBy.User),
        new("sessions", "user_id", OwnedBy.User),
        new("passkey_public_keys", "user_id", OwnedBy.User),
        new("passkey_signature_counters", "user_id", OwnedBy.User),
        new("budgets", "user_id", OwnedBy.User),
        new("accounts", "budget_id", OwnedBy.Budget),
        new("category_groups", "budget_id", OwnedBy.Budget),
        new("categories", "budget_id", OwnedBy.Budget),
        new("payees", "budget_id", OwnedBy.Budget),
        new("transactions", "budget_id", OwnedBy.Budget),
    ];

    /// <summary>
    /// The Google subject every single-account test authenticates as. Named here rather than left to
    /// the factory's default because the furnishing helper resolves the seeded ids by looking the
    /// credential up on it — a client and a lookup that disagreed would furnish one account and
    /// assert about another.
    /// </summary>
    private const string Subject = "google-erasing";

    [Test]
    public async Task Erase_ForAnAuthenticatedUserWithAFreshAssertion_ReturnsNoContent()
    {
        // Arrange — nothing seeded beyond what account provisioning itself creates, plus the passkey
        // the ceremony needs. The bare case is worth its own test: it is the only one that fails if
        // the route is simply missing.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        (Guid userId, _) = await ResolveOwnerAsync(host, Subject);

        // Act
        HttpResponseMessage response = await EraseAsync(client, device, userId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Erase_WithoutAuthentication_IsRefused()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();

        // Act — no subject header, so nothing authenticates and the fallback policy decides. The body
        // is well formed on purpose: a request turned away for its shape would prove nothing about
        // authorization.
        HttpResponseMessage response = await host.Factory.CreateClient().PostAsJsonAsync(ErasurePath, new
        {
            credentialId = "AA",
            clientDataJson = "AA",
            authenticatorData = "AA",
            signature = "AA",
            userHandle = (string?)null,
        });

        // Assert — the endpoint declares no authorization metadata of its own, so this is the test
        // that would notice an AllowAnonymous added to it. The title is what separates this 401 from
        // the others: three distinct ones are reachable on this route — nothing authenticated, an
        // authenticated token naming an account that no longer exists, and the gate's own refusal —
        // and only the last carries PasskeyVerificationExceptionHandler.Title. Without the title
        // asserted, a route that had lost its authorization entirely would still pass here on the 401
        // the gate answers a well-formed body with, which is exactly the body this test sends.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(await ReadTitleAsync(response)).IsNotEqualTo(PasskeyVerificationExceptionHandler.Title);
    }

    [Test]
    public async Task Erase_ForAFullyFurnishedAccount_ReturnsNoContent()
    {
        // Arrange — an account carrying a row in every table it can own, including a categorized
        // transaction. That transaction is what makes this test different from the bare one above:
        // transactions is the child of four RESTRICT edges — to budgets, accounts, categories and
        // payees — so an erasure that leant on the cascade, or that deleted in the wrong order,
        // answers 23503 here.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        (Guid userId, _) = await FurnishAccountAsync(host, client, Subject);
        await RegisterPasskeyAsync(client, device);

        // Act
        HttpResponseMessage response = await EraseAsync(client, device, userId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Erase_ForAFullyFurnishedAccount_LeavesNoRowInAnyTable()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        (Guid userId, Guid budgetId) = await FurnishAccountAsync(host, client, Subject);
        await RegisterPasskeyAsync(client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // The seeding is proved before the act, table by table, on the same connection and with the
        // same predicates the assertion below uses. This half is not decoration: without it every
        // assertion in this test is "count is zero", which an empty database satisfies.
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, userId, budgetId);
        foreach (OwnedTable table in OwnedTables)
        {
            await Assert.That(before[table.Name]).IsGreaterThan(0L);
        }

        // Act
        HttpResponseMessage response = await EraseAsync(client, device, userId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        IReadOnlyDictionary<string, long> after = await CountOwnedRowsAsync(admin, userId, budgetId);
        foreach (OwnedTable table in OwnedTables)
        {
            await Assert.That(after[table.Name]).IsEqualTo(0L);
        }
    }

    /// <summary>
    /// The one erasure step the handler does <b>not</b> take, measured on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>categories → category_groups</c> is the fifth RESTRICT edge in the owned graph and the only
    /// one no explicit delete answers: both tables cascade from <c>budgets</c>, PostgreSQL queues that
    /// edge's check as an after-row trigger when the <c>category_groups</c> row is deleted — strictly
    /// after the cascade into <c>categories</c> was queued — and the after-trigger queue is FIFO. So
    /// the categories are gone whichever of the two triggers fires first, and the order the
    /// constraints happen to have been created in does not come into it.
    /// </para>
    /// <para>
    /// This is the test that goes red if that ever stops holding. It cannot be left to
    /// <see cref="Erase_ForAFullyFurnishedAccount_LeavesNoRowInAnyTable" />, which seeds a
    /// transaction: the explicit transactions delete runs first there and empties the table the
    /// category is referenced from, so a cascade that could not reach the categories would never be
    /// asked to. No transaction here, which leaves the categories to the cascade alone.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Erase_ForAnAccountWithCategoriesAndNoTransaction_LeavesNoneOfEither()
    {
        // Arrange — a categorised budget with no movement in it at all.
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RegisterPasskeyAsync(client, device);
        Guid categoryGroupId = await CreateAsync(client, "/api/category-groups", new
        {
            name = "Essentials",
            description = (string?)null,
        });
        await CreateAsync(client, "/api/categories", new
        {
            name = "Groceries",
            description = (string?)null,
            categoryGroupId,
        });
        (Guid userId, Guid budgetId) = await ResolveOwnerAsync(host, Subject);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        await Assert.That(await CountBudgetRowsAsync(admin, "category_groups", budgetId)).IsEqualTo(1L);
        await Assert.That(await CountBudgetRowsAsync(admin, "categories", budgetId)).IsEqualTo(1L);

        // Act
        HttpResponseMessage response = await EraseAsync(client, device, userId);

        // Assert — a cascade that reached the groups before the categories would answer 23503 and
        // this would be a 500 rather than two zeros.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(await CountBudgetRowsAsync(admin, "categories", budgetId)).IsEqualTo(0L);
        await Assert.That(await CountBudgetRowsAsync(admin, "category_groups", budgetId)).IsEqualTo(0L);
    }

    [Test]
    public async Task Erase_LeavesAnotherAccountUntouched()
    {
        // Arrange — two furnished accounts. Without this test a handler that emptied every table in
        // the database would satisfy every other assertion in this file.
        const string erasedSubject = "google-erased";
        const string survivorSubject = "google-survivor";
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient erased = host.Factory.CreateAuthenticatedClient(erasedSubject);
        HttpClient survivor = host.Factory.CreateAuthenticatedClient(survivorSubject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        (Guid erasedUserId, _) = await FurnishAccountAsync(host, erased, erasedSubject);
        await RegisterPasskeyAsync(erased, device);
        (Guid survivorUserId, Guid survivorBudgetId) =
            await FurnishAccountAsync(host, survivor, survivorSubject);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        IReadOnlyDictionary<string, long> before =
            await CountOwnedRowsAsync(admin, survivorUserId, survivorBudgetId);
        foreach (OwnedTable table in OwnedTables)
        {
            await Assert.That(before[table.Name]).IsGreaterThan(0L);
        }

        // Act
        HttpResponseMessage response = await EraseAsync(erased, device, erasedUserId);

        // Assert — the survivor's counts are compared to what they were, not merely to "more than
        // zero": an erasure that took some of another account's rows and left others would pass a
        // non-zero check.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        IReadOnlyDictionary<string, long> after =
            await CountOwnedRowsAsync(admin, survivorUserId, survivorBudgetId);
        foreach (OwnedTable table in OwnedTables)
        {
            await Assert.That(after[table.Name]).IsEqualTo(before[table.Name]);
        }
    }

    /// <summary>
    /// Erasure is not idempotent to the caller — the second call is refused rather than answered — and
    /// it leaves <b>no</b> account behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces <c>Erase_CalledASecondTime_IsRefusedAndLeavesTheNewAccountIntact</c>, and the
    /// final assertion is inverted from that test's: it demanded exactly one <c>users</c> row after the
    /// second call, and one row is the defect. A Google id token stays valid for up to an hour after
    /// the account it names is gone, so the second request — a retry, a poll, a forgotten second tab —
    /// arrived authenticated and provisioning minted a whole new account for it: a <c>users</c> row
    /// carrying the address, a <c>credentials</c> row carrying the subject, and a default budget. That
    /// account then held no passkey, so the erasure gate refused it forever. Leaving stopped meaning
    /// leaving, and the wreckage was unerasable.
    /// </para>
    /// <para>
    /// The refusal itself is unchanged and is not the subject here: a 401 makes no claim about data, it
    /// says the request did not prove who it was, which is true of a token naming an account that no
    /// longer exists. What changed is that the refusal now writes nothing.
    /// </para>
    /// <para>
    /// The first call's 204 and the per-table zeros are kept deliberately, and they are the control: a
    /// middleware that refused <b>every</b> erasure — before or after — would satisfy an empty
    /// <c>users</c> table perfectly and would have destroyed the feature.
    /// </para>
    /// <para>
    /// The wart is real and is accepted rather than solved: a client retrying a lost 204 sees a
    /// failure over data that is genuinely gone. There is deliberately no stored record that an
    /// erasure happened, so nothing on the server could answer differently; the mitigation is
    /// client-side.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Erase_CalledASecondTime_IsRefusedAndCreatesNoAccount()
    {
        // Arrange
        await using PostgresTestHost host = await StartHostAsync();
        HttpClient client = host.Factory.CreateAuthenticatedClient(Subject);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        (Guid firstUserId, Guid firstBudgetId) = await FurnishAccountAsync(host, client, Subject);
        await RegisterPasskeyAsync(client, device);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();

        // The seeding is proved before the act, table by table, on the same connection and with the
        // same predicates the assertions below use — the promise this class's remarks make about
        // every count it asserts zero. Without it, eleven "count is zero" assertions are all satisfied
        // by a database the furnishing never reached.
        IReadOnlyDictionary<string, long> before = await CountOwnedRowsAsync(admin, firstUserId, firstBudgetId);
        foreach (OwnedTable table in OwnedTables)
        {
            await Assert.That(before[table.Name]).IsGreaterThan(0L);
        }

        await Assert.That(await ScalarAsync(admin, "select count(*) from users")).IsGreaterThan(0L);

        // Act — one ceremony, answered twice. The second call posts the same body directly instead of
        // beginning another ceremony: the options leg is itself an authenticated request on a route
        // that mints nothing, so for a caller whose account is gone it answers 401 too, and the
        // EnsureSuccessStatusCode inside it would end this test before its own assertion.
        AssertionResult assertion = await AuthenticateAsync(client, device, firstUserId);
        HttpResponseMessage first = await PostErasureAsync(client, assertion);
        HttpResponseMessage second = await PostErasureAsync(client, assertion);

        // The same stale token knocking on the ceremony's own door, which is the request a retrying
        // client actually makes first. Asserted separately because it is the one that used to provision.
        HttpResponseMessage retriedOptions =
            await client.PostAsync(ReauthenticationOptionsPath, content: null);

        // Assert
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(retriedOptions.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        IReadOnlyDictionary<string, long> afterFirst =
            await CountOwnedRowsAsync(admin, firstUserId, firstBudgetId);
        foreach (OwnedTable table in OwnedTables)
        {
            await Assert.That(afterFirst[table.Name]).IsEqualTo(0L);
        }

        // No rows at all, not "none belonging to the erased id". The loop above is scoped to the ids
        // the erasure took, so a resurrected account — a different id entirely — passes every one of
        // those eleven assertions. This unscoped count is the only line that sees it.
        await Assert.That(await ScalarAsync(admin, "select count(*) from users")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from credentials")).IsEqualTo(0L);
        await Assert.That(await ScalarAsync(admin, "select count(*) from budgets")).IsEqualTo(0L);
    }

    private const string ErasurePath = "/api/me/erasure";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";

    /// <summary>
    /// Runs both authenticated legs of a registration so the account holds a passkey a signature can
    /// actually be verified against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real ceremony rather than seeded rows, and that is not preference. <see cref="SeedIdentityRowsAsync" />
    /// writes four bytes of stand-in key material that no private key answers to, so an assertion
    /// checked against it could never verify — the erasure would be refused and every test in this
    /// file would fail for a reason that has nothing to do with what it measures.
    /// </para>
    /// <para>
    /// The account is established first, on a route that is allowed to mint one. Neither passkey leg
    /// provisions any more — only the data route groups do — so a registration is the second
    /// authenticated request an account makes, never the first. Placed here rather than at each call
    /// site because every test in this file that needs a passkey needs an account under it, and the two
    /// tests that furnish an account beforehand reach an idempotent read.
    /// </para>
    /// </remarks>
    private static async Task RegisterPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        await ApiFactory.EstablishAccountAsync(client);

        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(challenge, ApiFactory.PasskeyOrigin, prfEnabled: true);
        HttpResponseMessage response = await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
        });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Runs the whole erasure exchange: the authenticated options leg, the authenticator, and the
    /// erasure request carrying what it produced.
    /// </summary>
    private static async Task<HttpResponseMessage> EraseAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId) =>
        await PostErasureAsync(client, await AuthenticateAsync(client, device, userId));

    /// <summary>
    /// Runs the options leg and answers its challenge, stopping short of the erasure request.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="EraseAsync" /> for the one test that has to post an erasure <b>without</b>
    /// running another options leg first. That leg is an authenticated request on a route that no longer
    /// mints an account, so for a caller whose account is already gone it answers 401 — and the
    /// <c>EnsureSuccessStatusCode</c> inside <see cref="BeginCeremonyAsync" /> would end the test before
    /// the assertion it exists for.
    /// </remarks>
    private static async Task<AssertionResult> AuthenticateAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);

        return device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId));
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
    /// The <c>title</c> of a problem-details body, which is the only member that says which of this
    /// route's three 401s answered.
    /// </summary>
    private static async Task<string> ReadTitleAsync(HttpResponseMessage response) =>
        (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!["title"]!.GetValue<string>();

    /// <summary>
    /// Writes one row into every table an account can own and returns the ids the assertions key on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The money data goes in over HTTP, so every row is one the application itself could really have
    /// written — through the same validation, the same repositories and the same least-privilege role
    /// a request uses. A seeding path that wrote those rows directly could put the account into a
    /// shape no request produces, and an erasure proved against that shape proves nothing.
    /// </para>
    /// <para>
    /// The identity rows have no endpoint that creates them yet, so they are written out of band on
    /// the container superuser — but through the domain factories rather than raw SQL, which is what
    /// keeps a seeded passkey the same shape a registration would write. The transaction carries both
    /// a category and a payee name: the category is what makes the <c>transactions → categories</c>
    /// RESTRICT edge live, and the payee name is the only thing that puts a row in <c>payees</c>.
    /// </para>
    /// </remarks>
    private static async Task<(Guid UserId, Guid BudgetId)> FurnishAccountAsync(
        PostgresTestHost host,
        HttpClient client,
        string subject)
    {
        Guid accountId = await CreateAsync(client, "/api/accounts", new
        {
            name = "Checking",
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
        await CreateAsync(client, "/api/transactions", new
        {
            amount = -10m,
            date = "2026-06-26",
            accountId,
            description = "Coffee",
            payeeName = "Starbucks",
            categoryId,
        });

        (Guid userId, Guid budgetId) = await ResolveOwnerAsync(host, subject);
        await SeedIdentityRowsAsync(host, userId);
        return (userId, budgetId);
    }

    /// <summary>
    /// Reads back the user and default budget that provisioning minted for
    /// <paramref name="subject" />. Nothing the API returns names either id, so the lookup goes
    /// through the credential the middleware resolved the request on.
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
        // scope every budget-owned assertion in this file to whichever one came back first.
        if (await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"Subject '{subject}' owns more than one budget; the enumeration assumes exactly one.");
        }

        return (userId, budgetId);
    }

    /// <summary>
    /// Adds the passkey material and the session row no endpoint writes yet, so the FR-025
    /// enumeration has something to find in every user-owned table rather than only in the two
    /// provisioning fills.
    /// </summary>
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

        // Established against the passkey rather than the federated credential because
        // CK_sessions_kind_matches_credential ties the two together; the seeded row is the full
        // session a passkey earns, which is the shape production writes.
        db.Sessions.Add(Session.Establish(passkey, SeedInstant, SeedInstant.AddDays(14)));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Counts the rows each owned table holds for one account, on whichever id that table files its
    /// owner under.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, long>> CountOwnedRowsAsync(
        NpgsqlConnection connection,
        Guid userId,
        Guid budgetId)
    {
        Dictionary<string, long> counts = new(OwnedTables.Length, StringComparer.Ordinal);

        foreach (OwnedTable table in OwnedTables)
        {
            // The table and column names are compile-time constants from the private list above, not
            // anything a caller supplies; the owner id is bound as a parameter like everywhere else.
            await using NpgsqlCommand command = new(
                $"select count(*) from {table.Name} where {table.OwnerColumn} = @owner",
                connection);
            command.Parameters.AddWithValue(
                "owner",
                table.Owner is OwnedBy.Budget ? budgetId : userId);
            counts[table.Name] = await ReadCountAsync(command, table.Name);
        }

        return counts;
    }

    /// <summary>
    /// Counts the rows one budget-owned table holds for one budget, for the tests that name a table
    /// rather than sweep the whole list.
    /// </summary>
    private static async Task<long> CountBudgetRowsAsync(
        NpgsqlConnection connection,
        string table,
        Guid budgetId)
    {
        // The table name is a compile-time constant from the call site, not anything a caller
        // supplies at run time; the owner id is bound as a parameter like everywhere else.
        await using NpgsqlCommand command = new(
            $"select count(*) from {table} where budget_id = @owner",
            connection);
        command.Parameters.AddWithValue("owner", budgetId);
        return await ReadCountAsync(command, table);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return await ReadCountAsync(command, sql);
    }

    /// <summary>
    /// Reads a count, refusing anything else. Pattern-matched rather than cast-and-null-forgive: a
    /// null or unexpected scalar means the query changed shape, and that should fail loudly here
    /// instead of at the assertion.
    /// </summary>
    private static async Task<long> ReadCountAsync(NpgsqlCommand command, string source) =>
        await command.ExecuteScalarAsync() switch
        {
            long count => count,
            var unexpected => throw new InvalidOperationException(
                $"Expected a count from '{source}', got '{unexpected ?? "null"}'."),
        };

    /// <summary>
    /// Posts <paramref name="body" /> and returns the id of the row it created, failing loudly on
    /// any status other than success — a furnishing step that quietly did nothing would make the
    /// whole file vacuous.
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
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing. The length satisfies
    /// <c>CK_passkey_public_keys_webauthn_credential_id_length</c>, which admits 16 to 1023 bytes; the
    /// derivation satisfies <c>IX_passkey_public_keys_webauthn_credential_id</c>, which is
    /// <b>unique</b> — a constant handle makes the second account in
    /// <see cref="Erase_LeavesAnotherAccountUntouched" /> unseedable, and a seeding failure there
    /// would read as a bug in the erasure rather than in the fixture.
    /// </remarks>
    private static byte[] WebAuthnCredentialIdFor(Guid userId) =>
        [.. userId.ToByteArray(), .. userId.ToByteArray()];

    /// <summary>
    /// Four bytes of stand-in key material. Nothing here verifies a signature, and the only rule the
    /// column holds is that the key is between one byte and <see cref="PasskeyPublicKey.MaxCoseKeyLength" />.
    /// </summary>
    private static readonly byte[] CoseKey = [0xA5, 0x01, 0x02, 0x03];

    private static async Task<PostgresTestHost> StartHostAsync()
    {
        PostgresTestHost host = new();
        await host.StartAsync();
        return host;
    }
}
