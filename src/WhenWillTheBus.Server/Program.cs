// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;
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

// The token provider is a SINGLETON while the client is transient: Apple
// rate-limits provider-token regeneration, so the token must outlive a request.
builder.Services.AddSingleton<ApnsTokenProvider>();

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

// Push to every Live Activity currently registered.
//
// /apns/check proves Apple accepts our credentials; this proves a push actually
// reaches a phone and the widget can decode it. Together they cover the whole
// channel, and neither needs a bus.
//
// The state sent differs visibly from what the app started with -- a different
// stage, carrying an alert -- so "did it arrive" is answered by looking at the
// Lock Screen rather than by reading a log.
app.MapPost("/push/test", async (
    DeviceRegistry registry,
    ApnsClient apns,
    LocalClock clock,
    HttpRequest request,
    CancellationToken token) =>
{
    IReadOnlyCollection<RegisteredActivity> activities = registry.All;
    if (activities.Count == 0)
    {
        return Results.Ok(new
        {
            pushed = 0,
            note = "No Live Activity is registered. Start one from the app's Settings first -- "
                + "the phone has to create the activity before anything can push to it.",
        });
    }

    bool ending = string.Equals(request.Query["end"], "true", StringComparison.OrdinalIgnoreCase);
    DateTimeOffset now = clock.Now;

    BusActivityState state = new()
    {
        Stage = ending ? JourneyStage.Home : JourneyStage.AtStop,
        Target = now,
        Progress = 100,
        Basis = PredictionBasis.Route,
        FixedAt = now,
    };

    List<object> results = [];
    foreach (RegisteredActivity activity in activities)
    {
        PushResult result = ending
            ? await apns.EndAsync(activity.PushToken, state, now, token)
            : await apns.UpdateAsync(
                activity.PushToken,
                state,
                PushUrgency.TimeSensitive,
                now.AddMinutes(10),
                ("Test push", "If you can read this, the worker can reach your Lock Screen."),
                token);

        if (result.TokenIsDead)
        {
            registry.Forget(activity.PushToken);
        }

        results.Add(new
        {
            activity.JourneyId,
            delivered = result.Delivered,
            appleStatus = (int)result.Status,
            appleReason = result.Reason,
            tokenForgotten = result.TokenIsDead,
        });
    }

    await registry.SaveAsync(token);
    return Results.Ok(new { pushed = results.Count, ending, activities = results });
});

// Prove the APNs configuration without waiting for a school run.
//
// Everything about push is silent when it is wrong: a bad signing key, a
// mismatched team, the wrong topic, or a sandbox/production mix-up all end with
// Apple accepting nothing and nobody being told. The only natural trigger is a
// real journey, which happens twice a school day and not at all in the
// holidays -- a terrible feedback loop for a configuration error.
//
// So: push to a token that certainly is not registered, and read the REASON
// Apple gives back. BadDeviceToken means the JWT was signed correctly, the team
// was recognised and the topic was accepted -- everything except the token,
// which we knew. Any other reason names the part that is actually wrong.
app.MapPost("/apns/check", async (ApnsClient apns, IOptions<ApnsOptions> options, CancellationToken token) =>
{
    const string NotARealToken = "00000000000000000000000000000000000000000000000000000000000000ff";

    BusActivityState probe = new()
    {
        Stage = JourneyStage.Idle,
        FixedAt = DateTimeOffset.UtcNow,
    };

    PushResult result = await apns.UpdateAsync(
        NotARealToken, probe, PushUrgency.Passive, cancellationToken: token);

    ApnsOptions apnsOptions = options.Value;
    (bool healthy, string verdict) = result.Reason switch
    {
        "BadDeviceToken" or "DeviceTokenNotForTopic" or "Unregistered" => (true,
            "Credentials are good. Apple signed off on the key, the team and the topic, "
            + "and rejected only the fake token -- which is the point."),

        "InvalidProviderToken" => (false,
            "Apple would not accept the signing token. The .p8, Apns:KeyId and Apns:TeamId "
            + "have to belong together; one of them does not."),

        "ExpiredProviderToken" => (false,
            "Apple says the signing token has expired, which normally means this host's "
            + "clock is wrong rather than the key."),

        "TopicDisallowed" or "BadTopic" => (false,
            $"Apple refused the topic '{apnsOptions.LiveActivityTopic}'. Apns:BundleId must be "
            + "the APP's bundle id, and that App ID needs the Push Notifications capability."),

        "Forbidden" => (false,
            "Apple refused outright. Usually the key has been revoked."),

        null when result.Delivered => (false,
            "Apple ACCEPTED a push to a token that cannot exist, which should be impossible. "
            + "Treat this as unverified."),

        _ => (false, $"Unrecognised response from Apple: {result.Reason ?? "(none)"}."),
    };

    return Results.Ok(new
    {
        healthy,
        verdict,
        environment = apnsOptions.UseSandbox ? "sandbox" : "production",
        host = apnsOptions.Host,
        topic = apnsOptions.LiveActivityTopic,
        keyId = apnsOptions.KeyId,
        teamId = apnsOptions.TeamId,
        appleStatus = (int)result.Status,
        appleReason = result.Reason,
    });
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
