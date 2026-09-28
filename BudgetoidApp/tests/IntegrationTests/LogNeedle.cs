using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace IntegrationTests;

/// <summary>How a stored value was rendered when a log record carried it.</summary>
public enum LogNeedleKind
{
    /// <summary>A text column's value as stored.</summary>
    Text,

    /// <summary>A text value percent-encoded the way <c>Uri.EscapeDataString</c> writes it.</summary>
    UriEscaped,

    /// <summary>
    /// A text value escaped for a JSON string, by the default encoder or the relaxed one a structured
    /// sink may be configured with.
    /// </summary>
    JsonEscaped,

    /// <summary>The SHA-256 of a text value's UTF-8 bytes, as upper-case hex.</summary>
    Sha256HexUpper,

    /// <summary>The SHA-256 of a text value's UTF-8 bytes, as lower-case hex.</summary>
    Sha256HexLower,

    /// <summary>
    /// The SHA-256 of the lower-cased value's UTF-8 bytes, as upper-case hex: what a "pseudonymised"
    /// address usually is.
    /// </summary>
    LowerCasedSha256HexUpper,

    /// <summary>The SHA-256 of the lower-cased value's UTF-8 bytes, as lower-case hex.</summary>
    LowerCasedSha256HexLower,

    /// <summary>The whole value as standard base64, padding dropped.</summary>
    Base64,

    /// <summary>The whole value as unpadded base64url — the alphabet this API's wire uses.</summary>
    Base64Url,

    /// <summary>The whole value as upper-case hex.</summary>
    HexUpper,

    /// <summary>The whole value as lower-case hex.</summary>
    HexLower,

    /// <summary>
    /// The first 32 bytes as hex, either case: what EF renders for a <c>byte[]</c> parameter under
    /// sensitive-data logging before it writes <c>...</c>.
    /// </summary>
    HexFirst32Bytes,

    /// <summary>A 16-byte window of the value as standard base64.</summary>
    Base64Window,

    /// <summary>A 16-byte window of the value as base64url.</summary>
    Base64UrlWindow,

    /// <summary>A 16-byte window of the value as hex, either case.</summary>
    HexWindow,
}

/// <summary>
/// One rendering of one stored value that must not appear in any log record.
/// </summary>
/// <param name="Source">
/// Which column the value came from, as <c>table.column</c>. The report names this and never the value,
/// because printing the value would put it in a log of its own.
/// </param>
/// <param name="Kind">Which rendering this is.</param>
/// <param name="Value">The rendering.</param>
/// <param name="Comparison">How a record is searched for it.</param>
public sealed record LogNeedle(string Source, LogNeedleKind Kind, string Value, StringComparison Comparison)
{
    /// <summary>
    /// The shortest needle the census accepts. Anything shorter matches harmless text by accident, and
    /// an accidental match trains the reader to ignore a red.
    /// </summary>
    public const int MinimumLength = 16;

    /// <summary>
    /// The width EF truncates a logged <c>byte[]</c> parameter to. Measured at this repository's
    /// package versions.
    /// </summary>
    public const int EfLoggedByteLimit = 32;

    /// <summary>The window the partial base64 renderings are cut over.</summary>
    public const int WindowBytes = 16;

    /// <summary>
    /// A text value in every rendering a log record could carry it in: as stored, percent-encoded,
    /// JSON-escaped, and hashed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A hash is a rendering, not a redaction.</b> The values here are low-entropy and known to
    /// whoever holds a guess — an address, a provider subject — so a record carrying their SHA-256 is a
    /// record anybody with the guess can join on. Hashed both as stored and lower-cased, because the
    /// lower-cased form is what code "normalising" an address before hashing it writes.
    /// </para>
    /// <para>
    /// The escaped forms take the column's comparison, since escaping keeps the letters' case; the hex
    /// forms are compared ordinally, one needle per case. A rendering equal to one already listed is
    /// skipped, so a value with nothing to escape yields no second needle matching the same text.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<LogNeedle> ForText(string source, string value, bool ignoresCase)
    {
        ArgumentNullException.ThrowIfNull(value);

        StringComparison comparison = ignoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        List<LogNeedle> needles = [new(source, LogNeedleKind.Text, value, comparison)];

        void AddIfNew(LogNeedleKind kind, string rendering, StringComparison renderingComparison)
        {
            if (!needles.Any(needle => string.Equals(needle.Value, rendering, StringComparison.Ordinal)))
            {
                needles.Add(new(source, kind, rendering, renderingComparison));
            }
        }

        AddIfNew(LogNeedleKind.UriEscaped, Uri.EscapeDataString(value), comparison);
        AddIfNew(LogNeedleKind.JsonEscaped, JsonEncodedText.Encode(value).Value, comparison);
        AddIfNew(
            LogNeedleKind.JsonEscaped,
            JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value,
            comparison);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        AddIfNew(LogNeedleKind.Sha256HexUpper, Convert.ToHexString(hash), StringComparison.Ordinal);
        AddIfNew(LogNeedleKind.Sha256HexLower, Convert.ToHexStringLower(hash), StringComparison.Ordinal);

        byte[] loweredHash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        AddIfNew(LogNeedleKind.LowerCasedSha256HexUpper, Convert.ToHexString(loweredHash), StringComparison.Ordinal);
        AddIfNew(LogNeedleKind.LowerCasedSha256HexLower, Convert.ToHexStringLower(loweredHash), StringComparison.Ordinal);

        return needles;
    }

    /// <summary>
    /// A binary value in every rendering a log record could carry it in: whole, truncated the way EF
    /// truncates it, and in 16-byte windows, base64 and hex, for a record that carried only part of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A window's base64 is cut to the characters its sixteen bytes fully determine — 21 of the 22 —
    /// because the last character of an unpadded encoding also carries bits of whatever byte follows,
    /// so it is not a substring of the longer rendering the window was taken from.
    /// </para>
    /// <para>
    /// A window's hex needs no such cut, since every byte is two characters of its own; one needle
    /// per offset, compared ignoring case, covers both cases.
    /// </para>
    /// <para>
    /// Base64 is compared ordinally. The whole value's hex is one needle per case; the truncated and
    /// windowed hex are compared ignoring case.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<LogNeedle> ForBytes(string source, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);

        List<LogNeedle> needles =
        [
            new(source, LogNeedleKind.Base64, Convert.ToBase64String(value).TrimEnd('='), StringComparison.Ordinal),
            new(source, LogNeedleKind.Base64Url, Base64Url.EncodeToString(value), StringComparison.Ordinal),
            new(source, LogNeedleKind.HexUpper, Convert.ToHexString(value), StringComparison.Ordinal),
            new(source, LogNeedleKind.HexLower, Convert.ToHexStringLower(value), StringComparison.Ordinal),
        ];

        if (value.Length > EfLoggedByteLimit)
        {
            needles.Add(new(
                source,
                LogNeedleKind.HexFirst32Bytes,
                Convert.ToHexString(value, 0, EfLoggedByteLimit),
                StringComparison.OrdinalIgnoreCase));
        }

        // Every offset, not only multiples of three: a record that re-encoded a slice starting anywhere
        // is still a record carrying the value.
        const int determinedCharacters = WindowBytes * 8 / 6;
        for (int offset = 0; offset + WindowBytes <= value.Length; offset++)
        {
            ReadOnlySpan<byte> window = value.AsSpan(offset, WindowBytes);
            needles.Add(new(
                source,
                LogNeedleKind.Base64Window,
                Convert.ToBase64String(window)[..determinedCharacters],
                StringComparison.Ordinal));
            needles.Add(new(
                source,
                LogNeedleKind.Base64UrlWindow,
                Base64Url.EncodeToString(window)[..determinedCharacters],
                StringComparison.Ordinal));
            needles.Add(new(
                source,
                LogNeedleKind.HexWindow,
                Convert.ToHexString(window),
                StringComparison.OrdinalIgnoreCase));
        }

        return needles;
    }
}

/// <summary>One needle found in one record, named without the value it matched.</summary>
public sealed record LogOffence(string Source, LogNeedleKind Kind, string Origin)
{
    public override string ToString() => $"{Source} as {Kind} in [{Origin}]";
}

/// <summary>Searches captured records for needles.</summary>
public static class LogCensus
{
    /// <summary>
    /// Every (column, rendering, record origin) where a record's text contains a needle, once each.
    /// </summary>
    public static IReadOnlyList<LogOffence> Search(
        IReadOnlyList<CapturedLogRecord> records,
        IReadOnlyList<LogNeedle> needles)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(needles);

        HashSet<LogOffence> offences = [];
        foreach (CapturedLogRecord record in records)
        {
            foreach (LogNeedle needle in needles)
            {
                if (record.Text.Contains(needle.Value, needle.Comparison))
                {
                    offences.Add(new LogOffence(needle.Source, needle.Kind, record.Origin));
                }
            }
        }

        return [.. offences.OrderBy(offence => offence.ToString(), StringComparer.Ordinal)];
    }
}
