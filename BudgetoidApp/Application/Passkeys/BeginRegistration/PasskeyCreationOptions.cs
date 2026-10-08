namespace Application.Passkeys.BeginRegistration;

/// <summary>
/// The <c>publicKey</c> member of a WebAuthn <c>navigator.credentials.create()</c> call, in the shape
/// the browser expects, with every binary member already base64url text.
/// </summary>
/// <remarks>
/// Named for the protocol rather than for the product, because these members are the protocol's: a
/// reader comparing this against the WebAuthn specification should not have to translate first.
/// </remarks>
public sealed record PasskeyCreationOptions
{
    public required string Challenge { get; init; }

    public required PasskeyRelyingParty Rp { get; init; }

    public required PasskeyUser User { get; init; }

    public required IReadOnlyList<PasskeyCredentialParameter> PubKeyCredParams { get; init; }

    /// <summary>Milliseconds, which is the unit WebAuthn's own <c>timeout</c> is in.</summary>
    public required long Timeout { get; init; }

    public required string Attestation { get; init; }

    public required PasskeyAuthenticatorSelection AuthenticatorSelection { get; init; }

    /// <summary>
    /// The handles already registered to this account, so an authenticator that holds one of them
    /// declines rather than enrolling the same key a second time.
    /// </summary>
    public required IReadOnlyList<PasskeyCredentialDescriptor> ExcludeCredentials { get; init; }

    public required PasskeyRegistrationExtensions Extensions { get; init; }
}

public sealed record PasskeyRelyingParty(string Id, string Name);

/// <summary>
/// The account the credential is being registered to, as WebAuthn names it.
/// </summary>
/// <param name="Id">
/// The internal user id as 16 raw bytes, base64url. Not a random handle of its own: the id is already
/// an identifier no external party supplies, and a second one would be a column nothing else reads.
/// </param>
/// <param name="Name">The account's own identifier, which is what an authenticator lists it under.</param>
/// <param name="DisplayName">
/// The same value. The product asks for no separate display name, and inventing one would be
/// collecting a field nobody entered.
/// </param>
public sealed record PasskeyUser(string Id, string Name, string DisplayName);

public sealed record PasskeyCredentialParameter(string Type, int Alg);

public sealed record PasskeyCredentialDescriptor(string Type, string Id);

public sealed record PasskeyAuthenticatorSelection(
    string ResidentKey,
    bool RequireResidentKey,
    string UserVerification);

/// <summary>
/// The client extensions the ceremony asks for.
/// </summary>
/// <remarks>
/// <c>prf</c> is requested with no evaluation input, which asks the authenticator whether it can
/// derive from this credential without asking it to derive anything yet. Serializes as
/// <c>{"prf":{}}</c>.
/// </remarks>
public sealed record PasskeyRegistrationExtensions
{
    public PasskeyPrfRequest Prf { get; init; } = new();
}

/// <summary>An empty object, which is the whole request: "can this credential do PRF?"</summary>
public sealed record PasskeyPrfRequest;
