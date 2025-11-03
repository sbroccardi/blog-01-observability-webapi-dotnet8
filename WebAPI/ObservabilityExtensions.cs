using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

public static class ObservabilityExtensions
{
    public static readonly Meter ForecastMeter = new("WebAPI.Weather.Forecast", "1.0.0");
    public static readonly ActivitySource ForecastActivitySource = new("WebAPI.Weather.Forecast", "1.0.0");
    public static Counter<long> ForecastRequestsCounter = ForecastMeter.CreateCounter<long>("forecast_requests", unit: "1", description: "Counts forecast requests.");

    public static void AddObservability(this WebApplicationBuilder builder)
    {
        string serviceName = builder.Environment.ApplicationName; // e.g. "WebAPI.Weather"
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"; // e.g. "1.0.0"
        string serviceInstanceId = Environment.MachineName;

        // Configure the OTLP collector endpoint via configuration (environment variables) with a sane default.
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "http://otel-collector:4317";

        builder.Logging.AddConsole(); // Keep console logging for local debugging/demo purposes.
        builder.Logging.AddOpenTelemetry(options =>
        {
            // Enrich logs with trace/span IDs so logs can be correlated with traces.
            options.IncludeScopes = true; // enables trace/span IDs and custom scopes
            options.ParseStateValues = true; // parses structured log state
            options.IncludeFormattedMessage = true; // helpful for human-readable console logs
            options.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)); // Export logs to OTLP (Collector). Protocol defaults to gRPC; you can change as needed.
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService( // Configure resource information (these attributes are attached to all telemetry).
                serviceName: serviceName,
                serviceVersion: version,
                serviceInstanceId: serviceInstanceId))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation() // Instrument incoming HTTP requests automatically
                .AddHttpClientInstrumentation() // Instrument outgoing HTTP client calls
                .AddSource(ForecastActivitySource.Name) // Add traces created by our ActivitySource.
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(1.0))) // Decrease sampling ratio in production.
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint))) // Export traces using OTLP (gRPC) to a collector or backend that supports OTLP.
            .WithMetrics(m => m
                .AddMeter(ForecastMeter.Name) // Add your custom application meter so your counters are collected.
                .AddRuntimeInstrumentation() // Helpful runtime metrics
                .AddProcessInstrumentation() // Helpful process metrics (GC, threads, CPU, memory, etc.)
                                             //.AddAspNetCoreInstrumentation() // Replace older "instrumentation" packages and HTTP/runtime metrics.
                .AddMeter("Microsoft.AspNetCore.Hosting")        // incoming request metrics
                .AddMeter("Microsoft.AspNetCore.Server.Kestrel") // connection metrics
                .AddMeter("System.Net.Http")                     // outgoing http metrics
                .AddMeter("System.Net.NameResolution")           // DNS resolution metrics
                .AddConsoleExporter() // For demo purposes, export metrics to console as well.
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint))); // Export metrics to OTLP (collector) by default.
    }
}