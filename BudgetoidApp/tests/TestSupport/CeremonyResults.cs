namespace TestSupport;

/// <summary>
/// What a registration ceremony produced, in both the raw and the transport form.
/// </summary>
/// <remarks>
/// Both forms are exposed because tests need both: the verifier takes bytes, and a request DTO test
/// takes the base64url strings a browser would actually send.
/// </remarks>
public sealed record AttestationResult
{
    public required byte[] ClientDataJson { get; init; }

    public required byte[] AttestationObject { get; init; }

    public required byte[] CredentialId { get; init; }

    /// <summary>
    /// The PRF extension result the client reports, or null when the ceremony asked for none. Nothing
    /// in the authenticator data carries it — it arrives in the client extension results.
    /// </summary>
    public required bool? PrfEnabled { get; init; }

    public string ClientDataJsonBase64Url => Base64UrlText.Encode(ClientDataJson);

    public string AttestationObjectBase64Url => Base64UrlText.Encode(AttestationObject);

    public string CredentialIdBase64Url => Base64UrlText.Encode(CredentialId);
}

/// <summary>
/// What an authentication ceremony produced, in both the raw and the transport form.
/// </summary>
public sealed record AssertionResult
{
    public required byte[] ClientDataJson { get; init; }

    public required byte[] AuthenticatorData { get; init; }

    public required byte[] Signature { get; init; }

    public required byte[] CredentialId { get; init; }

    /// <summary>The user handle a discoverable credential returns, when one was configured.</summary>
    public required byte[]? UserHandle { get; init; }

    public string ClientDataJsonBase64Url => Base64UrlText.Encode(ClientDataJson);

    public string AuthenticatorDataBase64Url => Base64UrlText.Encode(AuthenticatorData);

    public string SignatureBase64Url => Base64UrlText.Encode(Signature);

    public string CredentialIdBase64Url => Base64UrlText.Encode(CredentialId);

    public string? UserHandleBase64Url => UserHandle is null ? null : Base64UrlText.Encode(UserHandle);
}
