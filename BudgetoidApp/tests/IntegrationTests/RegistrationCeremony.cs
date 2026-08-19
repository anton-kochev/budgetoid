using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Application.Registration;
using TestSupport;

namespace IntegrationTests;

/// <summary>
/// One run of the account-registration ceremony, as a browser drives it: the options leg, a synthetic
/// authenticator answering the nonce that leg issued, and the finish leg carrying the passkey's
/// envelopes and a full card of recovery codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared, unlike every other ceremony helper in this folder, and the difference is which question
/// the caller is asking.</b> The private copies in <see cref="CredentialListEndpointTests" /> and its
/// neighbours exist because each of those files is <em>about</em> the route it drives, and a shared
/// helper would become a second place a route's contract is described. Nothing here is about
/// registration: <see cref="ApiFactory.RegisterAccountAsync" /> drives it to <b>obtain an account and a
/// session</b> for a test whose subject is somewhere else entirely, and
/// <see cref="AccountRegistrationTests" /> — which is about the route — reaches it through forwarders so
/// that the file that owns the claims still reads as if it owned the driver too. A third copy of the
/// ceremony was the alternative, and the copies would disagree the first time the wire changed.
/// </para>
/// <para>
/// <b>Nothing here asserts on a status.</b> Several tests next door read the finish leg's response as
/// their subject, and a helper that threw on a refusal would turn a red about the route into a red about
/// the fixture. <see cref="EnsureOkAsync" /> is applied to the <em>arrangement</em> requests only — the
/// options leg, whose failure makes every later line a claim about a ceremony that never started.
/// </para>
/// </remarks>
internal static class RegistrationCeremony
{
    public const string OptionsPath = "/api/registration/options";

    public const string RegistrationPath = "/api/registration";

    /// <summary>
    /// The member the card travels on, as the wire spells it.
    /// </summary>
    /// <remarks>
    /// <b>The name is a rule rather than a preference.</b> <see cref="UnwrappedKeyMaterialVocabulary" />
    /// refuses the token <c>recovery_code</c> unless <c>hash</c> follows it, deliberately and on this one
    /// word, so a request record declaring <c>RecoveryCodes</c> is reported by
    /// <c>KeyMaterialSecrecyTests</c>. The already-shipped sibling route spells the identical payload
    /// <c>codes</c> for exactly that reason, and two write paths accepting one set of recovery codes have
    /// no business disagreeing about what the member is called. The other way out — a
    /// <c>JsonPropertyName</c> attribute over a differently named property — is the evasion that
    /// vocabulary's own remarks warn about.
    /// </remarks>
    public const string CodesMember = "codes";

    /// <summary>How many codes a card holds. Restated rather than read off the handler.</summary>
    public const int RequiredCodeCount = 10;

    /// <summary>The exact width of a verifier, decoded — <c>RecoveryCodeHash.VerifierLength</c>.</summary>
    public const int VerifierLength = 32;

    /// <summary>
    /// Runs the whole ceremony on one client: the options leg, the device answering the nonce it issued,
    /// and the finish leg carrying the passkey's envelopes and a full card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <see cref="WrappedKeyFixture" /> per factor, minted per call: <c>factor_id</c> is
    /// <c>PK_wrapped_account_keys</c> and therefore unique table-wide rather than per account, and a
    /// shared value would make a second registration anywhere in one database a <c>23505</c>.
    /// </para>
    /// <para>
    /// <paramref name="passkeyKeys" /> is supplied by exactly one caller, the conflict test that claims a
    /// factor identifier already standing in the table. It is a parameter rather than a second helper
    /// because that test needs every other member of the request to be the genuine article: a fixture of
    /// its own would be a second definition of "a correct registration" able to drift from this one, and
    /// the drift would read as the conflict under test.
    /// </para>
    /// </remarks>
    public static async Task<RegistrationCeremonyResult> RegisterAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        WrappedKeyFixture? passkeyKeys = null)
    {
        ArgumentNullException.ThrowIfNull(device);

        IssuedRegistrationOptions options = await BeginAsync(client);
        WrappedKeyFixture keys = passkeyKeys ?? WrappedKeyFixture.Mint();
        IReadOnlyList<RegistrationCodeSubmission> card = CardOf(Verifiers());

        AttestationResult attestation = device.Register(
            options.Challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true,

            // THE HANDLE COMES OFF THE OPTIONS RESPONSE AND OFF NOTHING ELSE, which is what a browser
            // hands its authenticator. Deriving it here instead would make every device driven through
            // this helper agree with the finish leg's own derivation whatever the options leg said — see
            // Registration_ThenAssertion_PresentsTheHandleTheDeviceWasGiven, which is the test that
            // reads it back.
            userHandle: options.UserHandle);

        return new RegistrationCeremonyResult(
            await PostAsync(client, attestation, keys, card),
            options.Challenge,
            RegistrationAccountId.For(options.Challenge),
            keys,
            card);
    }

    /// <summary>
    /// Posts a finish leg answering <paramref name="challenge" />, which the caller obtained from
    /// somewhere other than this route's own options leg.
    /// </summary>
    /// <remarks>
    /// Everything else about the request is the genuine article — a real device, real envelopes, a full
    /// card — so the pool the nonce came from is the only thing that can refuse it.
    /// </remarks>
    public static Task<HttpResponseMessage> RegisterOverAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        byte[] challenge)
    {
        ArgumentNullException.ThrowIfNull(device);

        return PostAsync(
            client,
            device.Register(challenge, ApiFactory.PasskeyOrigin, signCount: 0, prfEnabled: true),
            WrappedKeyFixture.Mint(),
            CardOf(Verifiers()));
    }

    /// <summary>
    /// Runs the account-registration options leg and returns both binary members it issued.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="BeginCeremonyAsync" />, which serves four other legs whose responses
    /// carry no <c>user</c> member at all — an assertion-options response has no account to name.
    /// </remarks>
    public static async Task<IssuedRegistrationOptions> BeginAsync(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        HttpResponseMessage response = await client.PostAsync(OptionsPath, content: null);
        await EnsureOkAsync(response);

        JsonObject body = await ReadJsonObjectAsync(response);

        return new IssuedRegistrationOptions(
            Base64UrlText.Decode(body["challenge"]!.GetValue<string>()),
            Base64UrlText.Decode(body["user"]!["id"]!.GetValue<string>()));
    }

    /// <summary>Posts the finish leg, exactly as a browser would send it.</summary>
    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        AttestationResult attestation,
        WrappedKeyFixture passkeyKeys,
        IReadOnlyList<RegistrationCodeSubmission> card) =>
        client.PostAsJsonAsync(RegistrationPath, BodyOf(attestation, passkeyKeys, card));

    /// <summary>
    /// The finish leg's body as a mutable map, so a test can take one member out or put a different
    /// shape in without restating the six that are not its subject.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dictionary rather than an anonymous type, and only because a member has to be <b>removable</b>.
    /// Two of the refusals next door are about an <em>absent</em> member — no <c>clientExtensionResults</c>
    /// object at all, no <c>codes</c> member at all — and an absent member is a different claim from one
    /// present and null: the first is a client that never asked, the second is a client that asked and
    /// got nothing. An anonymous type can express the second and not the first.
    /// </para>
    /// <para>
    /// <b>Everything a test does not name is the genuine article</b>, which is what keeps each refusal on
    /// its own subject: a malformed envelope or a repeated factor introduced by the fixture would be
    /// refused before or instead of the rule the test was written for, and the test would go on passing
    /// while saying nothing.
    /// </para>
    /// </remarks>
    public static Dictionary<string, object?> BodyOf(
        AttestationResult attestation,
        WrappedKeyFixture passkeyKeys,
        IReadOnlyList<RegistrationCodeSubmission> card)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        ArgumentNullException.ThrowIfNull(passkeyKeys);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["clientDataJson"] = attestation.ClientDataJsonBase64Url,
            ["attestationObject"] = attestation.AttestationObjectBase64Url,
            ["clientExtensionResults"] = new { prf = new { enabled = true } },
            ["factorId"] = passkeyKeys.FactorId,
            ["wrappedContentKey"] = passkeyKeys.WrappedContentKey,
            ["wrappedIndexKey"] = passkeyKeys.WrappedIndexKey,
            [CodesMember] = SubmissionsOf(card),
        };
    }

    /// <summary>
    /// Runs a genuine ceremony and posts a finish leg that <paramref name="amend" /> has changed in one
    /// respect.
    /// </summary>
    /// <remarks>
    /// The options leg is real and the device answers the nonce it issued, so the challenge is live and
    /// spent by this request — which is what puts the refusal at the rung the caller meant rather than at
    /// rung 4. The amendment is the only thing that differs from a registration that would have succeeded.
    /// </remarks>
    public static async Task<HttpResponseMessage> PostAmendedAsync(
        HttpClient client,
        SyntheticAuthenticator device,
        Action<Dictionary<string, object?>> amend)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(amend);

        byte[] challenge = await BeginCeremonyAsync(client, OptionsPath);
        AttestationResult attestation = device.Register(
            challenge,
            ApiFactory.PasskeyOrigin,
            signCount: 0,
            prfEnabled: true);
        Dictionary<string, object?> body = BodyOf(attestation, WrappedKeyFixture.Mint(), CardOf(Verifiers()));
        amend(body);

        return await client.PostAsJsonAsync(RegistrationPath, body);
    }

    /// <summary>
    /// One whole submission per code: the verifier, a factor of its own, and the envelope pair sealed
    /// under the key that code derives.
    /// </summary>
    /// <remarks>
    /// Ten submissions and never ten verifiers beside one factor and one pair — the shape is the rule,
    /// and it is what makes the eleven <c>wrapped_account_keys</c> rows the count they are. A fresh
    /// factor per code, minted per call for the reason <see cref="WrappedKeyFixture" /> gives about its
    /// own.
    /// </remarks>
    public static IReadOnlyList<RegistrationCodeSubmission> CardOf(IReadOnlyList<string> verifiers)
    {
        ArgumentNullException.ThrowIfNull(verifiers);

        return [.. verifiers.Select(verifier =>
            new RegistrationCodeSubmission(verifier, WrappedKeyFixture.Mint()))];
    }

    /// <summary>The card as the wire carries it, one object per code.</summary>
    public static object[] SubmissionsOf(IReadOnlyList<RegistrationCodeSubmission> card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return [.. card.Select(code => code.Wire)];
    }

    /// <summary>
    /// <paramref name="count" /> distinct verifiers of <see cref="VerifierLength" /> bytes each,
    /// base64url encoded exactly as a browser would send them.
    /// </summary>
    public static string[] Verifiers(int count = RequiredCodeCount) =>
    [
        .. Enumerable.Range(0, count)
            .Select(_ => Base64UrlText.Encode(RandomNumberGenerator.GetBytes(VerifierLength))),
    ];

    /// <summary>Runs an options leg and returns the challenge bytes it issued.</summary>
    public static async Task<byte[]> BeginCeremonyAsync(HttpClient client, string path)
    {
        ArgumentNullException.ThrowIfNull(client);

        HttpResponseMessage response = await client.PostAsync(path, content: null);
        await EnsureOkAsync(response);

        return Base64UrlText.Decode((await ReadJsonObjectAsync(response))["challenge"]!.GetValue<string>());
    }

    /// <summary>
    /// Fails naming the status and the body when an arrangement's own request did not succeed.
    /// </summary>
    /// <remarks>
    /// <c>EnsureSuccessStatusCode</c> throws without the body, and every refusal on these routes says
    /// which of several 401s answered in exactly that body.
    /// </remarks>
    public static async Task<HttpResponseMessage> EnsureOkAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.IsSuccessStatusCode
            ? response
            : throw new InvalidOperationException(
                $"An arrangement request to '{response.RequestMessage?.RequestUri}' failed with "
                + $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    public static async Task<JsonObject> ReadJsonObjectAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();
    }

    /// <summary>
    /// The value of the session cookie this response sets, or a sentence saying what arrived instead.
    /// </summary>
    /// <remarks>
    /// Throwing rather than returning null, so a missing header fails at the line that wanted it and
    /// says which headers the response really carried.
    /// </remarks>
    public static string SessionCookieValueOf(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        string[] headers = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? [.. values]
            : [];
        string? issued = headers.FirstOrDefault(header =>
            header.StartsWith($"{SessionCookieAuthenticationTests.CookieName}=", StringComparison.Ordinal));

        if (issued is null)
        {
            throw new InvalidOperationException(
                $"The response set no '{SessionCookieAuthenticationTests.CookieName}' cookie. Set-Cookie: "
                + (headers.Length == 0 ? "<none>" : string.Join(" | ", headers)));
        }

        string value = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(issued).Value.ToString();

        return value.Length > 0
            ? value
            : throw new InvalidOperationException(
                $"The '{SessionCookieAuthenticationTests.CookieName}' cookie was set to an empty value.");
    }
}

/// <summary>
/// What one options leg issued: the nonce, and the user handle it told the authenticator to keep.
/// </summary>
/// <remarks>
/// The two travel together because the whole claim about them is that they agree. Read as raw bytes
/// rather than as a <see cref="Guid" />: the handle is sixteen opaque bytes on the wire, and a test
/// that parsed them into a uuid first would quietly accept a response whose bytes are in the wrong
/// order — the exact failure <c>PasskeyEncoding.ToUserHandle</c>'s big-endian argument is about.
/// </remarks>
internal sealed record IssuedRegistrationOptions(byte[] Challenge, byte[] UserHandle);

/// <summary>
/// One code of a card: the verifier the client derived, and the factor and envelope pair that code
/// alone holds.
/// </summary>
/// <remarks>
/// The three key-custody members belong to the <b>code</b> because the key-encryption key that sealed
/// the envelopes was derived from that code. Carrying them together is what makes pairing one code's
/// verifier with another's envelopes unrepresentable in this fixture — a swap that satisfies every
/// constraint the database holds and is discovered by somebody who redeemed a code, was handed a
/// session, and found the account still locked.
/// </remarks>
internal sealed record RegistrationCodeSubmission(string Verifier, WrappedKeyFixture Keys)
{
    /// <summary>The submission as the wire carries it.</summary>
    public object Wire => new
    {
        verifier = Verifier,
        factorId = Keys.FactorId,
        wrappedContentKey = Keys.WrappedContentKey,
        wrappedIndexKey = Keys.WrappedIndexKey,
    };
}

/// <summary>
/// What one run of the ceremony produced, in the values the assertions key on.
/// </summary>
/// <param name="Response">The finish leg's response, unread.</param>
/// <param name="Challenge">The bytes the options leg minted.</param>
/// <param name="AccountId">
/// The identifier <see cref="RegistrationAccountId.For" /> derives from
/// <paramref name="Challenge" /> — computed here rather than read out of <c>users</c>, so an
/// assertion using it discriminates against an identifier the finish leg chose for itself.
/// </param>
/// <param name="PasskeyKeys">The passkey factor's share of the account keys.</param>
/// <param name="Card">The ten codes, each with the factor and the envelope pair that code holds.</param>
/// <remarks>
/// Named for the ceremony rather than for the account, because <c>Application.Registration</c> already
/// declares a <c>RegisteredAccount</c> of its own and two types of one name in one file's view is a
/// resolution a reader has to work out. <see cref="AccountRegistrationTests" /> aliases it back to the
/// shorter name it has always used.
/// </remarks>
internal sealed record RegistrationCeremonyResult(
    HttpResponseMessage Response,
    byte[] Challenge,
    Guid AccountId,
    WrappedKeyFixture PasskeyKeys,
    IReadOnlyList<RegistrationCodeSubmission> Card)
{
    /// <summary>The verifiers alone, for the redemption that has to present one.</summary>
    public IReadOnlyList<string> Verifiers => [.. Card.Select(code => code.Verifier)];
}
