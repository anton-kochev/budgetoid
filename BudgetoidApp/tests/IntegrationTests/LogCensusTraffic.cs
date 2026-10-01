using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Application.Passkeys;
using Domain.Security;
using Domain.Users;
using Infrastructure.Persistence.Inventory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The traffic <c>LogRedactionTests</c> drives through a host before it reads the logs: every route
/// that handles an email, a provider subject, a passkey handle or a narrative value, and every path
/// that fails loudly while doing so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sources of needles, and this class owes the census both.</b> Most needles are read back out
/// of the database over <see cref="NeverLoggedColumns.All" />, so a column added to that list is
/// searched without an edit here. But a value that was refused, or replaced, or erased never stands in
/// the database when the census reads it — so this class reports every identifying value it
/// <i>sent</i> (<see cref="SentValue" />), labelled with the column it would have filled, and calls the
/// census back (<c>snapshot</c>) after every step. After every step rather than before the ones known
/// to replace or delete, so a step added later cannot take a value away unseen. The limit is the
/// step: a value one step writes and deletes inside itself is never read back, which is why the
/// second budget's seed and its removal are two steps.
/// </para>
/// <para>
/// <b>Narrative columns with no write route are filled from the inventory, not by name.</b>
/// <see cref="FillEmptyNarrativeColumnsAsync" /> writes a well-formed envelope into every row of every
/// narrative column that the routes left empty, before the list reads and the export run, so those
/// reads carry the value through the app. That reaches a new narrative column on a table this traffic
/// already writes rows into. It cannot reach one on a table nothing writes into — an UPDATE over no
/// rows fills nothing — so that case surfaces as the census's "every column holds a value" floor.
/// </para>
/// <para>
/// <b>The order is load-bearing.</b> The rotation runs before the fill, because the fill gives
/// <c>budgets.name</c> a value no chunk arm can re-seal and the completion would then refuse. The
/// two-budget fault comes after the rotation, because a second owned budget makes the completion a
/// fault as well. The erasure is last, because it takes the account every other step writes into.
/// </para>
/// <para>
/// Each step is timed against the recorder so the census can say which steps wrote nothing but
/// framework noise. Nothing here asserts on a status beyond what the next step needs: a refused step
/// is still traffic. Whether each route was really <i>reached</i> is <see cref="RouteTally" />'s floor.
/// </para>
/// </remarks>
internal static class LogCensusTraffic
{
    private const string AssertionOptionsPath = "/api/passkeys/assertion/options";
    private const string AssertionPath = "/api/passkeys/assertion";
    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string PasskeyRegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string PasskeyRegistrationPath = "/api/passkeys/registration";
    private const string RedemptionPath = "/api/recovery-codes/redemption";
    private const string RecoveryCodesPath = "/api/me/recovery-codes";
    private const string ExportPath = "/api/me/export";
    private const string SignOutPath = "/api/me/session/revocation";
    private const string ErasurePath = "/api/me/erasure";
    private const string RotationPath = "/api/me/key-rotation";
    private const string EmailChangePath = "/api/me/email-change";

    /// <summary>The issuer the application's bearer handler accepts.</summary>
    public const string ProviderIssuer = "https://accounts.google.com";

    /// <summary>The audience the application's bearer handler accepts: <c>ApiFactory</c>'s client id.</summary>
    public const string ProviderAudience = "test-client-id";

    /// <summary>The name of the step that sends an unreadable body, for a census floor to find it by.</summary>
    public const string MalformedBodyStep = "malformed JSON body";

    /// <summary>The name of the bearer step whose token validates.</summary>
    public const string ValidatedTokenStep = "provider token, validated, new identity";

    /// <summary>The name of the bearer step whose token's signature is forged.</summary>
    public const string ForgedTokenStep = "provider token, forged signature";

    /// <summary>
    /// The name of the step that adds a passkey whose handle is already registered, for a census floor
    /// to find it by.
    /// </summary>
    public const string DuplicateHandleStep = "add passkey, handle already registered";

    /// <summary>The name of the step that erases the primary account.</summary>
    public const string ErasureStep = "erasure";

    public const string EmailChangeStep = "email change";

    public const string BearerEmailChangeStep = "email change, provider token validated";

    public const string ForgedEmailChangeStep = "email change, provider token forged";

    /// <summary>One step of the traffic and the records it wrote.</summary>
    public sealed record Step(string Name, IReadOnlyList<int> Statuses, IReadOnlyList<CapturedLogRecord> Records);

    /// <summary>
    /// One identifying value the traffic sent, and the never-logged column it would have filled.
    /// </summary>
    /// <remarks>
    /// Built only through the two factories, so the value is text or bytes and the column is
    /// resolved against <see cref="NeverLoggedColumns.All" /> when the value is made, so a label naming
    /// no never-logged column fails at the line that wrote it.
    /// </remarks>
    public sealed record SentValue
    {
        private SentValue(string column, object value)
        {
            Column = column;
            Value = value;
        }

        /// <summary>The column the value would have filled, as <c>table.column</c>.</summary>
        public string Column { get; }

        /// <summary>The value: a <see cref="string" /> or a <see cref="byte" /> array, and nothing else.</summary>
        public object Value { get; }

        public static SentValue OfText(string table, string column, string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            return new(Resolve(table, column), text);
        }

        public static SentValue OfBytes(string table, string column, byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);

            return new(Resolve(table, column), bytes);
        }

        private static string Resolve(string table, string column) =>
            NeverLoggedColumns.All.SingleOrDefault(entry => entry.Table == table && entry.Column == column)?.Qualified
            ?? throw new InvalidOperationException($"{table}.{column} is not a never-logged column.");
    }

    /// <summary>What the traffic produced, for the census to report on.</summary>
    /// <param name="Steps">Every step, in order.</param>
    /// <param name="FilledFromTheInventory">Narrative columns the routes left empty and the fill wrote.</param>
    /// <param name="EmptyBeforeTheReads">
    /// Narrative columns still holding no value when the list reads and the export began.
    /// </param>
    /// <param name="Subject">The provider subject the registered account carries.</param>
    /// <param name="Email">The address the registered account carries.</param>
    /// <param name="Sent">Every identifying value the traffic sent, stored or not.</param>
    public sealed record Run(
        IReadOnlyList<Step> Steps,
        IReadOnlyList<string> FilledFromTheInventory,
        IReadOnlyList<string> EmptyBeforeTheReads,
        string Subject,
        string Email,
        IReadOnlyList<SentValue> Sent);

    /// <summary>What the traffic against the real bearer handler produced.</summary>
    public sealed record BearerRun(IReadOnlyList<Step> Steps, IReadOnlyList<SentValue> Sent);

    /// <summary>What the inventory-driven fill did.</summary>
    public sealed record Fill(IReadOnlyList<string> Filled, IReadOnlyList<string> StillEmpty);

    /// <summary>The rows one pass of <see cref="WriteNarrativeRowsAsync" /> created.</summary>
    private sealed record NarrativeRows(
        Guid AccountId,
        Guid PayeeId,
        Guid GroupId,
        Guid CategoryId,
        Guid TransactionId,
        string AccountNameWire);

    /// <summary>
    /// Drives the whole traffic through <paramref name="factory" />, which must carry
    /// <paramref name="recorder" /> and both authentication flags.
    /// </summary>
    /// <param name="snapshot">
    /// Called, with the moment's name, after every step, so the census reads each stored value while it
    /// still stands, whichever later step replaces or deletes it.
    /// </param>
    public static async Task<Run> DriveAsync(
        PostgresTestHost host,
        ApiFactory factory,
        LogRecorder recorder,
        Func<string, Task> snapshot)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(snapshot);

        List<Step> steps = [];
        List<SentValue> sent = [];
        string subject = Marker("subject");
        string email = $"{Marker("email")}@Log-Census.Example";
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);

        // The primary device's counter must rise on every accepted assertion.
        uint signCount = 0;
        uint NextSignCount() => ++signCount;

        // Every step is followed by a snapshot, so a value a step stores and a later step replaces or
        // deletes is still a needle — whichever later step it is.
        Task Step(string name, Func<List<int>, Task> act) => StepAsync(steps, recorder, name, act, snapshot);

        // Registration with a marker email and subject — the one creating path.
        Guid accountId = Guid.Empty;
        string cookie = string.Empty;
        IReadOnlyList<string> verifiers = [];
        sent.Add(SentValue.OfText("credentials", "subject", subject));
        sent.Add(SentValue.OfText("users", "email", email));
        await Step("registration", async statuses =>
        {
            using HttpClient provider = factory.CreateAuthenticatedClient(subject, email);
            RegistrationCeremonyResult registered = await RegistrationCeremony.RegisterAsync(provider, device);
            statuses.Add((int)registered.Response.StatusCode);
            await RegistrationCeremony.EnsureOkAsync(registered.Response);
            accountId = registered.AccountId;
            cookie = RegistrationCeremony.SessionCookieValueOf(registered.Response);
            verifiers = registered.Verifiers;
        });

        // The same email under a new subject, then the same subject under a new email: each is refused,
        // and the half that is new is never stored — so only the sent list makes it a needle. The
        // duplicate-email attempt reaches the finish leg, so the handle its authenticator minted is sent
        // and refused too, and goes on the list the same way.
        string refusedSubject = Marker("refused-subject");
        string refusedEmail = $"{Marker("refused-email")}@example.test";
        sent.Add(SentValue.OfText("credentials", "subject", refusedSubject));
        sent.Add(SentValue.OfText("users", "email", refusedEmail));
        await Step("registration, duplicate email", async statuses =>
            statuses.AddRange(await AttemptRegistrationAsync(factory, refusedSubject, email, sent)));
        await Step("registration, duplicate subject", async statuses =>
            statuses.AddRange(await AttemptRegistrationAsync(factory, subject, refusedEmail, sent)));

        using HttpClient client = CookieClient(factory, cookie);
        using HttpClient anonymous = factory.CreateClient();

        await Step("passkey sign-in", async statuses =>
            statuses.Add(await SignInAsync(anonymous, device, accountId, NextSignCount(), tamper: false)));

        // Refused on its signature; the counter it reported is not stored, but it is spent here anyway so
        // no later assertion depends on whether a refusal advances it.
        await Step("passkey sign-in, tampered signature", async statuses =>
            statuses.Add(await SignInAsync(anonymous, device, accountId, NextSignCount(), tamper: true)));

        // A handle no account holds: refused before it could be stored anywhere.
        SyntheticAuthenticator stranger = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        sent.Add(SentValue.OfBytes("passkey_public_keys", "webauthn_credential_id", stranger.CredentialId));
        await Step("passkey sign-in, unregistered handle", async statuses =>
            statuses.Add(await SignInAsync(anonymous, stranger, accountId, signCount: 1, tamper: false)));

        await Step("recovery-code redemption, wrong verifier", async statuses =>
        {
            HttpResponseMessage response = await anonymous.PostAsJsonAsync(
                RedemptionPath,
                new Dictionary<string, string> { ["verifier"] = Base64UrlText.Encode(RandomNumberGenerator.GetBytes(32)) });
            statuses.Add((int)response.StatusCode);
        });

        // A code off the card: a session of its own, then signed out again.
        await Step("recovery-code redemption, then sign-out", async statuses =>
        {
            HttpResponseMessage redeemed = await anonymous.PostAsJsonAsync(
                RedemptionPath, new Dictionary<string, string> { ["verifier"] = verifiers[0] });
            statuses.Add((int)redeemed.StatusCode);
            if (!redeemed.IsSuccessStatusCode)
            {
                return;
            }

            using HttpClient redeemedClient = CookieClient(factory, RegistrationCeremony.SessionCookieValueOf(redeemed));
            statuses.Add((int)(await redeemedClient.PostAsync(SignOutPath, content: null)).StatusCode);
        });

        NarrativeRows? written = null;
        NarrativeRows? toDelete = null;
        await Step("narrative writes", async statuses =>
        {
            written = await WriteNarrativeRowsAsync(client, statuses);
            toDelete = await WriteNarrativeRowsAsync(client, statuses);
        });
        NarrativeRows kept = written ?? throw new InvalidOperationException("The narrative writes step wrote nothing.");
        NarrativeRows doomed = toDelete ?? throw new InvalidOperationException("The narrative writes step wrote nothing.");

        // One name sealed twice. A browser draws a fresh nonce for every seal, so the refused attempt's
        // envelope differs from the stored one while its index is equal. SealedNarrative is deterministic
        // in its label, so each attempt gets an unrelated label — one sharing a long prefix would share
        // a 16-byte window with the stored envelope and be found by accident. The refused envelope is
        // never stored, so only the sent list makes it a needle.
        await Step("payee, duplicate nameKey", async statuses =>
        {
            string label = Marker("payee");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                NarrativeField envelope = SealedNarrative.Name($"{Guid.NewGuid():N}-payee-seal");
                if (attempt > 0)
                {
                    sent.Add(SentValue.OfBytes("payees", "name", envelope.Envelope.ToArray()));
                }

                HttpResponseMessage response = await client.PostAsJsonAsync("/api/payees", new
                {
                    id = Guid.CreateVersion7().ToString("D"),
                    name = Base64UrlText.Encode(envelope.Envelope.Span),
                    nameKey = SealedNarrative.EncodedIndex(label),
                });
                statuses.Add((int)response.StatusCode);
            }
        });

        await Step("single reads", async statuses =>
        {
            foreach (string path in new[]
                     {
                         $"/api/accounts/{kept.AccountId}", $"/api/payees/{kept.PayeeId}",
                         $"/api/category-groups/{kept.GroupId}", $"/api/categories/{kept.CategoryId}",
                         $"/api/transactions/{kept.TransactionId}",
                     })
            {
                statuses.Add((int)(await client.GetAsync(path)).StatusCode);
            }
        });

        await Step("narrative updates", async statuses =>
            await UpdateNarrativeRowsAsync(client, kept, statuses));

        await Step("narrative deletes", async statuses =>
        {
            foreach (string path in new[]
                     {
                         $"/api/transactions/{doomed.TransactionId}", $"/api/categories/{doomed.CategoryId}",
                         $"/api/category-groups/{doomed.GroupId}", $"/api/accounts/{doomed.AccountId}",
                     })
            {
                statuses.Add((int)(await client.DeleteAsync(path)).StatusCode);
            }
        });

        SyntheticAuthenticator second = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        sent.Add(SentValue.OfBytes("passkey_public_keys", "webauthn_credential_id", second.CredentialId));
        await Step("add passkey", async statuses =>
            statuses.AddRange(await AddPasskeyAsync(client, second)));

        // The same authenticator enrolled a second time. Nothing refuses it before the save — the options
        // leg only lists it under excludeCredentials, which a client is free to ignore — so the insert
        // reaches passkey_public_keys and fails on IX_passkey_public_keys_webauthn_credential_id, which
        // PasskeyRepository.TryAddAsync catches after the save. That table is exempt from row-level
        // security, so the unique violation is the one place a stored handle can come back from the
        // server inside an exception; the census has to see that exception reach a record. The handle is
        // already on the sent list from the step above.
        await Step(DuplicateHandleStep, async statuses =>
            statuses.AddRange(await AddPasskeyAsync(client, second)));

        await Step("recovery-code regeneration", async statuses =>
        {
            AssertionResult assertion = await ReauthenticateAsync(client, device, accountId, NextSignCount());
            HttpResponseMessage response = await client.PostAsJsonAsync(RecoveryCodesPath, new
            {
                codes = RegistrationCeremony.SubmissionsOf(RegistrationCeremony.CardOf(RegistrationCeremony.Verifiers())),
                manifest = ManifestFixture.Mint().Text,
                rotationEpoch = await FactorGeneration.NextAsync(client),
                credentialId = assertion.CredentialIdBase64Url,
                clientDataJson = assertion.ClientDataJsonBase64Url,
                authenticatorData = assertion.AuthenticatorDataBase64Url,
                signature = assertion.SignatureBase64Url,
                userHandle = assertion.UserHandleBase64Url,
            });
            statuses.Add((int)response.StatusCode);
        });

        await Step("passkey revocation", async statuses =>
        {
            Guid revoked = await CredentialIdOfHandleAsync(host.ConnectionString, second.CredentialId);
            AssertionResult assertion = await ReauthenticateAsync(client, device, accountId, NextSignCount());
            HttpResponseMessage response = await client.PostAsJsonAsync($"/api/me/credentials/{revoked}/revocation", new
            {
                manifest = ManifestFixture.Mint().Text,
                rotationEpoch = await FactorGeneration.NextAsync(client),
                credentialId = assertion.CredentialIdBase64Url,
                clientDataJson = assertion.ClientDataJsonBase64Url,
                authenticatorData = assertion.AuthenticatorDataBase64Url,
                signature = assertion.SignatureBase64Url,
                userHandle = assertion.UserHandleBase64Url,
            });
            statuses.Add((int)response.StatusCode);
        });

        Guid rotationId = Guid.CreateVersion7();
        await Step("key rotation, begin", async statuses =>
        {
            IReadOnlyList<Guid> factorIds = await FactorIdsAsync(host.ConnectionString, accountId);
            AssertionResult assertion = await ReauthenticateAsync(client, device, accountId, NextSignCount());
            HttpResponseMessage response = await client.PostAsJsonAsync(RotationPath, new
            {
                rotationId,
                manifest = ManifestFixture.Mint().Text,
                rotationEpoch = await FactorGeneration.NextAsync(client),
                seals = factorIds
                    .Select(factorId => (object)new
                    {
                        factorId,
                        encapsulatedAccountKeys = WrappedKeyFixture.MintFor(factorId).EncapsulatedAccountKeys,
                    })
                    .ToArray(),
                credentialId = assertion.CredentialIdBase64Url,
                clientDataJson = assertion.ClientDataJsonBase64Url,
                authenticatorData = assertion.AuthenticatorDataBase64Url,
                signature = assertion.SignatureBase64Url,
                userHandle = assertion.UserHandleBase64Url,
            });
            statuses.Add((int)response.StatusCode);
        });

        await Step("key rotation, chunk", async statuses =>
        {
            object chunk = await ResealEveryRowAsync(host.ConnectionString, accountId, rotationId);
            statuses.Add((int)(await client.PostAsJsonAsync($"{RotationPath}/chunks", chunk)).StatusCode);
        });

        await Step("key rotation, completion", async statuses =>
            statuses.Add((int)(await client.PostAsJsonAsync($"{RotationPath}/completion", new { rotationId })).StatusCode));

        Fill fill = new([], []);
        await Step("fill empty narrative columns from the inventory", async _ =>
            fill = await FillEmptyNarrativeColumnsAsync(host.ConnectionString));

        await Step("list reads", async statuses =>
        {
            foreach (string path in new[]
                     {
                         "/api/accounts", "/api/payees", "/api/category-groups", "/api/categories",
                         "/api/transactions", "/api/currencies", "/api/me", "/api/me/credentials",
                         "/api/me/account-keys", RecoveryCodesPath, RotationPath,

                         // Development's API description: no value in it, but a route the table
                         // declares, and the route floor asks for every one.
                         "/openapi/v1.json",
                     })
            {
                statuses.Add((int)(await client.GetAsync(path)).StatusCode);
            }

            // The health route names no method and needs no session, so it is read without one.
            statuses.Add((int)(await anonymous.GetAsync(ServiceDefaults.Extensions.HealthPath)).StatusCode);
        });

        await Step("export", async statuses =>
            statuses.Add((int)(await client.GetAsync(ExportPath)).StatusCode));

        // A body the JSON reader refuses part-way, after it has read an envelope's wire text and the
        // email — which is what a parser error that quoted its input would repeat.
        await Step(MalformedBodyStep, async statuses =>
        {
            string body = $"{{\"id\":\"{Guid.CreateVersion7():D}\",\"name\":\"{kept.AccountNameWire}\","
                          + $"\"email\":\"{email}\",\"type\":";
            using StringContent content = new(body, Encoding.UTF8, "application/json");
            statuses.Add((int)(await client.PostAsync("/api/accounts", content)).StatusCode);
        });

        // A fault: an account holding no factor manifest issuing a card. A second account, so the
        // primary one stays whole for the steps after.
        string faultSubject = Marker("subject");
        string faultEmail = $"{Marker("email")}@example.test";
        sent.Add(SentValue.OfText("credentials", "subject", faultSubject));
        sent.Add(SentValue.OfText("users", "email", faultEmail));
        await Step("forced 500, recovery codes with no factor manifest", async statuses =>
            statuses.Add(await IssueCardWithNoManifestAsync(host, factory, faultSubject, faultEmail)));

        // A fault: the export refuses an owned set that is not exactly the ambient budget.
        // The second budget is a state only this seed reaches, and it is taken away again before the
        // erasure: an erasure over two owned budgets deletes the ambient budget's transactions only and
        // then fails the cascade on FK_transactions_budgets_budget_id (measured), which is a 500 about
        // the seed rather than the erasure the census wants driven.
        Guid seeded = Guid.Empty;
        await Step("forced 500, export over two budgets", async statuses =>
        {
            seeded = await RepositoryTestHost.SeedAdditionalBudgetOnAsync(
                host.ConnectionString, accountId, Marker("budget"));
            statuses.Add((int)(await client.GetAsync(ExportPath)).StatusCode);
        });

        // Its own step, so the snapshot after the one above reads the seeded budget while it stands.
        await Step("take the second budget away again", async _ =>
            await DeleteBudgetAsync(host.ConnectionString, seeded));

        // The email change, four ways: an address the fault account holds under this account's own
        // subject (409 email_already_linked), the fault account's subject (409 provider_identity_in_use),
        // an address the provider does not vouch for (401 email_unverified, refused by the provider gate
        // before the passkey), then a new subject and address (200). The success retires the credential holding `subject` and
        // moves `email` out of users, so the old values survive only in the snapshots and on the sent
        // list; the new ones are stored until the erasure below takes them. The refused address is new and
        // never stored, so only the sent list makes it a needle.
        string movedSubject = Marker("moved-subject");
        string movedEmail = $"{Marker("moved-email")}@Log-Census.Example";
        string inUseRefusedEmail = $"{Marker("in-use-email")}@example.test";
        string unverifiedEmail = $"{Marker("unverified-email")}@example.test";
        sent.Add(SentValue.OfText("credentials", "subject", movedSubject));
        sent.Add(SentValue.OfText("users", "email", movedEmail));
        sent.Add(SentValue.OfText("users", "email", inUseRefusedEmail));
        sent.Add(SentValue.OfText("users", "email", unverifiedEmail));
        await Step(EmailChangeStep, async statuses =>
        {
            (string Subject, string Email, string EmailVerified, string? ConflictKind)[] attempts =
            [
                (subject, faultEmail, "true", "email_already_linked"),
                (faultSubject, inUseRefusedEmail, "true", "provider_identity_in_use"),
                (movedSubject, unverifiedEmail, "false", null),
                (movedSubject, movedEmail, "true", null),
            ];

            foreach ((string attemptSubject, string attemptEmail, string emailVerified, string? conflictKind) in attempts)
            {
                AssertionResult assertion = await ReauthenticateAsync(client, device, accountId, NextSignCount());
                using HttpRequestMessage request = EmailChangeRequest(assertion);
                request.Headers.Add(TestAuthHandler.SubjectHeader, attemptSubject);
                request.Headers.Add(TestAuthHandler.EmailHeader, attemptEmail);
                request.Headers.Add(TestAuthHandler.EmailVerifiedHeader, emailVerified);
                HttpResponseMessage response = await client.SendAsync(request);
                statuses.Add((int)response.StatusCode);
                await RequireConflictKindAsync(EmailChangeStep, response, conflictKind);
            }
        });

        await Step(ErasureStep, async statuses =>
        {
            AssertionResult assertion = await ReauthenticateAsync(client, device, accountId, NextSignCount());
            HttpResponseMessage response = await client.PostAsJsonAsync(ErasurePath, new
            {
                credentialId = assertion.CredentialIdBase64Url,
                clientDataJson = assertion.ClientDataJsonBase64Url,
                authenticatorData = assertion.AuthenticatorDataBase64Url,
                signature = assertion.SignatureBase64Url,
                userHandle = assertion.UserHandleBase64Url,
            });
            statuses.Add((int)response.StatusCode);
        });

        return new Run(steps, fill.Filled, fill.StillEmpty, subject, email, sent);
    }

    /// <summary>
    /// Drives the registration routes of a host whose provider scheme is the real <c>JwtBearer</c>
    /// handler, holding <paramref name="signingKey" />: a token that validates and registers a new
    /// account, one that validates and names an account already registered, and one whose signature is
    /// forged.
    /// </summary>
    public static async Task<BearerRun> DriveProviderTokensAsync(
        ApiFactory factory,
        LogRecorder recorder,
        SecurityKey signingKey,
        string registeredSubject,
        string registeredEmail)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(signingKey);

        List<Step> steps = [];
        List<SentValue> sent = [];
        string freshSubject = Marker("bearer-subject");
        string freshEmail = $"{Marker("bearer-email")}@Bearer.Example";
        sent.Add(SentValue.OfText("credentials", "subject", freshSubject));
        sent.Add(SentValue.OfText("users", "email", freshEmail));
        sent.Add(SentValue.OfText("credentials", "subject", registeredSubject));
        sent.Add(SentValue.OfText("users", "email", registeredEmail));

        // A token carries the subject and the address as base64url JSON, which no needle renders — so
        // the token itself is the needle: whole, signed part, and payload alone, each filed under the
        // subject it carries. Every token this traffic sends goes through here, the forged one included.
        string Sent(string token)
        {
            int signatureDot = token.LastIndexOf('.');
            string[] segments = token.Split('.');
            sent.Add(SentValue.OfText("credentials", "subject", token));
            sent.Add(SentValue.OfText("credentials", "subject", token[..signatureDot]));
            sent.Add(SentValue.OfText("credentials", "subject", segments[1]));

            return token;
        }

        // Both registration legs name the provider scheme and nothing else, so a 2xx on either is the
        // bearer handler having validated the token.
        // The account the validated token registers, kept for the email change below: its cookie, its
        // authenticator and the handle the authenticator holds.
        string registeredCookie = string.Empty;
        SyntheticAuthenticator? registeredDevice = null;
        byte[] registeredHandle = [];

        await StepAsync(steps, recorder, ValidatedTokenStep, async statuses =>
        {
            using HttpClient client = BearerClient(factory, Sent(ProviderToken(signingKey, freshSubject, freshEmail)));
            HttpResponseMessage optionsResponse = await client.PostAsync(RegistrationCeremony.OptionsPath, content: null);
            statuses.Add((int)optionsResponse.StatusCode);
            if (!optionsResponse.IsSuccessStatusCode)
            {
                return;
            }

            JsonObject body = await RegistrationCeremony.ReadJsonObjectAsync(optionsResponse);
            IssuedRegistrationOptions options = new(
                Base64UrlText.Decode(body["challenge"]!.GetValue<string>()),
                Base64UrlText.Decode(body["user"]!["id"]!.GetValue<string>()));
            SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
            AttestationResult attestation = device.Register(
                options.Challenge, ApiFactory.PasskeyOrigin, signCount: 0, prfEnabled: true, userHandle: options.UserHandle);
            sent.Add(SentValue.OfBytes("passkey_public_keys", "webauthn_credential_id", device.CredentialId));
            HttpResponseMessage finished = await RegistrationCeremony.PostAsync(
                client, attestation, WrappedKeyFixture.Mint(), RegistrationCeremony.CardOf(RegistrationCeremony.Verifiers()));
            statuses.Add((int)finished.StatusCode);
            if (finished.IsSuccessStatusCode)
            {
                registeredCookie = RegistrationCeremony.SessionCookieValueOf(finished);
                registeredDevice = device;
                registeredHandle = options.UserHandle;
            }
        });

        await StepAsync(steps, recorder, "provider token, validated, registered identity", async statuses =>
        {
            using HttpClient client = BearerClient(factory, Sent(ProviderToken(signingKey, registeredSubject, registeredEmail)));
            statuses.Add((int)(await client.PostAsync(RegistrationCeremony.OptionsPath, content: null)).StatusCode);
        });

        // The same header and payload a valid token carries, and random bytes where its signature was.
        await StepAsync(steps, recorder, ForgedTokenStep, async statuses =>
        {
            string valid = Sent(ProviderToken(signingKey, registeredSubject, registeredEmail));
            string forged = Sent($"{valid[..valid.LastIndexOf('.')]}.{Base64UrlText.Encode(RandomNumberGenerator.GetBytes(256))}");
            using HttpClient client = BearerClient(factory, forged);
            statuses.Add((int)(await client.PostAsync(RegistrationCeremony.OptionsPath, content: null)).StatusCode);
        });

        // The email change on this host, so the bearer handler's own records on that route are searched:
        // a cookie of the account registered above, a forged token and then a valid one, each beside a
        // fresh assertion. The forged token is refused by the provider filter before the passkey gate.
        string movedSubject = Marker("bearer-moved-subject");
        string movedEmail = $"{Marker("bearer-moved-email")}@Bearer.Example";
        sent.Add(SentValue.OfText("credentials", "subject", movedSubject));
        sent.Add(SentValue.OfText("users", "email", movedEmail));
        uint registeredCount = 0;

        async Task<int> ChangeEmailAsync(string token)
        {
            SyntheticAuthenticator device = registeredDevice
                ?? throw new InvalidOperationException(
                    $"'{ValidatedTokenStep}' registered no account, so there is no session to change an email on.");
            using HttpClient client = CookieClient(factory, registeredCookie);
            byte[] challenge = await RegistrationCeremony.BeginCeremonyAsync(client, ReauthenticationOptionsPath);
            AssertionResult assertion = device.Authenticate(
                challenge, ApiFactory.PasskeyOrigin, registeredHandle, ++registeredCount);
            using HttpRequestMessage request = EmailChangeRequest(assertion);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return (int)(await client.SendAsync(request)).StatusCode;
        }

        await StepAsync(steps, recorder, ForgedEmailChangeStep, async statuses =>
        {
            string valid = Sent(ProviderToken(signingKey, movedSubject, movedEmail));
            string forged = Sent($"{valid[..valid.LastIndexOf('.')]}.{Base64UrlText.Encode(RandomNumberGenerator.GetBytes(256))}");
            statuses.Add(await ChangeEmailAsync(forged));
        });

        await StepAsync(steps, recorder, BearerEmailChangeStep, async statuses =>
            statuses.Add(await ChangeEmailAsync(Sent(ProviderToken(signingKey, movedSubject, movedEmail)))));

        return new BearerRun(steps, sent);
    }

    /// <summary>
    /// Writes an envelope into every row of every narrative column that holds no value yet, and reports
    /// which columns it wrote and which it could not.
    /// </summary>
    public static async Task<Fill> FillEmptyNarrativeColumnsAsync(string adminConnectionString)
    {
        List<string> filled = [];
        List<string> stillEmpty = [];
        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();

        foreach (ColumnClassificationEntry column in DataInventory.Of(ColumnClassification.Narrative))
        {
            string table = Quote(column.Table);
            string name = Quote(column.Column);

            await using NpgsqlCommand count = new(
                $"select count(*) from public.{table} where {name} is not null", admin);
            if ((long)(await count.ExecuteScalarAsync())! > 0)
            {
                continue;
            }

            await using NpgsqlCommand fill = new(
                $"update public.{table} set {name} = @value where {name} is null", admin);
            fill.Parameters.AddWithValue(
                "value",
                SealedNarrative.Name($"log census fill for {column.Qualified}").Envelope.ToArray());
            if (await fill.ExecuteNonQueryAsync() > 0)
            {
                filled.Add(column.Qualified);
            }
            else
            {
                stillEmpty.Add(column.Qualified);
            }
        }

        return new Fill(filled, stillEmpty);
    }

    public static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>A value distinctive enough to be a needle: longer than the census floor on its own.</summary>
    public static string Marker(string what) => $"log-census-{what}-{Guid.NewGuid():N}";

    private static async Task StepAsync(
        List<Step> steps,
        LogRecorder recorder,
        string name,
        Func<List<int>, Task> act,
        Func<string, Task>? snapshot = null)
    {
        int before = recorder.Snapshot().Count;
        List<int> statuses = [];
        await act(statuses);
        steps.Add(new Step(name, statuses, [.. recorder.Snapshot().Skip(before)]));

        if (snapshot is not null)
        {
            await snapshot($"after '{name}'");
        }
    }

    private static HttpClient CookieClient(ApiFactory factory, string cookie)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookieAuthenticationTests.CookieName}={cookie}");

        return client;
    }

    private static HttpClient BearerClient(ApiFactory factory, string token)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    /// <summary>An RS256 token the application's bearer handler accepts when it holds <paramref name="key" />.</summary>
    private static string ProviderToken(SecurityKey key, string subject, string email)
    {
        DateTime now = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ProviderIssuer,
            Audience = ProviderAudience,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["email"] = email,
                ["email_verified"] = true,
            },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>
    /// Runs a registration that is expected to be refused, and returns every status it met. A handle the
    /// attempt sends to the finish leg is added to <paramref name="sent" />.
    /// </summary>
    private static async Task<IReadOnlyList<int>> AttemptRegistrationAsync(
        ApiFactory factory,
        string subject,
        string email,
        List<SentValue> sent)
    {
        using HttpClient provider = factory.CreateAuthenticatedClient(subject, email);
        HttpResponseMessage options = await provider.PostAsync(RegistrationCeremony.OptionsPath, content: null);
        if (!options.IsSuccessStatusCode)
        {
            return [(int)options.StatusCode];
        }

        JsonObject issued = await RegistrationCeremony.ReadJsonObjectAsync(options);
        byte[] challenge = Base64UrlText.Decode(issued["challenge"]!.GetValue<string>());
        byte[] userHandle = Base64UrlText.Decode(issued["user"]!["id"]!.GetValue<string>());
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        AttestationResult attestation = device.Register(
            challenge, ApiFactory.PasskeyOrigin, signCount: 0, prfEnabled: true, userHandle: userHandle);
        sent.Add(SentValue.OfBytes("passkey_public_keys", "webauthn_credential_id", device.CredentialId));
        HttpResponseMessage finished = await RegistrationCeremony.PostAsync(
            provider,
            attestation,
            WrappedKeyFixture.Mint(),
            RegistrationCeremony.CardOf(RegistrationCeremony.Verifiers()));

        return [(int)options.StatusCode, (int)finished.StatusCode];
    }

    private static async Task<int> SignInAsync(
        HttpClient anonymous,
        SyntheticAuthenticator device,
        Guid accountId,
        uint signCount,
        bool tamper)
    {
        byte[] challenge = await RegistrationCeremony.BeginCeremonyAsync(anonymous, AssertionOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge, ApiFactory.PasskeyOrigin, PasskeyEncoding.ToUserHandle(accountId), signCount);
        byte[] signature = [.. assertion.Signature];
        if (tamper)
        {
            signature[^1] ^= 0xFF;
        }

        HttpResponseMessage response = await anonymous.PostAsJsonAsync(AssertionPath, new
        {
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = Base64UrlText.Encode(signature),
            userHandle = assertion.UserHandleBase64Url,
        });

        return (int)response.StatusCode;
    }

    /// <summary>An email-change POST carrying <paramref name="assertion" />; the caller adds the provider token.</summary>
    private static HttpRequestMessage EmailChangeRequest(AssertionResult assertion) =>
        new(HttpMethod.Post, EmailChangePath)
        {
            Content = JsonContent.Create(new
            {
                credentialId = assertion.CredentialIdBase64Url,
                clientDataJson = assertion.ClientDataJsonBase64Url,
                authenticatorData = assertion.AuthenticatorDataBase64Url,
                signature = assertion.SignatureBase64Url,
                userHandle = assertion.UserHandleBase64Url,
            }),
        };

    /// <summary>
    /// Throws unless a 409 carries <paramref name="expected" /> as its <c>conflictKind</c>; a null
    /// expectation asks for no 409 at all. A status alone cannot tell the two email-change refusals apart.
    /// </summary>
    private static async Task RequireConflictKindAsync(string step, HttpResponseMessage response, string? expected)
    {
        string? actual = null;
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            JsonObject body = await RegistrationCeremony.ReadJsonObjectAsync(response);
            actual = body["conflictKind"]?.GetValue<string>() ?? "<no conflictKind>";
        }

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{step}' expected conflictKind '{expected ?? "<none>"}' and got '{actual ?? "<none>"}' "
                + $"on a {(int)response.StatusCode}.");
        }
    }

    /// <summary>Runs the re-authentication options leg and has <paramref name="device" /> answer it.</summary>
    private static async Task<AssertionResult> ReauthenticateAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid accountId,
        uint signCount)
    {
        byte[] challenge = await RegistrationCeremony.BeginCeremonyAsync(client, ReauthenticationOptionsPath);

        return device.Authenticate(
            challenge, ApiFactory.PasskeyOrigin, PasskeyEncoding.ToUserHandle(accountId), signCount);
    }

    /// <summary>Both legs of adding a passkey to a signed-in account.</summary>
    private static async Task<IReadOnlyList<int>> AddPasskeyAsync(HttpClient client, SyntheticAuthenticator device)
    {
        byte[] challenge = await RegistrationCeremony.BeginCeremonyAsync(client, PasskeyRegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge, ApiFactory.PasskeyOrigin, signCount: 0, prfEnabled: true);
        WrappedKeyFixture keys = WrappedKeyFixture.Mint();

        HttpResponseMessage response = await client.PostAsJsonAsync(PasskeyRegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = keys.FactorId,
            wrappedPrivateKey = keys.WrappedPrivateKey,
            encapsulatedAccountKeys = keys.EncapsulatedAccountKeys,
            manifest = ManifestFixture.Mint().Text,
            rotationEpoch = await FactorGeneration.NextAsync(client),
        });

        return [(int)response.StatusCode];
    }

    /// <summary>
    /// Writes one row carrying every narrative column the route table can write, and returns what it
    /// created.
    /// </summary>
    private static async Task<NarrativeRows> WriteNarrativeRowsAsync(HttpClient client, List<int> statuses)
    {
        async Task PostAsync(string path, object body) =>
            statuses.Add((int)(await client.PostAsJsonAsync(path, body)).StatusCode);

        Guid accountId = Guid.CreateVersion7();
        string accountName = Marker("account-name");
        string accountNameWire = SealedNarrative.EncodedName(accountName);
        await PostAsync("/api/accounts", new
        {
            id = accountId.ToString("D"),
            name = accountNameWire,
            nameKey = SealedNarrative.EncodedIndex(accountName),
            type = "Checking",
            openingBalance = 100m,
            currencyCode = "USD",
        });

        Guid payeeId = Guid.CreateVersion7();
        string payeeName = Marker("payee-name");
        await PostAsync("/api/payees", new
        {
            id = payeeId.ToString("D"),
            name = SealedNarrative.EncodedName(payeeName),
            nameKey = SealedNarrative.EncodedIndex(payeeName),
        });

        Guid groupId = Guid.CreateVersion7();
        string groupName = Marker("group-name");
        await PostAsync("/api/category-groups", new
        {
            id = groupId.ToString("D"),
            name = SealedNarrative.EncodedName(groupName),
            nameKey = SealedNarrative.EncodedIndex(groupName),
            description = SealedNarrative.EncodedDescription(Marker("group-description")),
        });

        Guid categoryId = Guid.CreateVersion7();
        string categoryName = Marker("category-name");
        await PostAsync("/api/categories", new
        {
            id = categoryId.ToString("D"),
            name = SealedNarrative.EncodedName(categoryName),
            nameKey = SealedNarrative.EncodedIndex(categoryName),
            description = SealedNarrative.EncodedDescription(Marker("category-description")),
            categoryGroupId = groupId,
        });

        Guid transactionId = Guid.CreateVersion7();
        await PostAsync("/api/transactions", new
        {
            id = transactionId.ToString("D"),
            amount = -42.50m,
            date = "2026-06-12",
            accountId,
            payeeId,
            categoryId,
            description = SealedNarrative.EncodedDescription(Marker("transaction-description")),
        });

        return new NarrativeRows(accountId, payeeId, groupId, categoryId, transactionId, accountNameWire);
    }

    /// <summary>
    /// Replaces every narrative value of <paramref name="rows" /> through the update routes, and moves
    /// the group and the category so the two position routes are driven as well.
    /// </summary>
    private static async Task UpdateNarrativeRowsAsync(HttpClient client, NarrativeRows rows, List<int> statuses)
    {
        string accountName = Marker("account-rename");
        statuses.Add((int)(await client.PutAsJsonAsync($"/api/accounts/{rows.AccountId}", new
        {
            name = SealedNarrative.EncodedName(accountName),
            nameKey = SealedNarrative.EncodedIndex(accountName),
            type = "Savings",
            openingBalance = 50m,
        })).StatusCode);

        string payeeName = Marker("payee-rename");
        statuses.Add((int)(await client.PatchAsJsonAsync($"/api/payees/{rows.PayeeId}", new
        {
            name = SealedNarrative.EncodedName(payeeName),
            nameKey = SealedNarrative.EncodedIndex(payeeName),
        })).StatusCode);

        string groupName = Marker("group-rename");
        statuses.Add((int)(await client.PutAsJsonAsync($"/api/category-groups/{rows.GroupId}", new
        {
            name = SealedNarrative.EncodedName(groupName),
            nameKey = SealedNarrative.EncodedIndex(groupName),
            description = SealedNarrative.EncodedDescription(Marker("group-redescription")),
        })).StatusCode);

        string categoryName = Marker("category-rename");
        statuses.Add((int)(await client.PutAsJsonAsync($"/api/categories/{rows.CategoryId}", new
        {
            name = SealedNarrative.EncodedName(categoryName),
            nameKey = SealedNarrative.EncodedIndex(categoryName),
            description = SealedNarrative.EncodedDescription(Marker("category-redescription")),
        })).StatusCode);

        statuses.Add((int)(await client.PatchAsJsonAsync($"/api/transactions/{rows.TransactionId}", new
        {
            description = SealedNarrative.EncodedDescription(Marker("transaction-redescription")),
        })).StatusCode);

        statuses.Add((int)(await client.PatchAsJsonAsync(
            $"/api/category-groups/{rows.GroupId}/position", new { position = 0 })).StatusCode);
        statuses.Add((int)(await client.PatchAsJsonAsync(
            $"/api/categories/{rows.CategoryId}/placement", new { categoryGroupId = rows.GroupId, position = 0 })).StatusCode);
    }

    /// <summary>
    /// One chunk re-sealing every narrative row of the account's one budget under new labels — every
    /// row the completion's gate counts, so the completion can succeed.
    /// </summary>
    private static async Task<object> ResealEveryRowAsync(string adminConnectionString, Guid userId, Guid rotationId)
    {
        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();

        await using NpgsqlCommand budget = new("select id from budgets where user_id = @user", admin);
        budget.Parameters.AddWithValue("user", userId);
        Guid budgetId = await budget.ExecuteScalarAsync() is Guid id
            ? id
            : throw new InvalidOperationException($"No budget is filed under account '{userId}'.");

        async Task<IReadOnlyList<Guid>> IdsAsync(string table, string extra = "")
        {
            await using NpgsqlCommand command = new(
                $"select id from public.{Quote(table)} where budget_id = @budget {extra} order by id", admin);
            command.Parameters.AddWithValue("budget", budgetId);
            List<Guid> ids = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetGuid(0));
            }

            return ids;
        }

        object NameEntry(Guid id, string what)
        {
            string label = Marker(what);
            return new { id, name = SealedNarrative.EncodedName(label), nameKey = SealedNarrative.EncodedIndex(label) };
        }

        object NamedAndDescribed(Guid id, string what)
        {
            string label = Marker(what);
            return new
            {
                id,
                name = SealedNarrative.EncodedName(label),
                nameKey = SealedNarrative.EncodedIndex(label),
                description = SealedNarrative.EncodedDescription(Marker($"{what}-description")),
            };
        }

        return new
        {
            rotationId,
            accounts = (await IdsAsync("accounts")).Select(id => NameEntry(id, "account-reseal")).ToArray(),
            payees = (await IdsAsync("payees")).Select(id => NameEntry(id, "payee-reseal")).ToArray(),
            categoryGroups = (await IdsAsync("category_groups")).Select(id => NamedAndDescribed(id, "group-reseal")).ToArray(),
            categories = (await IdsAsync("categories")).Select(id => NamedAndDescribed(id, "category-reseal")).ToArray(),
            transactions = (await IdsAsync("transactions", "and description is not null"))
                .Select(id => (object)new
                {
                    id,
                    description = SealedNarrative.EncodedDescription(Marker("transaction-reseal")),
                })
                .ToArray(),
        };
    }

    private static async Task<IReadOnlyList<Guid>> FactorIdsAsync(string adminConnectionString, Guid userId)
    {
        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new(
            "select factor_id from wrapped_account_keys where user_id = @user order by factor_id", admin);
        command.Parameters.AddWithValue("user", userId);

        List<Guid> factorIds = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            factorIds.Add(reader.GetGuid(0));
        }

        return factorIds;
    }

    private static async Task DeleteBudgetAsync(string adminConnectionString, Guid budgetId)
    {
        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new("delete from budgets where id = @budget", admin);
        command.Parameters.AddWithValue("budget", budgetId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> CredentialIdOfHandleAsync(string adminConnectionString, byte[] handle)
    {
        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand command = new(
            "select credential_id from passkey_public_keys where webauthn_credential_id = @handle", admin);
        command.Parameters.AddWithValue("handle", handle);

        return await command.ExecuteScalarAsync() is Guid credentialId
            ? credentialId
            : throw new InvalidOperationException("No passkey carries the handle the revocation names.");
    }

    /// <summary>
    /// Issues a card for a second account that holds a passkey and no factor manifest, which the route
    /// answers with a fault.
    /// </summary>
    private static async Task<int> IssueCardWithNoManifestAsync(
        PostgresTestHost host,
        ApiFactory factory,
        string subject,
        string email)
    {
        ApiFactory.SignedInClient signedIn = await factory.CreateSignedInClientAsync(
            subject, email, withFactorManifest: false);
        SyntheticAuthenticator device = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        await RepositoryTestHost.SeedPasskeyOnAsync(
            host.ConnectionString, signedIn.UserId, device.CredentialId, device.CoseKey, device.Algorithm);

        byte[] challenge = await RegistrationCeremony.BeginCeremonyAsync(signedIn.Client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge, ApiFactory.PasskeyOrigin, PasskeyEncoding.ToUserHandle(signedIn.UserId), signCount: 0);

        HttpResponseMessage response = await signedIn.Client.PostAsJsonAsync(RecoveryCodesPath, new
        {
            codes = RegistrationCeremony.SubmissionsOf(RegistrationCeremony.CardOf(RegistrationCeremony.Verifiers())),
            manifest = ManifestFixture.Mint().Text,
            rotationEpoch = FactorManifest.MinimumRotationEpoch,
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
        signedIn.Client.Dispose();

        return (int)response.StatusCode;
    }
}
