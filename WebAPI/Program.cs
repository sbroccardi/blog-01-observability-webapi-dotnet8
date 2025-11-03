using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------
// Basic app setup  
// ---------------------------

// Add minimal services used by the generated template (Swagger/OpenAPI support).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Observability Web API",
        Version = "v1",
        Description = "ASP.NET Core 8 demo API instrumented with OpenTelemetry."
    });
});

// Add health checks endpoint
builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());

// Add observability (tracing, metrics, logging) using OpenTelemetry.
builder.AddObservability();

var app = builder.Build();

// ---------------------------
// HTTP request pipeline
// ---------------------------
if (app.Environment.IsDevelopment())
{
    // Developer friendly API docs
    app.UseSwagger();
    app.UseSwaggerUI();
} else
{
    // Enforce HSTS in production scenarios
    app.UseHsts();
}

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

// Minimal API endpoint that demonstrates tracing, metrics, and structured logging.
app.MapGet("/weatherforecast", (ILogger<Program> logger, HttpContext httpContext, CancellationToken cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Reutilizamos el span "Server" creado automáticamente por AspNetCoreInstrumentation
        var activity = Activity.Current;
        if (activity is not null && activity.IsAllDataRequested)
        {
            // Add baggage to propagate userId downstream (if there were any downstream calls).
            activity?.SetBaggage("user.id", httpContext.User?.Identity?.Name ?? "anonymous");
        }

        try
        {
            // Increment the forecast fetched event counter.
            ObservabilityExtensions.ForecastRequestsCounter.Add(1, new KeyValuePair<string, object?>("event","add"));

            // Start a Sub-activity (span) scoped to this request handler. This will be picked up by the tracer.
            using var subActivity = ObservabilityExtensions.ForecastActivitySource.StartActivity("ForecastGeneration", ActivityKind.Internal);

            // Add an event to the Activity to mark the start of forecast generation.
            subActivity?.AddEvent(new("forecast.generate.start"));

            logger.ForecastFetchStarted();

            var stopwatch = Stopwatch.StartNew();
            
            var summaries = new[]
            {
                "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
            };

            // Create some sample forecast data.
            var forecast = Enumerable.Range(1, 5).Select(index =>
                    new WeatherForecast
                    (
                        DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                        Random.Shared.Next(-20, 55),
                        summaries[Random.Shared.Next(summaries.Length)]
                    ))
                .ToArray();

            stopwatch.Stop();

            if (subActivity is not null && subActivity.IsAllDataRequested)
            {
                subActivity.AddEvent(new ActivityEvent("forecast.generate.end", tags: new ActivityTagsCollection
                {
                    { "forecast.temp.min", forecast.Min(f => f.TemperatureC) },
                    { "forecast.temp.max", forecast.Max(f => f.TemperatureC) }
                }));

                // Add a tag/attribute to the Activity.
                // This allows filtering or aggregation in backends that support attributes.
                subActivity.SetTag("forecast.count", forecast.Length);
                subActivity.SetTag("forecast.elapsed_ms", stopwatch.ElapsedMilliseconds);
                subActivity.SetStatus(ActivityStatusCode.Ok, "Forecast generated successfully");
            }
            
            if (activity is not null && activity.IsAllDataRequested)
            {
                activity.SetStatus(ActivityStatusCode.Ok, "Forecast generation completed successfully");
            }

            // Structured log message. Because OpenTelemetry logging is enabled, logs will include traceIds.
            logger.ForecastFetchFinished(stopwatch.ElapsedMilliseconds);
            
            return Results.Ok(forecast);
        }
        catch (Exception ex)
        {
            if (activity is not null && activity.IsAllDataRequested)
            {
                //Set Span Status
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddEvent(new("forecast.generate.failed", tags: new ActivityTagsCollection
                {
                    { "exception.type", ex.GetType().FullName },
                    { "exception.message", ex.Message }
                }));
            }

            logger.ForecastError(ex.Message);
            return Results.Problem("An unexpected error occurred while fetching forecast.");
        }
        
    })
    .WithName("GetWeatherForecast")
    .Produces<WeatherForecast[]>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithOpenApi(); // Enables OpenAPI metadata for Swagger

app.Run();

// Record used by the sample endpoint.
record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

internal static partial class WeatherForecastLoggerExtensions
{
    [LoggerMessage(LogLevel.Information, "Fetching forecast started.")]
    public static partial void ForecastFetchStarted(this ILogger logger);

    [LoggerMessage(LogLevel.Information, "Fetching forecast finished after {elapsedMilliseconds} ms.")]
    public static partial void ForecastFetchFinished(this ILogger logger, double elapsedMilliseconds);

    [LoggerMessage(LogLevel.Error, "Error during forecast generation: {Message}")]
    public static partial void ForecastError(this ILogger logger, string message);
}