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
    private DateTimeOffset _scansPolledAt = DateTimeOffset.MinValue;

    private Student? _rider;
    private ArrivalPrediction? _prediction;
    private Journey _journey = Journey.Idle;
    private RiderInfo? _latest;
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
    public async Task StartAsync()
    {
        WheresTheBusCredentials? credentials = await _credentials.ReadAsync();
        if (credentials is null)
        {
            Problem = "Sign in to see the bus.";
            return;
        }

        _client ??= new WheresTheBusClient(_http, credentials);

        await LoadHistoryAsync();

        LiveActivityBridge.OnPushToken(async token =>
        {
            if (_activityJourneyId is string journeyId && Rider is not null)
            {
                await _server.RegisterAsync(journeyId, Rider.ChildId, token);
            }
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
        Journey journey = _engine.Stage(rider, now, prediction, schoolArrival: null);

        Latest = info;
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

        return imported;
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
