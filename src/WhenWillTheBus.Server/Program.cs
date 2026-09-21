// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;
using WhenWillTheBus.Server.Apns;
using WhenWillTheBus.Server.Devices;
using WhenWillTheBus.Server.LiveActivity;
using WhenWillTheBus.Server.Monitoring;

// Container health check.
//
// The aspnet runtime image ships with neither curl nor wget, so a HEALTHCHECK
// written against either is not merely broken -- it fails silently and forever,
// and the container reports unhealthy while serving perfectly well. Probing
// ourselves needs no extra package and nothing new in the image.
if (args.Contains("--healthcheck"))
{
    try
    {
        using HttpClient probe = new() { Timeout = TimeSpan.FromSeconds(5) };
        string url = Environment.GetEnvironmentVariable("HEALTHCHECK_URL")
            ?? "http://127.0.0.1:8080/health";
        using HttpResponseMessage reply = await probe.GetAsync(url);
        return reply.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Secrets come from the environment or a Docker secret, never from a file in
// the repository: this process holds the family's WheresTheBus password AND
// knows where a child is right now.
builder.Configuration.AddEnvironmentVariables("WWTB_");

builder.Services
    .AddOptions<MonitorOptions>()
    .Bind(builder.Configuration.GetSection(MonitorOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<ApnsOptions>()
    .Bind(builder.Configuration.GetSection(ApnsOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(provider =>
{
    MonitorOptions options = provider.GetRequiredService<IOptions<MonitorOptions>>().Value;
    return new LocalClock(TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone));
});

builder.Services.AddSingleton(provider =>
    new PredictionEngine(provider.GetRequiredService<LocalClock>()));

builder.Services.AddHttpClient<WheresTheBusClient>(http =>
    {
        http.Timeout = Tuning.RequestTimeout;
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // The login 307-redirects to a per-account shard and the POST body must
        // be replayed against it. WheresTheBusClient does that itself, visibly,
        // rather than depending on handler behaviour that varies.
        AllowAutoRedirect = false,
    });

builder.Services.AddSingleton(provider =>
{
    MonitorOptions options = provider.GetRequiredService<IOptions<MonitorOptions>>().Value;
    return new WheresTheBusCredentials(options.Email, options.Password, options.DeviceId);
});

// APNs requires HTTP/2.
builder.Services.AddHttpClient<ApnsClient>(http =>
{
    http.DefaultRequestVersion = HttpVersion.Version20;
    http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
    http.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<LiveActivityPublisher>();
builder.Services.AddSingleton<BusMonitor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BusMonitor>());

WebApplication app = builder.Build();

string? apiKey = builder.Configuration["Api:Key"];
if (string.IsNullOrWhiteSpace(apiKey))
{
    app.Logger.LogCritical(
        "Api:Key is not set. This service knows a child's live location and will not "
        + "serve unauthenticated requests. Set WWTB_Api__Key to a long random value.");
    return 1;
}

// Everything but /health needs the key. Compared in fixed time so the endpoint
// cannot be used as an oracle to guess it a byte at a time.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    string presented = context.Request.Headers.Authorization.ToString();
    const string Prefix = "Bearer ";

    if (!presented.StartsWith(Prefix, StringComparison.Ordinal)
        || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented[Prefix.Length..]),
            Encoding.UTF8.GetBytes(apiKey)))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// A phone starts its Live Activity locally, then registers the activity's push
// token here so the worker can keep it up to date while the app is suspended.
app.MapPost("/activities", async (
    ActivityRegistration registration,
    DeviceRegistry registry,
    CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(registration.PushToken)
        || string.IsNullOrWhiteSpace(registration.JourneyId))
    {
        return Results.BadRequest(new { error = "journeyId and pushToken are both required" });
    }

    registry.Register(new RegisteredActivity(
        registration.JourneyId,
        registration.ChildId,
        registration.PushToken,
        DateTimeOffset.UtcNow));

    await registry.SaveAsync(token);
    return Results.NoContent();
});

app.MapDelete("/activities/{pushToken}", async (
    string pushToken,
    DeviceRegistry registry,
    CancellationToken token) =>
{
    registry.Forget(pushToken);
    await registry.SaveAsync(token);
    return Results.NoContent();
});

// Import a history export from the Home Assistant integration.
//
// Predictions work from two route samples, so a fortnight of this is the
// difference between a useful estimate today and one in October. The phone
// imports the same bundle through its own Settings screen; both run the same
// engine and each keeps its own copy.
//
// childId overrides the id in the bundle. Not a convenience: a school can
// reissue a child's id between terms, and history filed under the old one is
// silently never used -- the estimates simply never improve and nothing says
// why.
app.MapPost("/history/import", async (
    HttpRequest request,
    PredictionEngine engine,
    BusMonitor monitor,
    CancellationToken token) =>
{
    using StreamReader reader = new(request.Body);
    string json = await reader.ReadToEndAsync(token);

    IReadOnlyList<ImportedRider> riders;
    try
    {
        riders = HistoryImport.Parse(json);
    }
    catch (InvalidDataException error)
    {
        return Results.BadRequest(new { error = error.Message });
    }

    long? forced = long.TryParse(request.Query["childId"], out long only) ? only : null;
    if (forced is not null && riders.Count > 1)
    {
        return Results.BadRequest(new
        {
            error = "childId can only be forced when the bundle holds exactly one rider.",
        });
    }

    List<object> loaded = [];
    foreach (ImportedRider rider in riders)
    {
        if (forced is null && !long.TryParse(rider.ChildId, out _))
        {
            continue;
        }

        long childId = forced ?? long.Parse(rider.ChildId);
        engine.LoadHistory(childId, rider.Arrivals);
        loaded.Add(new
        {
            childId,
            imported = rider.Arrivals.Count,
            kept = engine.ArrivalsFor(childId).Count,
        });
    }

    await monitor.PersistAsync(token);
    return Results.Ok(new { riders = loaded });
});

// Hand the phone what this worker has learned.
//
// Both run the same engine but keep separate copies, and only one of them is
// always on: the worker watches every run, the phone learns only while somebody
// has it open. Without this the phone's estimates fall steadily behind and
// nothing says so -- they simply stop improving.
//
// This DOES carry route coordinates, so it sits behind the API key like
// everything else, and goes only to a device that already has the credentials.
app.MapGet("/history/export", (BusMonitor monitor, PredictionEngine engine) =>
{
    List<(long ChildId, IReadOnlyList<RunArrival> Arrivals)> riders = monitor.Students.Keys
        .Select(childId => (childId, engine.ArrivalsFor(childId)))
        .ToList();

    return Results.Text(HistoryExport.ToBundle(riders), "application/json");
});

// What the worker knows, WITHOUT coordinates. A status page is a convenience;
// leaking a child's position into one would not be.
app.MapGet("/status", (BusMonitor monitor, PredictionEngine engine, LocalClock clock) =>
{
    DateTimeOffset now = clock.Now;

    return Results.Ok(new
    {
        now,
        riders = monitor.Students.Values.Select(student =>
        {
            ArrivalPrediction? prediction = engine.PredictNextArrival(student, now);
            return new
            {
                student.ChildId,
                student.Name,
                student.BusNumber,
                arrivals = engine.ArrivalsFor(student.ChildId).Count,
                nextArrival = prediction?.Arrival,
                basis = prediction?.Basis.ToString(),
                samples = prediction?.Samples,
                routeSamples = prediction?.RouteSamples,
            };
        }),
    });
});

app.Run();
return 0;

/// <summary>What a phone sends after starting a Live Activity.</summary>
internal sealed record ActivityRegistration(string JourneyId, long ChildId, string PushToken);
