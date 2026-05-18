using System.Text.Json.Serialization;
using BookingStateMachine.Endpoints;
using BookingStateMachine.HealthChecks;
using BookingStateMachine.Metrics;
using BookingStateMachine.Services;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Events;

// ── 1. Serilog structured logging ─────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("System",    LogEventLevel.Warning)
    .Enrich.FromLogContext()
    // Emit JSON lines; swap to WriteTo.Console() for human-readable output.
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
Console.WriteLine("START 1");
builder.Host.UseSerilog();

// ── 2. Services ───────────────────────────────────────────────────────────────

builder.Services.ConfigureHttpJsonOptions(opt =>
    opt.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<BookingRepository>();
builder.Services.AddSingleton<BookingMetrics>();
builder.Services.AddScoped<BookingStateMachineService>();

// ── 3. OpenTelemetry metrics → Prometheus scrape endpoint ─────────────────────
builder.Services.AddOpenTelemetry()
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation();                    // HTTP request metrics
        m.AddMeter(BookingMetrics.MeterName);               // our custom meter
        m.AddPrometheusExporter();                           // /metrics
    });

// ── 4. Health checks ──────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<LivenessHealthCheck> ("liveness",  tags: ["live"])
    .AddCheck<ReadinessHealthCheck>("readiness", tags: ["ready"]);

// ── 5. OpenAPI / Swagger ──────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opt =>
{
    opt.SwaggerDoc("v1", new() { Title = "Booking State Machine", Version = "v1" });
});

// ── 6. Build ──────────────────────────────────────────────────────────────────
Console.WriteLine("START 2");
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(opt =>
    {
        opt.SwaggerEndpoint("/swagger/v1/swagger.json", "Booking State Machine v1");
        opt.RoutePrefix = "swagger";
    });
}

app.UseHttpsRedirection();

// ── 7. Endpoints ──────────────────────────────────────────────────────────────

// Business endpoints
app.MapBookingEndpoints();

// Health probes
app.MapHealthChecks("/health/live",  new() { Predicate = c => c.Tags.Contains("live")  });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });

// Prometheus metrics scrape
app.MapPrometheusScrapingEndpoint("/metrics");

Console.WriteLine("START 3");
app.Run();

// Make Program accessible from test projects (integration tests).
public partial class Program { }
