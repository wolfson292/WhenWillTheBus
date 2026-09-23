// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;

namespace WhenWillTheBus.Core.Tests;

/// <summary>
/// Which time the screen leads with.
/// </summary>
/// <remarks>
/// Extracted from the page and given tests because the same fault reached a
/// phone twice: a live morning journey showing a countdown to the AFTERNOON,
/// because the prediction had already rolled to the next run.
/// </remarks>
public sealed class HeadlineTests
{
    private static readonly DateTimeOffset MorningStop = EngineFixtures.LocalAt(2026, 9, 23, 8, 7);
    private static readonly DateTimeOffset School = EngineFixtures.LocalAt(2026, 9, 23, 9, 25);
    private static readonly DateTimeOffset Afternoon = EngineFixtures.LocalAt(2026, 9, 23, 17, 21);

    private static Journey At(JourneyStage stage, DateTimeOffset? target = null) =>
        new() { Stage = stage, Target = target };

    /// <summary>
    /// THE REPORTED FAULT, 23 Sep. The bus was at the stop at 08:07 and the
    /// screen read "5:21 PM, in 9h 13m" — because the engine had already
    /// counted the morning arrival and moved on to the afternoon.
    /// </summary>
    [Fact]
    public void TheBusBeingAtTheStopIsNotACountdownToTheAfternoon()
    {
        Headline headline = Headline.For(At(JourneyStage.AtStop), Afternoon);

        Assert.Equal(HeadlineKind.Now, headline.Kind);
        Assert.Null(headline.Moment);
    }

    [Fact]
    public void RidingToSchoolCountsTowardsSchool()
    {
        Headline headline = Headline.For(At(JourneyStage.ToSchool, School), Afternoon);

        Assert.Equal(HeadlineKind.Time, headline.Kind);
        Assert.Equal(School, headline.Moment);
    }

    /// <summary>
    /// With nothing learned about where the ride ends there is no number. The
    /// afternoon pickup is not a substitute for one.
    /// </summary>
    [Fact]
    public void ARideWithNoLearnedEndShowsNoTimeAtAll()
    {
        Headline headline = Headline.For(At(JourneyStage.ToSchool), Afternoon);

        Assert.Equal(HeadlineKind.None, headline.Kind);
        Assert.Null(headline.Moment);
    }

    [Fact]
    public void RidingHomeCountsTowardsTheStop()
    {
        Headline headline = Headline.For(At(JourneyStage.FromSchool, Afternoon), Afternoon);

        Assert.Equal(HeadlineKind.Time, headline.Kind);
        Assert.Equal(Afternoon, headline.Moment);
    }

    /// <summary>
    /// Waiting for the bus is the one case where the next arrival at the stop
    /// IS the question being asked.
    /// </summary>
    [Fact]
    public void WaitingCountsTowardsTheNextArrival()
    {
        Assert.Equal(
            new Headline(HeadlineKind.Time, MorningStop),
            Headline.For(At(JourneyStage.ToStop, MorningStop), MorningStop));

        Assert.Equal(
            new Headline(HeadlineKind.Time, Afternoon),
            Headline.For(Journey.Idle, Afternoon));
    }

    /// <summary>
    /// Once the journey is OVER the next run is a real answer, not a borrowed
    /// one: at school in the morning, the next bus really is the afternoon's.
    /// </summary>
    [Fact]
    public void AFinishedJourneyMayLookAheadToTheNextRun()
    {
        Assert.Equal(
            new Headline(HeadlineKind.Time, Afternoon),
            Headline.For(At(JourneyStage.AtSchool), Afternoon));

        Assert.Equal(
            new Headline(HeadlineKind.Time, Afternoon),
            Headline.For(At(JourneyStage.Home), Afternoon));
    }

    [Fact]
    public void WithNoPredictionAtAllThereIsNothingToShow()
    {
        Assert.Equal(HeadlineKind.None, Headline.For(Journey.Idle, null).Kind);
        Assert.Equal(HeadlineKind.None, Headline.For(At(JourneyStage.AtSchool), null).Kind);
    }
}
