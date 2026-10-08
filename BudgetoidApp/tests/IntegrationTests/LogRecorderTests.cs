using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests;

/// <summary>
/// The controls under <see cref="LogRecorder" /> and <see cref="LogCensus" />: a value planted in each
/// place a log record can carry it is found there, and a value nobody logged is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every control logs through the real host's <see cref="ILoggerFactory" /></b>, not through a
/// recorder constructed on its own. What <c>LogRedactionTests</c> relies on is that the recorder sees
/// what the application's pipeline hands a provider — its filters, its scope provider, its levels —
/// and a recorder exercised outside that pipeline proves nothing about it.
/// </para>
/// <para>
/// Each case also searches for a <b>neighbour</b> that was never logged, and demands it is absent. A
/// search that answered "found" to everything would pass every positive control in this file.
/// </para>
/// </remarks>
public sealed class LogRecorderTests
{
    [Test]
    public async Task Search_ForAValueInTemplateArguments_FindsIt()
    {
        // Arrange
        string planted = Planted();
        string neighbour = Planted();

        // Act
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
            loggers.CreateLogger("LogRecorderControl.Template")
                .LogInformation("A control carrying {Value}", planted));

        // Assert
        await Assert.That(OffencesFor(records, planted)).IsNotEmpty();
        await Assert.That(OffencesFor(records, neighbour)).IsEmpty();
    }

    [Test]
    public async Task Search_ForAValueInADictionaryScopeKeyTheTemplateDoesNotName_FindsIt()
    {
        // Arrange
        string planted = Planted();
        string neighbour = Planted();

        // Act — the message names nothing; the value rides only on the scope.
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
        {
            ILogger logger = loggers.CreateLogger("LogRecorderControl.Scope");
            using (logger.BeginScope(new Dictionary<string, object?> { ["NotInTheTemplate"] = planted }))
            {
                logger.LogInformation("A control with nothing in its message");
            }
        });

        // Assert
        await Assert.That(OffencesFor(records, planted)).IsNotEmpty();
        await Assert.That(OffencesFor(records, neighbour)).IsEmpty();
    }

    [Test]
    public async Task Search_ForAValueInAnInnerExceptionMessage_FindsIt()
    {
        // Arrange — two levels down, and the second one inside an aggregate, so a recorder that
        // rendered only the outer exception's message misses it.
        string planted = Planted();
        string neighbour = Planted();
        InvalidOperationException logged = new(
            "outer",
            new AggregateException("aggregate", new ArgumentException(planted)));

        // Act
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
            loggers.CreateLogger("LogRecorderControl.InnerException")
                .LogError(logged, "A control failure"));

        // Assert
        await Assert.That(OffencesFor(records, planted)).IsNotEmpty();
        await Assert.That(OffencesFor(records, neighbour)).IsEmpty();
    }

    [Test]
    public async Task Search_ForAValueInExceptionData_FindsItAsItStoodWhenLogged()
    {
        // Arrange — on an inner exception, since Exception.ToString renders no Data at all.
        string planted = Planted();
        string neighbour = Planted();
        InvalidOperationException inner = new("inner");
        inner.Data["planted"] = planted;
        InvalidOperationException logged = new("outer", inner);

        // Act — and overwrite it straight afterwards, which a recorder that kept the live exception
        // and rendered it at snapshot time would report instead.
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
        {
            loggers.CreateLogger("LogRecorderControl.ExceptionData").LogError(logged, "A control failure");
            inner.Data["planted"] = "overwritten after the record was written";
        });

        // Assert
        await Assert.That(OffencesFor(records, planted)).IsNotEmpty();
        await Assert.That(OffencesFor(records, neighbour)).IsEmpty();
    }

    [Test]
    public async Task Search_ForAValueInATraceRecordFromAMicrosoftCategory_FindsIt()
    {
        // Arrange — a category the framework itself writes under, at the level no default enables.
        string planted = Planted();
        string neighbour = Planted();

        // Act
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
            loggers.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
                .LogTrace("A control carrying {Value}", planted));

        // Assert
        await Assert.That(OffencesFor(records, planted)).IsNotEmpty();
        await Assert.That(OffencesFor(records, neighbour)).IsEmpty();
    }

    [Test]
    public async Task Search_ForBytesRenderedEachWay_FindsEachUnderItsOwnKind()
    {
        // Arrange — 48 bytes, so the EF truncation (32) is shorter than the value and a record
        // carrying only the prefix is a different fact from one carrying the whole.
        byte[] value = RandomNumberGenerator.GetBytes(48);
        byte[] neighbour = RandomNumberGenerator.GetBytes(48);
        (string Category, string Rendering, LogNeedleKind Expected)[] renderings =
        [
            ("LogRecorderControl.Base64", Convert.ToBase64String(value), LogNeedleKind.Base64),
            ("LogRecorderControl.Base64Url", Base64Url.EncodeToString(value), LogNeedleKind.Base64Url),
            ("LogRecorderControl.HexUpper", Convert.ToHexString(value), LogNeedleKind.HexUpper),
            ("LogRecorderControl.HexLower", Convert.ToHexStringLower(value), LogNeedleKind.HexLower),
            ("LogRecorderControl.EfTruncated",
                $"0x{Convert.ToHexString(value, 0, LogNeedle.EfLoggedByteLimit)}...",
                LogNeedleKind.HexFirst32Bytes),
            ("LogRecorderControl.Base64Slice",
                Convert.ToBase64String(value.AsSpan(5, 30)),
                LogNeedleKind.Base64Window),
            ("LogRecorderControl.Base64UrlSlice",
                Base64Url.EncodeToString(value.AsSpan(7, 25)),
                LogNeedleKind.Base64UrlWindow),

            // An odd offset, and neither the whole value nor the first 32 bytes.
            ("LogRecorderControl.HexSlice",
                Convert.ToHexStringLower(value.AsSpan(5, 30)),
                LogNeedleKind.HexWindow),
        ];

        // Act
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
        {
            foreach ((string category, string rendering, _) in renderings)
            {
                loggers.CreateLogger(category).LogInformation("A control carrying {Value}", rendering);
            }
        });
        IReadOnlyList<LogOffence> found = LogCensus.Search(records, LogNeedle.ForBytes("control.bytes", value));
        IReadOnlyList<LogOffence> neighbourFound =
            LogCensus.Search(records, LogNeedle.ForBytes("control.neighbour", neighbour));

        // Assert — each record is found under the kind that describes it.
        string[] missing =
        [
            .. renderings
                .Where(rendering => !found.Any(offence =>
                    offence.Kind == rendering.Expected
                    && offence.Origin.StartsWith($"{rendering.Category} ", StringComparison.Ordinal)))
                .Select(rendering => $"{rendering.Category} not found as {rendering.Expected}"),
        ];
        await Assert.That(missing).IsEmpty();
        await Assert.That(neighbourFound).IsEmpty();
    }

    [Test]
    public async Task Search_ForTextRenderedEachWay_FindsEachUnderItsOwnKind()
    {
        // Arrange — a value every rendering changes: upper case for the lower-cased hash, '@' for the
        // percent-encoding, '+' for the default JSON encoder and '"' for the relaxed one. So each record
        // below carries one rendering and not the value itself.
        string value = $"Planted+\"Quoted\"@Mixed-Case-{Guid.NewGuid():N}";
        string neighbour = $"Planted+\"Quoted\"@Mixed-Case-{Guid.NewGuid():N}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        byte[] loweredHash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        (string Category, string Rendering, LogNeedleKind Expected)[] renderings =
        [
            ("LogRecorderControl.UriEscaped", Uri.EscapeDataString(value), LogNeedleKind.UriEscaped),
            ("LogRecorderControl.JsonEscaped", JsonEncodedText.Encode(value).Value, LogNeedleKind.JsonEscaped),
            ("LogRecorderControl.JsonEscapedRelaxed",
                JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value,
                LogNeedleKind.JsonEscaped),
            ("LogRecorderControl.Sha256HexUpper", Convert.ToHexString(hash), LogNeedleKind.Sha256HexUpper),
            ("LogRecorderControl.Sha256HexLower", Convert.ToHexStringLower(hash), LogNeedleKind.Sha256HexLower),
            ("LogRecorderControl.LowerCasedSha256HexUpper",
                Convert.ToHexString(loweredHash),
                LogNeedleKind.LowerCasedSha256HexUpper),
            ("LogRecorderControl.LowerCasedSha256HexLower",
                Convert.ToHexStringLower(loweredHash),
                LogNeedleKind.LowerCasedSha256HexLower),
        ];

        // Act
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
        {
            foreach ((string category, string rendering, _) in renderings)
            {
                loggers.CreateLogger(category).LogInformation("A control carrying {Value}", rendering);
            }
        });
        IReadOnlyList<LogOffence> found =
            LogCensus.Search(records, LogNeedle.ForText("control.text", value, ignoresCase: false));
        IReadOnlyList<LogOffence> neighbourFound =
            LogCensus.Search(records, LogNeedle.ForText("control.neighbour", neighbour, ignoresCase: false));

        // Assert — each record is found under the kind that describes it, and the value as stored is in
        // none of them, so no record is found merely because the raw text rode along.
        string[] missing =
        [
            .. renderings
                .Where(rendering => !found.Any(offence =>
                    offence.Kind == rendering.Expected
                    && offence.Origin.StartsWith($"{rendering.Category} ", StringComparison.Ordinal)))
                .Select(rendering => $"{rendering.Category} not found as {rendering.Expected}"),
        ];
        await Assert.That(missing).IsEmpty();
        await Assert.That(found.Where(offence => offence.Kind == LogNeedleKind.Text)).IsEmpty();
        await Assert.That(neighbourFound).IsEmpty();
    }

    [Test]
    public async Task Search_ForBytesPassedRawAsATemplateArgument_FindsThemAsAStructuredSinkWouldRenderThem()
    {
        // Arrange — the array itself, not a rendering of it. The formatted message says
        // "System.Byte[]"; a JSON sink writes the structured value as base64.
        byte[] value = RandomNumberGenerator.GetBytes(48);
        byte[] neighbour = RandomNumberGenerator.GetBytes(48);

        // Act
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
            loggers.CreateLogger("LogRecorderControl.RawBytes").LogInformation("A control carrying {Value}", value));
        LogNeedleKind[] found =
        [
            .. LogCensus.Search(records, LogNeedle.ForBytes("control.bytes", value)).Select(offence => offence.Kind),
        ];

        // Assert
        await Assert.That(found).Contains(LogNeedleKind.Base64);
        await Assert.That(LogCensus.Search(records, LogNeedle.ForBytes("control.neighbour", neighbour))).IsEmpty();
    }

    /// <summary>A byte container other than an array that a call site can hand a logger.</summary>
    public enum ByteCarrier
    {
        ArraySegment,
        ImmutableArray,
        List,
        ReadOnlyList,
    }

    [Test]
    [Arguments(ByteCarrier.ArraySegment)]
    [Arguments(ByteCarrier.ImmutableArray)]
    [Arguments(ByteCarrier.List)]
    [Arguments(ByteCarrier.ReadOnlyList)]
    public async Task Search_ForBytesPassedRawInAnotherContainer_FindsThemAsForAnArray(ByteCarrier carrier)
    {
        // Arrange — the same bytes in a container a sink still writes as base64. A recorder that knew
        // only byte[] would render these element by element, as numbers, and find nothing.
        byte[] value = RandomNumberGenerator.GetBytes(48);
        byte[] neighbour = RandomNumberGenerator.GetBytes(48);
        object carried = carrier switch
        {
            ByteCarrier.ArraySegment => new ArraySegment<byte>(value),
            ByteCarrier.ImmutableArray => ImmutableArray.Create(value),
            ByteCarrier.List => new List<byte>(value),

            // A read-only wrapper, so the runtime type is not an array the byte[] arm would take.
            ByteCarrier.ReadOnlyList => (IReadOnlyList<byte>)Array.AsReadOnly(value),
            _ => throw new ArgumentOutOfRangeException(nameof(carrier)),
        };

        // Act
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
            loggers.CreateLogger("LogRecorderControl.RawContainer").LogInformation("A control carrying {Value}", carried));
        LogNeedleKind[] found =
        [
            .. LogCensus.Search(records, LogNeedle.ForBytes("control.bytes", value)).Select(offence => offence.Kind),
        ];

        // Assert
        await Assert.That(found).Contains(LogNeedleKind.Base64);
        await Assert.That(LogCensus.Search(records, LogNeedle.ForBytes("control.neighbour", neighbour))).IsEmpty();
    }

    [Test]
    public async Task Search_ForAValueLoggedInADifferentCase_FindsItOnlyWhenTheNeedleIgnoresCase()
    {
        // Arrange
        string planted = Planted();
        string neighbour = Planted();

        // Act
        IReadOnlyList<CapturedLogRecord> records = await CaptureAsync(loggers =>
            loggers.CreateLogger("LogRecorderControl.Case")
                .LogInformation("A control carrying {Value}", planted.ToUpperInvariant()));

        // Assert — the case-insensitive column's comparison is what finds a re-cased address.
        await Assert.That(LogCensus.Search(records, LogNeedle.ForText("control.text", planted, ignoresCase: true)))
            .IsNotEmpty();
        await Assert.That(LogCensus.Search(records, LogNeedle.ForText("control.text", planted, ignoresCase: false)))
            .IsEmpty();
        await Assert.That(LogCensus.Search(records, LogNeedle.ForText("control.neighbour", neighbour, ignoresCase: true)))
            .IsEmpty();
    }

    private static string Planted() => $"planted-{Guid.NewGuid():N}";

    private static IReadOnlyList<LogOffence> OffencesFor(IReadOnlyList<CapturedLogRecord> records, string value) =>
        LogCensus.Search(records, LogNeedle.ForText("control.text", value, ignoresCase: false));

    /// <summary>
    /// Boots a real API host with a recorder attached, lets <paramref name="write" /> log through the
    /// host's own logger factory, disposes the host and returns what the recorder holds.
    /// </summary>
    private static async Task<IReadOnlyList<CapturedLogRecord>> CaptureAsync(Action<ILoggerFactory> write)
    {
        await using PostgresTestHost host = new();
        await host.StartAsync();
        LogRecorder recorder = new();

        ApiFactory factory = host.CreateFactory(configureServices: recorder.AttachTo);
        await using (factory)
        {
            write(factory.Services.GetRequiredService<ILoggerFactory>());
        }

        return recorder.Snapshot();
    }
}
