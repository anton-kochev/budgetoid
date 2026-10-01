using System.Buffers.Text;

namespace TestSupport;

/// <summary>
/// The unpadded base64url a WebAuthn request DTO carries its byte members as.
/// </summary>
public static class Base64UrlText
{
    /// <summary>Encodes bytes the way a browser hands them to the API.</summary>
    public static string Encode(ReadOnlySpan<byte> value) => Base64Url.EncodeToString(value);

    /// <summary>Decodes a base64url string, which is how every golden vector enters a test.</summary>
    public static byte[] Decode(string value) => Base64Url.DecodeFromChars(value);
}
