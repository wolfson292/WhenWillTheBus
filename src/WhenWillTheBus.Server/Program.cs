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
using WhenWillTheBus.Server.Api;
using WhenWillTheBus.Server.Apns;
using WhenWillTheBus.Server.Devices;
using WhenWillTheBus.Server.LiveActivity;
using WhenWillTheBus.Server.Media;
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

builder.Services
    .AddOptions<MediaOptions>()
    .Bind(builder.Configuration.GetSection(MediaOptions.Section));

// Radarr and Sonarr are on the LAN and answer quickly or not at all; a long
// timeout here would only hold a phone's search spinner for half a minute.
builder.Services.AddHttpClient<ArrClient>(http => http.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<RequestLog>();

builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<ClientRegistry>();
builder.Services.AddSingleton<LiveActivityPublisher>();
builder.Services.AddSingleton<BusMonitor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<BusMonitor>());

DateTimeOffset started = DateTimeOffset.UtcNow;

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

    // Bearer for the app, Basic for a browser. The management page is meant to
    // be opened by a person, and a person cannot set a header -- while putting
    // the key in the query string would write it into history, bookmarks and
    // every access log between here and the sofa.
    string presented = context.Request.Headers.Authorization.ToString();
    string? offered = null;

    if (presented.StartsWith("Bearer ", StringComparison.Ordinal))
    {
        offered = presented["Bearer ".Length..];
    }
    else if (presented.StartsWith("Basic ", StringComparison.Ordinal))
    {
        try
        {
            // The username is ignored. There is one credential here, and asking
            // for a second invented one would only be something else to forget.
            string pair = Encoding.UTF8.GetString(Convert.FromBase64String(presented["Basic ".Length..]));
            int colon = pair.IndexOf(':', StringComparison.Ordinal);
            offered = colon < 0 ? pair : pair[(colon + 1)..];
        }
        catch (FormatException)
        {
            offered = null;
        }
    }

    if (offered is null
        || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(offered),
            Encoding.UTF8.GetBytes(apiKey)))
    {
        // Only the page challenges. A 401 carrying this header makes a browser
        // prompt, which is right for a person and wrong for the app -- it would
        // turn a bad key into a dialog nobody is there to answer.
        if (context.Request.Path.StartsWithSegments("/manage"))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"When Will The Bus\", charset=\"UTF-8\"";
        }

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

    // Default to sandbox: a build that did not say is an older one, and those
    // were all development builds.
    bool sandbox = !string.Equals(registration.Environment, "production", StringComparison.OrdinalIgnoreCase);

    registry.Register(new RegisteredActivity(
        registration.JourneyId,
        registration.ChildId,
        registration.PushToken,
        DateTimeOffset.UtcNow,
        sandbox,

        // Bounded like every other display-ish string arriving from a phone.
        Trimmed(registration.DeviceId, 64)));

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

// Everything a phone needs to draw its screens.
//
// Lets a phone run with NO WheresTheBus credentials: the family password then
// lives in one place rather than on every phone, and the third-party API sees
// one poller instead of four. Carries positions, so it sits behind the API key
// like everything else.
app.MapGet("/rider/state", (BusMonitor monitor, PredictionEngine engine, LocalClock clock) =>
    Results.Text(RiderState.Serialise(monitor.Students, engine, clock, clock.Now), "application/json"));

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
            ? await apns.EndAsync(activity.PushToken, state, now, activity.Sandbox, token)
            : await apns.UpdateAsync(
                activity.PushToken,
                state,
                PushUrgency.TimeSensitive,
                now.AddMinutes(10),
                ("Test push", "If you can read this, the worker can reach your Lock Screen."),
                activity.Sandbox,
                token);

        // Forget the token when the activity is gone: because Apple said so, or
        // because we just ended it. A registration outliving its activity is a
        // push to nothing, and it muddies the next test by answering alongside
        // the live one.
        bool forget = result.TokenIsDead || (ending && result.Delivered);
        if (forget)
        {
            registry.Forget(activity.PushToken);
        }

        results.Add(new
        {
            activity.JourneyId,
            environment = activity.Sandbox ? "sandbox" : "production",
            delivered = result.Delivered,
            appleStatus = (int)result.Status,
            appleReason = result.Reason,
            tokenForgotten = forget,
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
//
// BOTH HOSTS, EVERY TIME. This worker serves a development phone and a
// TestFlight household at once, and the two are separate Apple environments
// that can fail independently. Checking only the default proved the half whose
// owner was running the check and left the other half -- the half on everybody
// else's phone -- unverified until a school run quietly produced no card.
app.MapPost("/apns/check", async (ApnsClient apns, IOptions<ApnsOptions> options, CancellationToken token) =>
{
    const string NotARealToken = "00000000000000000000000000000000000000000000000000000000000000ff";

    BusActivityState probe = new()
    {
        Stage = JourneyStage.Idle,
        FixedAt = DateTimeOffset.UtcNow,
    };

    ApnsOptions apnsOptions = options.Value;

    EnvironmentCheck sandboxCheck = await ProbeAsync(true);
    EnvironmentCheck productionCheck = await ProbeAsync(false);

    return Results.Ok(new
    {
        // Both, because a household needs both. One environment answering
        // correctly says nothing about the other.
        healthy = sandboxCheck.Healthy && productionCheck.Healthy,
        topic = apnsOptions.LiveActivityTopic,
        keyId = apnsOptions.KeyId,
        teamId = apnsOptions.TeamId,
        fallback = apnsOptions.UseSandbox ? "sandbox" : "production",
        environments = new[] { sandboxCheck, productionCheck },
    });

    async Task<EnvironmentCheck> ProbeAsync(bool sandbox)
    {
        PushResult result = await apns.UpdateAsync(
            NotARealToken, probe, PushUrgency.Passive, sandbox: sandbox, cancellationToken: token);

        (bool healthy, string verdict) = Interpret(result, apnsOptions);

        return new EnvironmentCheck(
            sandbox ? "sandbox" : "production",
            ApnsOptions.HostFor(sandbox),
            healthy,
            verdict,
            (int)result.Status,
            result.Reason);
    }
});

/// <summary>What Apple's refusal of a deliberately invalid token actually means.</summary>
static (bool Healthy, string Verdict) Interpret(PushResult result, ApnsOptions apnsOptions)
{
    return result.Reason switch
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
}

// A phone introducing itself, so the management page can say WHICH phones are
// talking to this worker rather than only how many.
//
// What it may send is decided by Apple, not by us. identifierForVendor is the
// one device identifier an app is permitted to use here -- it is scoped to this
// vendor, and iOS reissues it once the last of our apps leaves the device. The
// label is typed by the owner because UIDevice.name has returned a generic
// model name since iOS 16 without an entitlement Apple grants for managed
// fleets. Nothing else identifying is accepted: no advertising identifier, no
// UDID or serial, no MAC address, no phone number, no account.
app.MapPost("/clients/hello", async (
    ClientHello hello,
    ClientRegistry clients,
    LocalClock clock,
    CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(hello.Id))
    {
        return Results.BadRequest(new { error = "id is required" });
    }

    DateTimeOffset now = clock.Now;
    ClientIdentity known = clients.Greet(
        new ClientIdentity(
            hello.Id,
            Trimmed(hello.Label, 40),
            Trimmed(hello.Model, 40),
            Trimmed(hello.SystemVersion, 20),
            Trimmed(hello.AppVersion, 20),
            Trimmed(hello.Build, 20),
            !string.Equals(hello.Environment, "production", StringComparison.OrdinalIgnoreCase),
            now,
            now,
            1),
        now);

    await clients.SaveAsync(token);
    return Results.Ok(new { known.Id, known.Label, known.FirstSeen, known.Visits });
});

// The same thing as JSON, for anything that would rather not scrape a page.
app.MapGet("/clients", (ClientRegistry clients) => Results.Ok(clients.All));

// ---------------------------------------------------------------- requests
//
// Searching and asking for something to be downloaded. Behind the same bearer
// key as everything else, which is the whole authorisation model here: anyone
// who can reach this is the household.

app.MapGet("/media/search", async (
    string q,
    string? kind,
    ArrClient arr,
    CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest(new { error = "q is required" });
    }

    // Both by default. Somebody typing a title usually does not care which of
    // two programs is going to hold it, and being asked to choose first is a
    // question about our plumbing rather than about what they want to watch.
    MediaKind[] kinds = kind?.ToLowerInvariant() switch
    {
        "movie" => [MediaKind.Movie],
        "tv" or "series" => [MediaKind.Series],
        _ => [MediaKind.Movie, MediaKind.Series],
    };

    List<MediaResult> results = [];
    foreach (MediaKind each in kinds.Where(arr.Supports))
    {
        results.AddRange((await arr.SearchAsync(each, q, token)).Take(12));
    }

    return Results.Ok(new
    {
        results = results.Select(result => new
        {
            kind = result.Kind is MediaKind.Series ? "series" : "movie",
            result.Title,
            result.Year,
            result.RemoteId,
            result.Overview,
            result.PosterUrl,
            result.AlreadyHave,
        }),
    });
});

app.MapPost("/media/request", async (
    MediaAsk ask,
    ArrClient arr,
    RequestLog log,
    ClientRegistry clients,
    LocalClock clock,
    IOptions<MediaOptions> media,
    CancellationToken token) =>
{
    MediaKind kind = string.Equals(ask.Kind, "series", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ask.Kind, "tv", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Series
            : MediaKind.Movie;

    if (ask.RemoteId <= 0)
    {
        return Results.BadRequest(new { error = "remoteId is required" });
    }

    AddOutcome outcome = media.Value.AutoApprove
        ? await arr.AddAsync(kind, ask.RemoteId, token)
        : new AddOutcome(false, "Saved — waiting to be approved.");

    // WHO ASKED comes from the phone that asked, not from the body: the client
    // identifier is one a phone has already proved by using it, and a name in
    // a request body is whatever the sender felt like typing.
    string? who = clients.Find(ask.ClientId)?.Label;

    log.Record(new MediaRequest(
        kind,
        ask.Title ?? "(unnamed)",
        ask.Year,
        ask.RemoteId,
        ask.PosterUrl,
        who,
        clock.Now,
        outcome.Outcome));

    await log.SaveAsync(token);
    return Results.Ok(new { added = outcome.Added, outcome = outcome.Outcome });
});

app.MapGet("/media/requests", (RequestLog log) => Results.Ok(log.Recent().Select(request => new
{
    kind = request.Kind is MediaKind.Series ? "series" : "movie",
    request.Title,
    request.Year,
    request.PosterUrl,
    request.RequestedBy,
    request.RequestedAt,
    request.Outcome,
})));

// The management page. A browser reaches it with Basic auth; everything on it
// is also available as JSON from /status, /rider/state and /clients.
app.MapGet("/manage", (
    BusMonitor monitor,
    PredictionEngine engine,
    ClientRegistry clients,
    DeviceRegistry registry,
    LocalClock clock) => Results.Content(
        ManagementPage.Render(
            monitor.Students, engine, clients.All, registry.All, clock, started, clock.Now),
        "text/html; charset=utf-8"));

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

// Bounded on the way in. These are display strings from an unauthenticated
// shape, and a label of a megabyte would be stored, persisted and rendered.
static string? Trimmed(string? value, int limit)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    string clean = value.Trim();
    return clean.Length <= limit ? clean : clean[..limit];
}

/// <summary>
/// What a phone tells the worker about itself.
/// </summary>
/// <remarks>
/// Every field is optional except the identifier, and every field is a claim
/// rather than a fact — this is a phone describing itself over the network.
/// Nothing here is trusted for anything but display, which is why none of it
/// is used for authorisation: the bearer key decides that.
/// </remarks>
internal sealed record ClientHello(
    string Id,
    string? Label,
    string? Model,
    string? SystemVersion,
    string? AppVersion,
    string? Build,
    string? Environment);

/// <summary>A request to add something, as a phone sends it.</summary>
/// <remarks>
/// Carries only an identifier and enough text to log; the payload the media
/// server is actually given is rebuilt on this side from what the media server
/// itself says about that identifier.
/// </remarks>
internal sealed record MediaAsk(
    string? Kind, long RemoteId, string? Title, int? Year, string? PosterUrl, string? ClientId);

/// <summary>What a phone sends after starting a Live Activity.</summary>
/// <param name="Environment">"sandbox" or "production"; sandbox when a build did not say.</param>
/// <param name="DeviceId">
/// Which phone, so a token the OS reissued mid-card replaces its predecessor
/// rather than joining it. Absent from builds that predate the field.
/// </param>
internal sealed record ActivityRegistration(
    string JourneyId, long ChildId, string PushToken, string? Environment, string? DeviceId);

/// <summary>One APNs environment's answer to the configuration probe.</summary>
/// <remarks>
/// Both are reported on every check. A worker serving a development phone and a
/// TestFlight household talks to two separate Apple environments, and they fail
/// independently — so proving one says nothing about the other.
/// </remarks>
/// <param name="Environment">"sandbox" or "production".</param>
/// <param name="Reason">Apple's own word for the refusal. BadDeviceToken is the healthy one.</param>
internal sealed record EnvironmentCheck(
    string Environment,
    string Host,
    bool Healthy,
    string Verdict,
    int AppleStatus,
    string? Reason);
