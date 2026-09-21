// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Core.Storage;

namespace WhenWillTheBus.App.Services;

/// <summary>
/// What the app knows about one rider right now, for the UI to bind to.
/// </summary>
public sealed class BusService : INotifyPropertyChanged
{
    private readonly CredentialStore _credentials;
    private readonly ServerLink _server;
    private readonly HttpClient _http;
    private readonly LocalClock _clock;
    private readonly PredictionEngine _engine;

    private WheresTheBusClient? _client;
    private Dictionary<long, Student> _students = [];
    private readonly Dictionary<long, long> _lastServerTime = [];
    private CancellationTokenSource? _polling;
    private string? _activityJourneyId;
    private bool _testCard;
    private DateTimeOffset _scansPolledAt = DateTimeOffset.MinValue;

    private Student? _rider;
    private ArrivalPrediction? _prediction;
    private Journey _journey = Journey.Idle;
    private RiderInfo? _latest;
    private SchoolArrival? _school;
    private string? _problem;

    public BusService(CredentialStore credentials, ServerLink server, HttpClient http)
    {
        _credentials = credentials;
        _server = server;
        _http = http;
        _clock = new LocalClock(TimeZoneInfo.Local);
        _engine = new PredictionEngine(_clock);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Student? Rider { get => _rider; private set => Set(ref _rider, value); }

    public ArrivalPrediction? Prediction { get => _prediction; private set => Set(ref _prediction, value); }

    public Journey Journey { get => _journey; private set => Set(ref _journey, value); }

    public RiderInfo? Latest { get => _latest; private set => Set(ref _latest, value); }

    /// <summary>When the morning ride is expected to reach school, once learned.</summary>
    public SchoolArrival? School { get => _school; private set => Set(ref _school, value); }

    /// <summary>Whatever is currently stopping this working, in words a parent can act on.</summary>
    public string? Problem { get => _problem; private set => Set(ref _problem, value); }

    /// <summary>
    /// True when a Live Activity is running that nothing can update once the app
    /// is suspended. Worth telling the reader: the card will simply stop.
    /// </summary>
    public bool LiveActivityIsLocalOnly =>
        _activityJourneyId is not null && !LiveActivityBridge.HasPush;

    /// <summary>Where learned history lives: the app container, not iCloud.</summary>
    private static string HistoryPath =>
        Path.Combine(FileSystem.AppDataDirectory, "history.json");

    /// <summary>Sign in and start watching. Safe to call again.</summary>
    /// <remarks>
    /// Nothing may escape this method. It is called from OnAppearing and
    /// OnResume, both of which are async void, where an unhandled exception
    /// terminates the process rather than surfacing anywhere. An app a parent is
    /// relying on to know where their child is should degrade to an error
    /// message, never to a crash.
    /// </remarks>
    public async Task StartAsync()
    {
        try
        {
            await StartCoreAsync();
        }
        catch (Exception error)
        {
            Problem = $"Could not start: {error.Message}";
        }
    }

    private async Task StartCoreAsync()
    {
        WheresTheBusCredentials? credentials = await _credentials.ReadAsync();
        if (credentials is null)
        {
            Problem = "Sign in to see the bus.";
            return;
        }

        _client ??= new WheresTheBusClient(_http, credentials);

        await LoadHistoryAsync();

        LiveActivityBridge.OnPushToken(token =>
        {
            // Fire and forget, and swallow: this runs from a native callback,
            // and a worker that cannot be reached is a degraded card, not a
            // reason to take the app down.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_activityJourneyId is string journeyId && Rider is not null)
                    {
                        await _server.RegisterAsync(journeyId, Rider.ChildId, token);
                    }
                }
                catch (Exception)
                {
                    // The card still works locally; see LiveActivityIsLocalOnly.
                }
            });
        });

        _polling?.Cancel();
        _polling = new CancellationTokenSource();
        _ = PollAsync(_polling.Token);
    }

    /// <summary>Stop polling when the app goes to the background.</summary>
    /// <remarks>
    /// Deliberate: iOS will suspend this process within seconds anyway, and the
    /// worker takes over from here. Pretending otherwise would just burn battery
    /// and hammer somebody else's service for readings nobody sees.
    /// </remarks>
    public void Pause() => _polling?.Cancel();

    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TimeSpan wait = Tuning.BusPollInterval;

            try
            {
                wait = await TickAsync(token);
                Problem = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (WheresTheBusAuthException)
            {
                Problem = "WheresTheBus rejected the sign-in. Check the email and password.";
                return;
            }
            catch (WheresTheBusException error)
            {
                Problem = $"Could not reach WheresTheBus: {error.Message}";
                wait = TimeSpan.FromMinutes(1);
            }

            try
            {
                await Task.Delay(wait, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<TimeSpan> TickAsync(CancellationToken token)
    {
        DateTimeOffset now = _clock.Now;

        if (_students.Count == 0)
        {
            await RefreshRosterAsync(token);
            Rider = _students.Values.FirstOrDefault();
        }

        if (Rider is null || Rider.BusNumber is null)
        {
            Problem = "No rider on this account yet.";
            return Tuning.ScanPollInterval;
        }

        if (now - _scansPolledAt >= Tuning.ScanPollInterval)
        {
            await RefreshScansAsync(token);
            _scansPolledAt = now;
        }

        Student rider = _students[Rider.ChildId];

        RiderInfo info = await _client!.GetRiderInfoAsync(
            rider.BusNumber!, rider.ChildId, _lastServerTime.GetValueOrDefault(rider.ChildId), token);

        _lastServerTime[rider.ChildId] = info.ServerTime;
        _engine.Observe(rider, info, now);

        ArrivalPrediction? prediction = _engine.PredictNextArrival(rider, now);

        // The morning stage needs a target or it cannot fire at all -- see the
        // same call in the worker.
        SchoolArrival? school = SchoolArrivalPredictor.Predict(rider, now, _clock);
        Journey journey = _engine.Stage(rider, now, prediction, school?.Arrival);

        Latest = info;
        School = school;
        Prediction = prediction;
        Journey = journey;
        Rider = rider;

        DriveLiveActivity(rider, journey, prediction, info, now);

        if (_engine.PromotePending(_students, now))
        {
            await SaveHistoryAsync();
        }

        // Full rate while a run is actually happening; otherwise there is nothing
        // a reading could change, and the service belongs to somebody else.
        bool watched = _engine.IsWatching(rider, Run.Am, now) || _engine.IsWatching(rider, Run.Pm, now);
        return watched ? Tuning.BusPollInterval : Tuning.ScanPollInterval;
    }

    /// <summary>
    /// Start, update or end the Live Activity.
    /// </summary>
    /// <remarks>
    /// Started LOCALLY, which is the whole point: push-to-start does not work
    /// when the app is closed, its token goes stale on Apple's side with no
    /// signal, and every one of those failures is silent.
    ///
    /// Tagged with the journey id, so a new journey is a new activity and a
    /// failed start leaves nothing wedged.
    /// </remarks>
    private void DriveLiveActivity(
        Student rider,
        Journey journey,
        ArrivalPrediction? prediction,
        RiderInfo info,
        DateTimeOffset now)
    {
        if (!LiveActivityBridge.Enabled)
        {
            return;
        }

        // A test card is not a journey, and the next poll would otherwise end it
        // a second after it appeared -- which looks exactly like a card that
        // failed to start.
        if (_testCard)
        {
            return;
        }

        if (journey.Active && journey.JourneyId is string journeyId)
        {
            if (_activityJourneyId != journeyId)
            {
                if (LiveActivityBridge.Start(journeyId, rider.Name, rider.ChildId, journey, prediction, info, now))
                {
                    _activityJourneyId = journeyId;
                }
            }
            else
            {
                LiveActivityBridge.Update(journey, prediction, info, now);
            }

            return;
        }

        if (_activityJourneyId is not null)
        {
            LiveActivityBridge.End(journey, prediction, info, now);
            _activityJourneyId = null;
        }
    }

    private async Task RefreshRosterAsync(CancellationToken token)
    {
        using System.Text.Json.JsonDocument userInfo = await _client!.GetUserInfoAsync(token);
        using System.Text.Json.JsonDocument allRiders = await _client.GetAllRidersAsync(token);

        Dictionary<long, Student> fresh = RosterReader.ReadRoster(
            userInfo.RootElement.GetProperty("payload"),
            allRiders.RootElement.GetProperty("payload"));

        foreach ((long childId, Student student) in fresh)
        {
            if (_students.TryGetValue(childId, out Student? existing))
            {
                fresh[childId] = student with { Scans = existing.Scans };
            }
        }

        _students = fresh;
    }

    private async Task RefreshScansAsync(CancellationToken token)
    {
        using System.Text.Json.JsonDocument scans = await _client!.GetStudentScansAsync(token);

        Dictionary<long, List<ScanEvent>> byChild = RosterReader.ReadScans(
            scans.RootElement.GetProperty("payload"), _students);

        foreach ((long childId, List<ScanEvent> fresh) in byChild)
        {
            if (!_students.TryGetValue(childId, out Student? student))
            {
                continue;
            }

            // Merged BEFORE classifying, because the alternation fallback needs a
            // whole day and the endpoint only ever returns today.
            List<ScanEvent> merged = student.Scans
                .Concat(fresh)
                .GroupBy(scan => scan.Timestamp)
                .Select(group => group.First())
                .OrderBy(scan => scan.Timestamp)
                .TakeLast(HistoryStore.ScanHistoryLimit)
                .ToList();

            _students[childId] = student with
            {
                Scans = ScanClassifier.Classify(merged, student.SchoolName, _clock),
            };
        }
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            foreach (StoredRider rider in await HistoryStore.LoadAsync(HistoryPath))
            {
                _engine.LoadHistory(rider.ChildId, rider.Arrivals);
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            // Unreadable history costs accuracy, not correctness: the estimate
            // falls back to the timetable and relearns.
            Problem = "Stored history could not be read; relearning from today.";
        }
    }

    private async Task SaveHistoryAsync() =>
        await HistoryStore.SaveAsync(
            HistoryPath,
            _students.Keys.Select(childId => new StoredRider
            {
                ChildId = childId,
                Arrivals = _engine.ArrivalsFor(childId),
                Scans = _students[childId].Scans,
            }));

    /// <summary>
    /// Take whatever the worker has learned. Returns how many arrivals resulted,
    /// or -1 when the worker could not be reached.
    /// </summary>
    public async Task<int> SyncHistoryFromWorkerAsync()
    {
        string? bundle = await _server.FetchHistoryAsync();
        return bundle is null ? -1 : await ImportHistoryAsync(bundle);
    }

    /// <summary>
    /// Start a Live Activity that is not attached to a real journey, so the push
    /// path can be proved without waiting for a school run.
    /// </summary>
    /// <remarks>
    /// This is a REAL activity, started the same way and registered the same
    /// way. A test that took a different path would prove nothing about the one
    /// that matters.
    /// </remarks>
    public async Task<string> StartTestCardAsync()
    {
        if (!LiveActivityBridge.Enabled)
        {
            return "Live Activities are switched off for this app in iOS Settings.";
        }

        if (Rider is null)
        {
            return "Sign in first, so the card has a name to show.";
        }

        DateTimeOffset now = _clock.Now;
        string journeyId = $"test-{_clock.ToLocal(now):yyyyMMdd-HHmmss}";

        Journey pretend = new()
        {
            Stage = JourneyStage.ToStop,
            Progress = 35,
            Target = now.AddMinutes(8),
            JourneyId = journeyId,
        };

        _testCard = true;
        _activityJourneyId = journeyId;

        if (!LiveActivityBridge.Start(journeyId, Rider.Name, Rider.ChildId, pretend, null, Latest, now))
        {
            _testCard = false;
            _activityJourneyId = null;
            return "iOS refused to start the activity.";
        }

        // The push token arrives asynchronously through the bridge callback, and
        // that callback is what registers it with the worker. Give it a moment
        // before reporting, so the answer reflects what actually happened.
        await Task.Delay(TimeSpan.FromSeconds(3));

        return LiveActivityBridge.HasPush
            ? $"Card started ({journeyId}). Registering its push token with the worker."
            : $"Card started ({journeyId}), but iOS issued no push token, so the worker "
                + "cannot update it. Check the Push Notifications capability.";
    }

    /// <summary>Take the test card away and hand control back to real journeys.</summary>
    public string EndTestCard()
    {
        if (!_testCard)
        {
            return "No test card is running.";
        }

        LiveActivityBridge.End(Journey.Idle, null, Latest, _clock.Now);
        _testCard = false;
        _activityJourneyId = null;
        return "Test card ended.";
    }

    /// <summary>Import the bundle exported from the Home Assistant integration.</summary>
    /// <remarks>
    /// Predictions work from two route samples and three arrivals, so a fortnight
    /// of this makes the app useful on day one rather than in October.
    /// </remarks>
    public async Task<int> ImportHistoryAsync(string json)
    {
        int imported = 0;

        foreach (ImportedRider rider in HistoryImport.Parse(json))
        {
            if (long.TryParse(rider.ChildId, out long childId))
            {
                _engine.LoadHistory(childId, rider.Arrivals);
                imported += rider.Arrivals.Count;
            }
        }

        if (imported > 0)
        {
            await SaveHistoryAsync();
        }

        // What is HELD after merging, not what arrived: an arrival already known
        // is merged, not added, and reporting the raw count would overstate it.
        return _students.Keys.Sum(childId => _engine.ArrivalsFor(childId).Count) is int held and > 0
            ? held
            : imported;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        MainThread.BeginInvokeOnMainThread(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }
}
