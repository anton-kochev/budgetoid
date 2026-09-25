using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntegrationTests;

/// <summary>
/// Records every log record a host writes, flattened to text at the moment it is written, so a test
/// can search what a sink would have been handed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flattened at <c>Log</c> time, never later.</b> A scope's state can read a pooled
/// <c>HttpContext</c> that the next request has already reused, and an exception's <c>Data</c> can be
/// written after the record left — so a record kept as live objects and rendered at snapshot time
/// describes some later state of the process, not what the sink saw. Every member of
/// <see cref="CapturedLogRecord" /> is a string for that reason.
/// </para>
/// <para>
/// <b>Trace for every category, through a provider-specific filter rule.</b> A provider-specific rule
/// outranks every category rule that names no provider, so no <c>Logging:LogLevel</c> setting the
/// application or its environment carries can narrow what this sees. What it cannot see is a record
/// the call site never makes — a <c>LoggerMessage</c> whose level is disabled everywhere is still
/// made, because this rule enables it.
/// </para>
/// <para>
/// Modelled on <see cref="StatementRecorder" />: a concurrent queue, no opinion about what a test is
/// looking for, and a snapshot the caller takes when it is done — after disposing the factory, so a
/// record written during shutdown is in it.
/// </para>
/// </remarks>
public sealed class LogRecorder : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLogRecord> _records = new();

    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>Every record captured so far, in the order they were written.</summary>
    public IReadOnlyList<CapturedLogRecord> Snapshot() => [.. _records];

    /// <summary>
    /// Registers this recorder on a host's services and opens it to every level of every category.
    /// </summary>
    /// <remarks>
    /// Meant for <see cref="ApiFactory" />'s <c>configureServices</c>, which runs last, so nothing the
    /// application registers afterwards can remove the rule.
    /// </remarks>
    public void AttachTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ILoggerProvider>(this);
        services.Configure<LoggerFilterOptions>(options =>
            options.Rules.Add(new LoggerFilterRule(
                typeof(LogRecorder).FullName, categoryName: null, LogLevel.Trace, filter: null)));
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    /// <summary>Deliberately empty: the records outlive the host so the test can read them.</summary>
    public void Dispose()
    {
    }

    private void Record<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        List<KeyValuePair<string, string>> templateValues = [];
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach ((string key, object? value) in pairs)
            {
                templateValues.Add(new KeyValuePair<string, string>(key, Render(value)));
            }
        }

        List<string> scopes = [];
        _scopes.ForEachScope(
            static (scope, sink) =>
            {
                StringBuilder text = new(Render(scope));
                if (scope is IEnumerable<KeyValuePair<string, object?>> scopePairs)
                {
                    foreach ((string key, object? value) in scopePairs)
                    {
                        text.Append(' ').Append(key).Append('=').Append(Render(value));
                    }
                }

                sink.Add(text.ToString());
            },
            scopes);

        _records.Enqueue(new CapturedLogRecord(
            category,
            level,
            eventId.Id,
            eventId.Name,
            formatter(state, exception),
            templateValues,
            scopes,
            exception is null ? null : RenderExceptionChain(exception)));
    }

    /// <summary>
    /// A value as a structured sink could render it: its own text, and — for bytes — the two
    /// encodings a JSON or OTLP exporter would reach for.
    /// </summary>
    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        byte[] bytes => RenderBytes(bytes),
        ReadOnlyMemory<byte> memory => RenderBytes(memory.ToArray()),
        Memory<byte> memory => RenderBytes(memory.ToArray()),
        IEnumerable sequence => string.Join(
            " ",
            sequence.Cast<object?>()
                .Select(element => Render(element))
                .Prepend(sequence.ToString() ?? string.Empty)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string RenderBytes(byte[] bytes) =>
        $"{Convert.ToBase64String(bytes)} {Convert.ToHexString(bytes)}";

    /// <summary>
    /// The exception, every inner exception and every member of every aggregate: each one's
    /// <c>ToString()</c> and each <c>Data</c> entry, as they stand right now.
    /// </summary>
    private static string RenderExceptionChain(Exception root)
    {
        StringBuilder text = new(root.ToString());
        Stack<Exception> pending = new([root]);
        HashSet<Exception> seen = new(ReferenceEqualityComparer.Instance);

        while (pending.TryPop(out Exception? exception))
        {
            if (!seen.Add(exception))
            {
                continue;
            }

            text.AppendLine().Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
            foreach (DictionaryEntry entry in exception.Data)
            {
                text.AppendLine().Append(Render(entry.Key)).Append('=').Append(Render(entry.Value));
            }

            if (exception is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }

            if (exception.InnerException is { } innerException)
            {
                pending.Push(innerException);
            }
        }

        return text.ToString();
    }

    private sealed class RecordingLogger(LogRecorder recorder, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => recorder._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            recorder.Record(category, logLevel, eventId, state, exception, formatter);
        }
    }
}

/// <summary>One log record, flattened to the text a sink would have been handed.</summary>
/// <param name="Category">The logger's category.</param>
/// <param name="Level">The record's level.</param>
/// <param name="EventId">The numeric event id.</param>
/// <param name="EventName">The event name, where the call site gave one.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="TemplateValues">Every key and value of the state, <c>{OriginalFormat}</c> included.</param>
/// <param name="Scopes">Every scope active at write time, its text and its key/values.</param>
/// <param name="Exception">The whole exception chain, or <see langword="null" /> when none was logged.</param>
public sealed record CapturedLogRecord(
    string Category,
    LogLevel Level,
    int EventId,
    string? EventName,
    string Message,
    IReadOnlyList<KeyValuePair<string, string>> TemplateValues,
    IReadOnlyList<string> Scopes,
    string? Exception)
{
    /// <summary>Everything the record carries, as one searchable text.</summary>
    public string Text { get; } = string.Join(
        "\n",
        new[] { Category, EventName ?? string.Empty, Message }
            .Concat(TemplateValues.Select(pair => $"{pair.Key}={pair.Value}"))
            .Concat(Scopes)
            .Append(Exception ?? string.Empty));

    /// <summary>Where the record came from, without anything it carried.</summary>
    public string Origin => $"{Category} {Level} event {EventId}";
}
