// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Net;
using System.Text;
using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Api;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Server.Devices;

namespace WhenWillTheBus.Server.Api;

/// <summary>
/// The worker's own status page.
/// </summary>
/// <remarks>
/// Everything here was previously answerable only by opening a shell on the
/// Docker host and reading JSON — which meant that in practice nobody answered
/// it, and a worker serving stale predictions looked exactly like one working
/// perfectly.
///
/// NO COORDINATES, for the same reason /status has none: this page knows where
/// a child is, and a page left open on a laptop is a different risk from an
/// endpoint behind a bearer token. What it shows is what a parent could see on
/// the app anyway, plus which phones are connected.
///
/// Rendered as one self-contained document with no external stylesheet, script
/// or font. Partly so it works on the LAN with no internet, and partly because
/// a page about a child's whereabouts should not be telling a CDN each time it
/// is opened.
/// </remarks>
public static class ManagementPage
{
    /// <summary>How often the page reloads itself, matched to the watching poll.</summary>
    private const int RefreshSeconds = 30;

    public static string Render(
        IReadOnlyDictionary<long, Student> students,
        PredictionEngine engine,
        IReadOnlyList<ClientIdentity> clients,
        IReadOnlyCollection<RegisteredActivity> activities,
        LocalClock clock,
        DateTimeOffset started,
        DateTimeOffset now)
    {
        StringBuilder html = new();

        html.Append(CultureInfo.InvariantCulture, $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="refresh" content="{RefreshSeconds}">
            <title>When Will The Bus — worker</title>
            {Style}
            </head>
            <body>
            <h1>When Will The Bus <span class="sub">worker</span></h1>
            <p class="meta">
              {Escape(clock.ToLocal(now).ToString("dddd d MMMM, HH:mm:ss", CultureInfo.InvariantCulture))}
              · up {Escape(Duration(now - started))}
              · refreshes every {RefreshSeconds}s
            </p>
            """);

        Riders(html, students, engine, clock, now);
        Clients(html, clients, activities, now);
        Activities(html, activities, clients, clock, now);

        html.Append("""
            <p class="foot">No coordinates are shown on this page, deliberately.</p>
            </body>
            </html>
            """);

        return html.ToString();
    }

    private static void Riders(
        StringBuilder html,
        IReadOnlyDictionary<long, Student> students,
        PredictionEngine engine,
        LocalClock clock,
        DateTimeOffset now)
    {
        html.Append("<h2>Riders</h2>");

        if (students.Count == 0)
        {
            html.Append("""<p class="empty">No roster yet. The worker signs in and loads it on its first poll.</p>""");
            return;
        }

        foreach (Student student in students.Values)
        {
            ArrivalPrediction? prediction = engine.PredictNextArrival(student, now);
            SchoolArrival? school = SchoolArrivalPredictor.Predict(student, now, clock);
            Journey journey = engine.Stage(student, now, prediction, school?.Arrival);
            RiderInfo? info = engine.LatestFor(student.ChildId);

            html.Append(CultureInfo.InvariantCulture, $"""
                <section class="card">
                <h3>{Escape(student.Name)} <span class="tag">{Escape(Stage(journey.Stage))}</span></h3>
                <dl>
                """);

            Row(html, "Next", prediction is null
                ? "nothing scheduled"
                : $"{Time(prediction.Arrival, clock)} · {Escape(prediction.Run.ToString().ToUpperInvariant())} "
                  + $"· {Escape(prediction.Basis.ToString().ToLowerInvariant())} from {prediction.Samples} sample(s)");

            if (journey.Target is DateTimeOffset target)
            {
                Row(html, "Journey ends", $"{Time(target, clock)}"
                    + (journey.Progress is int done ? $" · {done}% through" : string.Empty));
            }
            else if (journey.Active)
            {
                Row(html, "Journey ends", "<span class=\"warn\">not yet learned</span>");
            }

            Row(html, "School run ends", school is null
                ? "<span class=\"warn\">no drop-off scans recorded yet</span>"
                : $"{Time(school.Arrival, clock)} · from {school.Samples} scan(s)"
                  + (school.RideMinutes is int ride ? $" · about a {ride} min ride" : string.Empty));

            Row(html, "Learned arrivals", engine.ArrivalsFor(student.ChildId).Count.ToString(CultureInfo.InvariantCulture));

            Row(html, "Bus", info is null
                ? "<span class=\"warn\">no reading yet</span>"
                : $"{Escape(student.BusNumber ?? "?")} · {Escape(info.Status.ToString().ToLowerInvariant())}"
                  + (info.DistanceMiles is double miles
                      ? $" · {miles.ToString("F1", CultureInfo.InvariantCulture)} mi from the stop"
                      : string.Empty)
                  + (info.GpsAgeMinutes is int age ? $" · fix {age} min old" : string.Empty));

            Row(html, "Timetable",
                $"AM {Escape(student.AmScheduled?.ToString("h:mm tt", CultureInfo.InvariantCulture) ?? "—")}"
                + $" · PM {Escape(student.PmScheduled?.ToString("h:mm tt", CultureInfo.InvariantCulture) ?? "—")}");

            ScanEvent[] today = [.. student.Scans.Where(scan => clock.SameDay(scan.Timestamp, now))];
            Row(html, "Scans today", today.Length == 0
                ? "none"
                : string.Join(" · ", today.Select(scan =>
                    $"{Escape(scan.Kind.ToString().ToLowerInvariant())} {Time(scan.Timestamp, clock)}")));

            html.Append("</dl></section>");
        }
    }

    private static void Clients(
        StringBuilder html,
        IReadOnlyList<ClientIdentity> clients,
        IReadOnlyCollection<RegisteredActivity> activities,
        DateTimeOffset now)
    {
        html.Append("<h2>Phones</h2>");

        if (clients.Count == 0)
        {
            html.Append("""
                <p class="empty">No phone has introduced itself yet. A phone says hello when the
                app opens with a worker configured.</p>
                """);
            return;
        }

        html.Append("""
            <table>
            <thead><tr><th>Phone</th><th>Model</th><th>iOS</th><th>App</th>
            <th>Push</th><th>Cards</th><th>Last seen</th></tr></thead>
            <tbody>
            """);

        foreach (ClientIdentity client in clients)
        {
            // Stale means "has not called in a while", and the page should say
            // so rather than leave a row looking as live as the others.
            bool stale = now - client.LastSeen > TimeSpan.FromHours(24);
            int cards = activities.Count(activity => activity.Sandbox == client.Sandbox);

            html.Append(CultureInfo.InvariantCulture, $"""
                <tr class="{(stale ? "stale" : string.Empty)}">
                <td>{Escape(client.Label ?? "unnamed")}<br><span class="id">{Escape(ClientRegistry.Short(client.Id))}</span></td>
                <td>{Escape(client.Model ?? "—")}</td>
                <td>{Escape(client.SystemVersion ?? "—")}</td>
                <td>{Escape(client.AppVersion ?? "—")}{(client.Build is null ? string.Empty : Escape($" ({client.Build})"))}</td>
                <td>{(client.Sandbox ? "sandbox" : "production")}</td>
                <td>{cards}</td>
                <td>{Escape(Ago(now - client.LastSeen))}</td>
                </tr>
                """);
        }

        html.Append("</tbody></table>");
    }

    private static void Activities(
        StringBuilder html,
        IReadOnlyCollection<RegisteredActivity> activities,
        IReadOnlyList<ClientIdentity> clients,
        LocalClock clock,
        DateTimeOffset now)
    {
        html.Append("<h2>Live Activity cards</h2>");

        if (activities.Count == 0)
        {
            html.Append("""
                <p class="empty">None registered. A card is started by the phone, not by the worker,
                so there is nothing here outside a journey.</p>
                """);
            return;
        }

        html.Append("""
            <table>
            <thead><tr><th>Journey</th><th>Child</th><th>Push</th><th>Registered</th></tr></thead>
            <tbody>
            """);

        foreach (RegisteredActivity activity in activities.OrderByDescending(a => a.RegisteredAt))
        {
            html.Append(CultureInfo.InvariantCulture, $"""
                <tr>
                <td>{Escape(activity.JourneyId)}</td>
                <td>{activity.ChildId}</td>
                <td>{(activity.Sandbox ? "sandbox" : "production")}</td>
                <td>{Escape(Ago(now - activity.RegisteredAt))} ({Time(activity.RegisteredAt, clock)})</td>
                </tr>
                """);
        }

        html.Append("</tbody></table>");
    }

    private static void Row(StringBuilder html, string name, string value) =>
        html.Append(CultureInfo.InvariantCulture, $"<dt>{Escape(name)}</dt><dd>{value}</dd>");

    private static string Time(DateTimeOffset moment, LocalClock clock) =>
        Escape(clock.ToLocal(moment).ToString("h:mm tt", CultureInfo.InvariantCulture));

    private static string Stage(JourneyStage stage) => stage switch
    {
        JourneyStage.ToStop => "bus on the way",
        JourneyStage.AtStop => "bus at the stop",
        JourneyStage.ToSchool => "riding to school",
        JourneyStage.AtSchool => "at school",
        JourneyStage.FromSchool => "riding home",
        JourneyStage.Home => "home",
        _ => "idle",
    };

    private static string Duration(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays}d {span.Hours}h",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours}h {span.Minutes}m",
        _ => $"{(int)span.TotalMinutes}m",
    };

    private static string Ago(TimeSpan span) => span switch
    {
        { TotalSeconds: < 90 } => "just now",
        { TotalMinutes: < 60 } => $"{(int)span.TotalMinutes} min ago",
        { TotalHours: < 24 } => $"{(int)span.TotalHours}h ago",
        _ => $"{(int)span.TotalDays}d ago",
    };

    /// <summary>
    /// Escape anything going into the document.
    /// </summary>
    /// <remarks>
    /// A phone's label is typed by whoever owns the phone and is rendered here,
    /// which makes it untrusted input however friendly its source — and this is
    /// the page an administrator reads while signed in.
    /// </remarks>
    private static string Escape(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private const string Style = """
        <style>
        :root {
          color-scheme: light dark;
          --bg: #ffffff; --fg: #1a1a1a; --dim: #666; --line: #e3e3e3;
          --card: #fafafa; --warn: #a15c00; --tag: #e8eef7;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #16181c; --fg: #e8e8e8; --dim: #9aa0a6; --line: #2c2f36;
            --card: #1d2025; --warn: #e0a458; --tag: #24303f;
          }
        }
        * { box-sizing: border-box; }
        body {
          margin: 0 auto; padding: 24px 16px 48px; max-width: 900px;
          background: var(--bg); color: var(--fg);
          font: 15px/1.5 -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif;
        }
        h1 { font-size: 22px; margin: 0 0 2px; }
        h1 .sub { color: var(--dim); font-weight: 400; }
        h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .08em;
             color: var(--dim); margin: 32px 0 10px; }
        h3 { font-size: 17px; margin: 0 0 10px; }
        .meta, .foot { color: var(--dim); font-size: 13px; margin: 0 0 4px; }
        .foot { margin-top: 32px; }
        .empty { color: var(--dim); background: var(--card);
                 border: 1px solid var(--line); border-radius: 8px; padding: 12px 14px; }
        .card { background: var(--card); border: 1px solid var(--line);
                border-radius: 10px; padding: 14px 16px; margin-bottom: 12px; }
        .tag { font-size: 12px; font-weight: 500; color: var(--fg); background: var(--tag);
               border-radius: 999px; padding: 3px 10px; vertical-align: 2px; }
        dl { display: grid; grid-template-columns: 150px 1fr; gap: 6px 14px; margin: 0; }
        dt { color: var(--dim); }
        dd { margin: 0; }
        .warn { color: var(--warn); }
        table { width: 100%; border-collapse: collapse; font-size: 14px; }
        th { text-align: left; font-weight: 500; color: var(--dim); font-size: 12px;
             text-transform: uppercase; letter-spacing: .05em; }
        th, td { padding: 9px 10px; border-bottom: 1px solid var(--line); vertical-align: top; }
        tr.stale td { color: var(--dim); }
        .id { color: var(--dim); font-size: 12px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
        @media (max-width: 560px) {
          dl { grid-template-columns: 1fr; gap: 2px; }
          dt { margin-top: 6px; }
          table { font-size: 13px; }
          th, td { padding: 7px 6px; }
        }
        </style>
        """;
}
