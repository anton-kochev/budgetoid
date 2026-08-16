using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ServiceDefaults;

public static class Extensions
{
    /// <summary>The path the liveness probe answers on.</summary>
    /// <remarks>
    /// A constant because it is named twice and the two must not drift: here, and in the API's
    /// first-party-client check, which exempts this one route and nothing else. Mapped raw by
    /// <c>MapHealthChecks</c>, so the endpoint carries no metadata that could identify it instead.
    /// </remarks>
    public const string HealthPath = "/health";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks();
        builder.ConfigureOpenTelemetry();
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Production-safe liveness endpoint for container platform probes (e.g. Azure Container Apps).
        // Do not include dependency checks here: a failing database should not cause the platform
        // to restart otherwise healthy API containers.
        app.MapHealthChecks(HealthPath, new HealthCheckOptions
        {
            Predicate = _ => false,
        })
            .AllowAnonymous();

        return app;
    }

    private static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddMeter("Microsoft.AspNetCore.Hosting"))
            .WithTracing(tracing => tracing.AddSource(builder.Environment.ApplicationName));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddOtlpExporter());
        }

        return builder;
    }
}
