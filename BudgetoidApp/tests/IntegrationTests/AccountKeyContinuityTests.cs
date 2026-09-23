using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Application.Passkeys;
using Npgsql;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// The account's two keys stay reachable across a change to its factor set: every factor that should
/// still open them does, to the same pair, and a value sealed under that pair is still readable.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these tests add is state-dependent, and nothing more.</b> Every production mutation measured
/// as reddening one of them at an open is also caught by an older test. What no older test holds is a
/// claim about a state reached by a sequence: code factors still opening after a passkey is revoked, a
/// first factor still opening after a second registration, and a narrative value sealed under the
/// account keys still opening after the factor set moves.
/// </para>
/// <para>
/// <b>Arranged with <see cref="AccountKeyFixture" />, never with <see cref="WrappedKeyFixture" /></b>,
/// because every claim here is that something <em>opens</em>. The fixture's remarks say why the two must
/// stay apart.
/// </para>
/// <para>
/// <b>Every account here is signed in over the default seeded passkey.</b> That seeding writes no
/// <c>wrapped_account_keys</c> row (see <c>RepositoryTestHost.SeedPasskeyOnAsync</c>), so the account
/// holds exactly the factors a test registers and "every surviving factor" is a literal list rather
/// than a subset of one. The seeded passkey cannot sign either — its COSE key is four placeholder
/// bytes — so every ceremony here is proved by a device the test itself registered. It is still a
/// passkey credential, so the account holds one more passkey than it has passkey factors — a shape
/// no real account has, since a real passkey never exists without its factor row.
/// </para>
/// <para>
/// <b>The recovery-codes sign-in arm is refused, not overlooked.</b> Issuing a card replaces the
/// account's set, and replacing the set sweeps the sessions it opened — including this client's own.
/// The replacement cookie never reaches the client, whose <c>Cookie</c> header is pinned
/// (<c>ApiFactory.CreateCookieClient</c>), so every request after the issue would answer 401.
/// </para>
/// <para>
/// <b>Recovered keys are compared as base64url strings, never as <c>byte[]</c></b>: TUnit's
/// <c>IsEquivalentTo</c> ignores order by default, which is blind to the one mistake here worth
/// catching — two halves in the wrong place.
/// </para>
/// <para>
/// Rows are read on <see cref="PostgresTestHost.ConnectionString" />, the container superuser.
/// <c>wrapped_account_keys</c> carries <c>user_isolation</c>, so a policed read would report a surviving
/// row as absent.
/// </para>
/// </remarks>
public sealed class AccountKeyContinuityTests
{
    private const string Subject = "google-key-continuity";

    private const string ReauthenticationOptionsPath = "/api/passkeys/reauthentication/options";
    private const string RegistrationOptionsPath = "/api/passkeys/registration/options";
    private const string RegistrationPath = "/api/passkeys/registration";
    private const string PayeesPath = "/api/payees";

    private const string RecoveryCodesPath = "/api/me/recovery-codes";

    /// <summary>
    /// The size of a recovery-code card, declared here rather than read off the handler: a handler
    /// constant that drifted would otherwise move this test's expectation with it.
    /// </summary>
    private const int CardSize = 10;

    private const string PayeeTable = "payees";
    private const string PayeeNameColumn = "name";

    /// <summary>
    /// Twelve UTF-8 bytes, so a 41-byte envelope: 41 is not a multiple of 3, so padded standard base64
    /// emits padding and differs from the unpadded base64url wire. That is what lets the name read-back
    /// pin the encoding as well as the value.
    /// </summary>
    private const string PayeeName = "Corner Store";

    /// <summary>
    /// Three registered passkeys and a card of ten recovery codes over one account's keys, one of those
    /// passkeys revoked — and each of the twelve surviving factors still opens, from the rows the
    /// server stored, to the account's content key and index key, under which a payee name sealed
    /// before the revocation is still readable through the API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This test guards a conjunction, not a route.</b> It is red if registration or the card issue
    /// filed the wrong key material or filed it under the wrong factor, if the payee write or read path
    /// changed a byte of the ciphertext or served the wrong row id, if revocation disturbed a surviving
    /// passkey's row, or if revoking a passkey disturbed a code factor's row. The passkey survivors are
    /// held by byte equality <em>and</em> an open; the card is held by an open only. It adds
    /// <b>nothing</b> on <c>RevokePasskeyHandler</c> beyond <c>CredentialRevocationTests</c>. That
    /// handler writes no key material — the revoked row leaves by the database's
    /// <c>ON DELETE CASCADE</c> — so no mutation that still answers 200 and still removes the named
    /// passkey reddens this test. Mutations that break either half do redden it, at the factor census:
    /// measured, commenting out the <c>DeletePasskeyAsync</c> call still answers 200 and leaves the
    /// revoked factor's row; reasoned, negating the last-passkey floor refuses the revocation. A floor
    /// loosened as far as <c>&lt;= 3</c> survives, because the seeded passkey is a fourth credential.
    /// Do not read this as coverage of the revocation route; <c>CredentialRevocationTests</c> is that.
    /// </para>
    /// <para>
    /// <b>The passkey survivors' opens add nothing against production, and are kept anyway.</b> Their
    /// stored bytes are asserted equal to what was posted <em>before</em> they are opened, and what was
    /// posted opens by construction — so against the real server those opens can never be the first
    /// check to fail. Measured: feeding <see cref="AccountKeyFixture.Factor.TryOpen" /> the fixture's own
    /// envelopes instead of the stored bytes leaves this test green, which is exactly what a redundant
    /// open looks like. Their job is to keep the chain standing if someone deletes, loosens or pastes
    /// over the byte expectations.
    /// </para>
    /// <para>
    /// <b>The card is opened and never compared byte for byte — do not "harmonise" the two loops.</b>
    /// Byte equality ahead of the ten opens would make them logically implied, the same shape as the
    /// passkey loop above. Stored == posted for a card is already pinned by
    /// <c>RecoveryCodeGenerationTests.RecoveryCodeGeneration_FilesOneWrappedKeyRowPerCode_EachUnderItsOwnFactor</c>.
    /// Open-only makes the open the first check to see a card row with the right width and the right
    /// version but the wrong content.
    /// </para>
    /// <para>
    /// <b>The payee is opened under the content key a survivor handed back and under the id the server
    /// served.</b> The key half adds nothing against production — it is already asserted equal to
    /// <see cref="AccountKeyFixture.ContentKey" /> — and is kept for the same reason as the passkey
    /// opens. The id half is real: <c>PayeeDto.Id</c> is the associated data <c>Name</c> was sealed
    /// against, and a browser opens under the id it is served. <c>Guid.Parse</c> alone is laxer than
    /// the browser — it takes braced, upper-case and undashed spellings the browser refuses outright —
    /// so the served string is first asserted to be the canonical <c>"D"</c> spelling of what it
    /// parses to. That spelling check is what makes the open read the id as a browser would. There is
    /// deliberately no <c>servedId == payeeId</c> assertion, which would fire first and hollow the
    /// open out again.
    /// </para>
    /// </remarks>
    [Test]
    public async Task Revocation_LeavesEverySurvivingFactorOpeningTheAccountKeys_AndItsSealedPayeeNameStillReadable()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);

        AccountKeyFixture keys = AccountKeyFixture.Mint();
        SyntheticAuthenticator provingDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator otherDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator revokedDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        AccountKeyFixture.Factor provingSurvivor = keys.MintFactor();
        AccountKeyFixture.Factor otherSurvivor = keys.MintFactor();
        AccountKeyFixture.Factor revoked = keys.MintFactor();

        (await RegisterPasskeyAsync(client, provingDevice, provingSurvivor)).EnsureSuccessStatusCode();
        (await RegisterPasskeyAsync(client, otherDevice, otherSurvivor)).EnsureSuccessStatusCode();
        (await RegisterPasskeyAsync(client, revokedDevice, revoked)).EnsureSuccessStatusCode();

        // Every real account holds a card beside its passkeys, so this one does too.
        AccountKeyFixture.RecoveryCodeFactor[] card =
            [.. Enumerable.Range(0, CardSize).Select(_ => keys.MintRecoveryCodeFactor())];
        (await IssueRecoveryCodesAsync(client, provingDevice, userId, card)).EnsureSuccessStatusCode();
        AccountKeyFixture.Factor[] codeFactors = [.. card.Select(code => code.Factor)];

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        Guid revokedCredentialId = await ResolveCredentialIdAsync(admin, revokedDevice);

        // Exactly the thirteen factors this test arranged, or "every survivor" below is a subset.
        WrappedAccountKeysRow[] before = await WrappedAccountKeysAsync(admin, userId);
        await Assert.That(FactorIds(before))
            .IsEqualTo(FactorIds([provingSurvivor, otherSurvivor, revoked, .. codeFactors]))
            .Because("the account must hold exactly the three passkey factors and ten code factors arranged here before the act");

        Guid payeeId = Guid.CreateVersion7();
        string sealedName = keys.SealNarrative(PayeeTable, PayeeNameColumn, payeeId, PayeeName);

        // The encoding guard SealedNarrative prescribes: "Corner Store" is 12 bytes, so the envelope is
        // 41 bytes and padded base64 emits padding. Without this, a read-back through the wrong encoder
        // could produce the same string and the name assertion would pin the value only.
        await Assert.That(Convert.ToBase64String(Base64UrlText.Decode(sealedName)))
            .IsNotEqualTo(sealedName)
            .Because("the payee name must be one whose padded base64 differs from its unpadded base64url wire");

        HttpResponseMessage created = await client.PostAsJsonAsync(PayeesPath, new
        {
            id = payeeId.ToString("D"),
            name = sealedName,
            nameKey = SealedNarrative.EncodedIndex(PayeeName),
        });
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);

        // Act — the survivor proves, and the route names the third passkey.
        HttpResponseMessage response = await RevokeAsync(client, provingDevice, userId, revokedCredentialId);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        WrappedAccountKeysRow[] after = await WrappedAccountKeysAsync(admin, userId);
        await Assert.That(FactorIds(after))
            .IsEqualTo(FactorIds([provingSurvivor, otherSurvivor, .. codeFactors]))
            .Because("the revoked factor's row must be gone and exactly the two passkeys and ten codes must remain");

        byte[]? contentKeyASurvivorHandedBack = null;

        foreach (AccountKeyFixture.Factor survivor in new[] { provingSurvivor, otherSurvivor })
        {
            WrappedAccountKeysRow row = after.Single(stored => stored.FactorId == survivor.Id);

            await Assert.That(Base64UrlText.Encode(row.WrappedPrivateKey))
                .IsEqualTo(survivor.WrappedPrivateKey)
                .Because($"factor {survivor.FactorId}'s stored wrapped private key must be what was posted");
            await Assert.That(Base64UrlText.Encode(row.EncapsulatedAccountKeys))
                .IsEqualTo(survivor.EncapsulatedAccountKeys)
                .Because($"factor {survivor.FactorId}'s stored encapsulated keys must be what was posted");

            bool opened = survivor.TryOpen(
                row.WrappedPrivateKey,
                row.EncapsulatedAccountKeys,
                out byte[] contentKey,
                out byte[] indexKey);

            await Assert.That(opened)
                .IsTrue()
                .Because($"surviving factor {survivor.FactorId}'s stored row must open under its own key");
            await Assert.That(Base64UrlText.Encode(contentKey))
                .IsEqualTo(Base64UrlText.Encode(keys.ContentKey))
                .Because($"surviving factor {survivor.FactorId} must hand back the account's content key");
            await Assert.That(Base64UrlText.Encode(indexKey))
                .IsEqualTo(Base64UrlText.Encode(keys.IndexKey))
                .Because($"surviving factor {survivor.FactorId} must hand back the account's index key");

            contentKeyASurvivorHandedBack ??= contentKey;
        }

        // Open only, never byte equality: see the remarks.
        foreach (AccountKeyFixture.Factor code in codeFactors)
        {
            WrappedAccountKeysRow row = after.Single(stored => stored.FactorId == code.Id);

            bool opened = code.TryOpen(
                row.WrappedPrivateKey,
                row.EncapsulatedAccountKeys,
                out byte[] contentKey,
                out byte[] indexKey);

            await Assert.That(opened)
                .IsTrue()
                .Because($"code factor {code.FactorId}'s stored row must open under its own key after the revocation");
            await Assert.That(Base64UrlText.Encode(contentKey))
                .IsEqualTo(Base64UrlText.Encode(keys.ContentKey))
                .Because($"code factor {code.FactorId} must hand back the account's content key");
            await Assert.That(Base64UrlText.Encode(indexKey))
                .IsEqualTo(Base64UrlText.Encode(keys.IndexKey))
                .Because($"code factor {code.FactorId} must hand back the account's index key");
        }

        // Read through the API, because "readable" is a claim about what a browser receives.
        HttpResponseMessage read = await client.GetAsync($"{PayeesPath}/{payeeId:D}");
        await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);

        JsonNode payee = (await JsonNode.ParseAsync(await read.Content.ReadAsStreamAsync()))!;
        string name = payee["name"]!.GetValue<string>();
        await Assert.That(name).IsEqualTo(sealedName);

        // Opened under the id the server served, as a browser would: only in the canonical spelling.
        // A spelling check, never a value check — servedId == payeeId would fire first and hollow the
        // open out. See the remarks.
        string servedIdText = payee["id"]!.GetValue<string>();
        Guid servedId = Guid.Parse(servedIdText);
        await Assert.That(servedIdText)
            .IsEqualTo(servedId.ToString("D"))
            .Because("the served payee id must be spelled lower-case and hyphenated, the only spelling a browser opens under");

        bool readable = AccountKeyFixture.TryOpenNarrative(
            contentKeyASurvivorHandedBack
                ?? throw new InvalidOperationException("No survivor handed back a content key."),
            PayeeTable,
            PayeeNameColumn,
            servedId,
            name,
            out string text);

        await Assert.That(readable)
            .IsTrue()
            .Because("the payee name served after the revocation must open under the content key a survivor recovered");
        await Assert.That(text).IsEqualTo(PayeeName);
    }

    /// <summary>
    /// A second passkey registered over an account's existing keys, and both factors' stored rows open,
    /// each under its own key-encryption key and identifier, to the same content key and index key.
    /// </summary>
    /// <remarks>
    /// <b>What this does not restate:</b> that registering a passkey moves no relation outside the
    /// factor tables and the manifest — <c>FactorManifestPromotionTests</c> owns that census. It
    /// allow-lists <c>wrapped_account_keys</c>, so the first factor's row surviving the second
    /// registration is held here.
    /// </remarks>
    [Test]
    public async Task Registration_OfASecondPasskeyOverTheAccountsExistingKeys_LeavesBothFactorsOpeningTheSamePair()
    {
        // Arrange
        await using PostgresTestHost host = await StartSignedInHostAsync();
        (HttpClient client, Guid userId, _) = await host.Factory.CreateSignedInClientAsync(Subject);

        AccountKeyFixture keys = AccountKeyFixture.Mint();
        SyntheticAuthenticator firstDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        SyntheticAuthenticator secondDevice = SyntheticAuthenticator.CreateEs256(ApiFactory.PasskeyRelyingPartyId);
        AccountKeyFixture.Factor first = keys.MintFactor();
        AccountKeyFixture.Factor second = keys.MintFactor();

        (await RegisterPasskeyAsync(client, firstDevice, first)).EnsureSuccessStatusCode();

        // Act
        HttpResponseMessage response = await RegisterPasskeyAsync(client, secondDevice, second);

        // Assert
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);

        await using NpgsqlConnection admin = new(host.ConnectionString);
        await admin.OpenAsync();
        WrappedAccountKeysRow[] rows = await WrappedAccountKeysAsync(admin, userId);

        await Assert.That(FactorIds(rows))
            .IsEqualTo(FactorIds(first, second))
            .Because("the account must hold exactly the two factors registered here");

        // This test must never gain stored == posted assertions ahead of these opens: within this test
        // the open is the first check to fail — measured: tampering the last byte of
        // EncapsulatedAccountKeys inside WrappedAccountKeys.For, width and version kept, reddens this
        // test at the open. That mutation reddens older tests too (PasskeyCeremonyTests' stored ==
        // posted, AccountRegistrationTests' per-factor opens). A byte check ahead of the open would
        // fail first and demote it to the fixture opening its own output.
        // One factor's blob stored in the other's row fails here too: the HKDF info and the associated
        // data bind the factor id, and each factor has its own pair.
        foreach (AccountKeyFixture.Factor factor in new[] { first, second })
        {
            WrappedAccountKeysRow row = rows.Single(stored => stored.FactorId == factor.Id);

            bool opened = factor.TryOpen(
                row.WrappedPrivateKey,
                row.EncapsulatedAccountKeys,
                out byte[] contentKey,
                out byte[] indexKey);

            await Assert.That(opened)
                .IsTrue()
                .Because($"factor {factor.FactorId}'s stored row must open under its own key and id");
            await Assert.That(Base64UrlText.Encode(contentKey))
                .IsEqualTo(Base64UrlText.Encode(keys.ContentKey))
                .Because($"factor {factor.FactorId} must hand back the account's content key");
            await Assert.That(Base64UrlText.Encode(indexKey))
                .IsEqualTo(Base64UrlText.Encode(keys.IndexKey))
                .Because($"factor {factor.FactorId} must hand back the account's index key");
        }
    }

    /// <summary>One <c>wrapped_account_keys</c> row, in the columns these tests read.</summary>
    private readonly record struct WrappedAccountKeysRow(
        Guid FactorId,
        byte[] WrappedPrivateKey,
        byte[] EncapsulatedAccountKeys);

    /// <summary>
    /// A factor set as one ordered string, so a failure shows ids rather than an element count. TUnit
    /// truncates the received string near 100 characters and windows it at the first difference, so
    /// with several wrong ids only the first is named.
    /// </summary>
    private static string FactorIds(IEnumerable<WrappedAccountKeysRow> rows) =>
        string.Join(", ", rows.Select(row => row.FactorId.ToString("D")).Order(StringComparer.Ordinal));

    /// <inheritdoc cref="FactorIds(IEnumerable{WrappedAccountKeysRow})" />
    private static string FactorIds(params AccountKeyFixture.Factor[] factors) =>
        string.Join(", ", factors.Select(factor => factor.FactorId).Order(StringComparer.Ordinal));

    private static async Task<PostgresTestHost> StartSignedInHostAsync()
    {
        PostgresTestHost host = new(usesApplicationAuthentication: true);
        await host.StartAsync();
        return host;
    }

    // The helpers below are copied from CredentialRevocationTests and RecoveryCodeGenerationTests
    // rather than shared, which is this suite's convention. RegisterPasskeyAsync differs in two ways:
    // it posts an AccountKeyFixture factor rather than a WrappedKeyFixture, and it returns the
    // response so a test can assert on it.

    /// <summary>
    /// Runs both authenticated legs of a passkey registration, carrying <paramref name="factor" />'s
    /// real key material.
    /// </summary>
    private static async Task<HttpResponseMessage> RegisterPasskeyAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        AccountKeyFixture.Factor factor)
    {
        byte[] challenge = await BeginCeremonyAsync(client, RegistrationOptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        int rotationEpoch = await FactorGeneration.NextAsync(client);

        return await client.PostAsJsonAsync(RegistrationPath, new
        {
            clientDataJson = attestation.ClientDataJsonBase64Url,
            attestationObject = attestation.AttestationObjectBase64Url,
            clientExtensionResults = new { prf = new { enabled = true } },
            factorId = factor.FactorId,
            wrappedPrivateKey = factor.WrappedPrivateKey,
            encapsulatedAccountKeys = factor.EncapsulatedAccountKeys,

            // Opaque to the server, which checks framing and generation only.
            manifest = ManifestFixture.Mint().Text,
            rotationEpoch,
        });
    }

    /// <summary>
    /// Runs the whole revocation ceremony: the re-authentication options leg,
    /// <paramref name="device" /> answering the nonce it issued, and the revocation post.
    /// </summary>
    private static async Task<HttpResponseMessage> RevokeAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        Guid credentialId)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);
        int rotationEpoch = await FactorGeneration.NextAsync(client);

        return await client.PostAsJsonAsync($"/api/me/credentials/{credentialId}/revocation", new
        {
            manifest = ManifestFixture.Mint().Text,
            rotationEpoch,
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
    }

    /// <summary>
    /// Runs the whole card issue: the re-authentication options leg, <paramref name="device" />
    /// answering the nonce it issued, and the post carrying every code's verifier and factor.
    /// </summary>
    /// <remarks>
    /// Copied in shape from <c>RecoveryCodeGenerationTests</c>' own generation helpers. The two zero
    /// sign counts here and in <see cref="RevokeAsync" /> are explicit because one device signs both:
    /// <c>PasskeySignatureCounter.Accept</c> refuses a non-zero count that fails to advance and lets a
    /// repeated zero through, so zeros remove an ordering trap between the two ceremonies.
    /// </remarks>
    private static async Task<HttpResponseMessage> IssueRecoveryCodesAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Guid userId,
        IReadOnlyList<AccountKeyFixture.RecoveryCodeFactor> card)
    {
        byte[] challenge = await BeginCeremonyAsync(client, ReauthenticationOptionsPath);
        AssertionResult assertion = device.Authenticate(
            challenge,
            ApiFactory.PasskeyOrigin,
            PasskeyEncoding.ToUserHandle(userId),
            signCount: 0);
        int rotationEpoch = await FactorGeneration.NextAsync(client);

        return await client.PostAsJsonAsync(RecoveryCodesPath, new
        {
            codes = card.Select(code => new
            {
                verifier = code.Verifier,
                factorId = code.Factor.FactorId,
                wrappedPrivateKey = code.Factor.WrappedPrivateKey,
                encapsulatedAccountKeys = code.Factor.EncapsulatedAccountKeys,
            }).ToArray(),
            manifest = ManifestFixture.Mint().Text,
            rotationEpoch,
            credentialId = assertion.CredentialIdBase64Url,
            clientDataJson = assertion.ClientDataJsonBase64Url,
            authenticatorData = assertion.AuthenticatorDataBase64Url,
            signature = assertion.SignatureBase64Url,
            userHandle = assertion.UserHandleBase64Url,
        });
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
    /// Translates a device's WebAuthn handle into the <c>credentials.id</c> the revocation route names.
    /// </summary>
    private static async Task<Guid> ResolveCredentialIdAsync(
        NpgsqlConnection admin,
        SyntheticAuthenticator device)
    {
        await using NpgsqlCommand command = new(
            "select credential_id from passkey_public_keys where webauthn_credential_id = @handle",
            admin);
        command.Parameters.AddWithValue("handle", device.CredentialId);

        return await command.ExecuteScalarAsync() switch
        {
            Guid credentialId => credentialId,
            var unexpected => throw new InvalidOperationException(
                $"No passkey was registered for that handle, got '{unexpected ?? "null"}'."),
        };
    }

    /// <summary>
    /// Every <c>wrapped_account_keys</c> row of one account, on the container superuser connection.
    /// </summary>
    private static async Task<WrappedAccountKeysRow[]> WrappedAccountKeysAsync(
        NpgsqlConnection admin,
        Guid userId)
    {
        await using NpgsqlCommand command = new(
            """
            select factor_id, wrapped_private_key, encapsulated_account_keys
            from wrapped_account_keys
            where user_id = @userId
            order by created_at_utc
            """,
            admin);
        command.Parameters.AddWithValue("userId", userId);

        List<WrappedAccountKeysRow> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new WrappedAccountKeysRow(
                reader.GetGuid(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetFieldValue<byte[]>(2)));
        }

        return [.. rows];
    }
}
