// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.App;

/// <summary>
/// The one place a colour is decided.
/// </summary>
/// <remarks>
/// Two rules hold this together, and everything else follows from them.
///
/// ONE COLOUR MEANS ONE THING. Yellow is the bus and nothing else — never a
/// background, never a heading, never decoration. Blue means far off or weakly
/// known, green means the rider is accounted for, coral means something is
/// broken. A parent glancing for four seconds reads the colour before the words,
/// so a colour spent on ornament is a colour that can no longer carry a fact.
///
/// THE INTERFACE WARMS AS THE BUS CLOSES IN. <see cref="For"/> is the whole of
/// that rule. Nothing flashes, nothing pulses, nothing animates: a bus tracker
/// that demands attention every time it recalculates teaches people to stop
/// looking at it.
///
/// Dark only, for now. The palette below has light-mode values designed for it
/// (see the canvas), but wiring them means an AppThemeBinding on every single
/// property, and half-done that reads as a bug rather than a theme.
/// </remarks>
public static class Theme
{
    public static readonly Color Ink900 = Color.FromArgb("#0E1117");
    public static readonly Color Ink800 = Color.FromArgb("#151A23");
    public static readonly Color Ink700 = Color.FromArgb("#1C232E");
    public static readonly Color Ink600 = Color.FromArgb("#2E3746");

    public static readonly Color Text = Color.FromArgb("#EDF1F7");
    public static readonly Color TextMid = Color.FromArgb("#AEB9C9");

    /// <summary>Captions. 6.3:1 on <see cref="Ink900"/>, so it clears AA at 13px.</summary>
    public static readonly Color TextDim = Color.FromArgb("#8A96A9");

    /// <summary>School-bus yellow. The bus, and only the bus.</summary>
    public static readonly Color Bus = Color.FromArgb("#F6C445");

    /// <summary>Far off, or resting on nothing better than a timetable.</summary>
    public static readonly Color Far = Color.FromArgb("#6E96C9");

    /// <summary>Scanned on, at school, home. The resolved states.</summary>
    public static readonly Color Aboard = Color.FromArgb("#3FB98A");

    /// <summary>The worker is unreachable, or the bus stopped reporting.</summary>
    public static readonly Color Fault = Color.FromArgb("#FF7A6B");

    /// <summary>Inside this, an arrival is imminent enough to warm the interface.</summary>
    private static readonly TimeSpan Soon = TimeSpan.FromMinutes(10);

    /// <summary>The colour this moment in the journey is entitled to.</summary>
    public static Color For(JourneyStage stage, DateTimeOffset? target, DateTimeOffset now) => stage switch
    {
        // The rider is accounted for. Nothing about this is urgent, whatever the
        // clock says.
        JourneyStage.ToSchool or JourneyStage.AtSchool or JourneyStage.FromSchool or JourneyStage.Home => Aboard,

        JourneyStage.AtStop => Bus,

        // The only place the ramp actually ramps.
        JourneyStage.ToStop => target is { } due && due - now <= Soon ? Bus : Far,

        _ => Far,
    };

    /// <summary>
    /// How much the estimate is entitled to claim, as a count out of four.
    /// </summary>
    /// <remarks>
    /// This is the visual form of <see cref="PredictionBasis"/>, which exists so
    /// the app never sounds more certain than it is. The published afternoon time
    /// here reads nearly half an hour later than the bus actually comes, so a
    /// timetable guess drawn like a measurement is the failure the whole
    /// prediction engine is built to avoid.
    /// </remarks>
    public static (int Filled, Color Colour, string Label) Confidence(PredictionBasis? basis) => basis switch
    {
        PredictionBasis.Route => (4, Bus, "on route"),
        PredictionBasis.Approach => (3, Bus, "estimated"),
        PredictionBasis.Historical => (2, Far, "usual time"),
        PredictionBasis.Scheduled => (1, Far, "timetable only"),
        _ => (0, Far, "nothing yet"),
    };
}
