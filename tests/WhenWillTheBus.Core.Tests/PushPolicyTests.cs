// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Notifications;

namespace WhenWillTheBus.Core.Tests;

public sealed class PushPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 7, 50, 0, TimeSpan.FromHours(-4));
    private static readonly DateTimeOffset Target = new(2026, 9, 17, 8, 1, 0, TimeSpan.FromHours(-4));

    private static Journey Approaching(DateTimeOffset? target = null, JourneyStage stage = JourneyStage.ToStop) =>
        new() { Stage = stage, Target = target ?? Target, Progress = 40, JourneyId = "20260917-am" };

    private static PublishedActivity Published(
        JourneyStage stage = JourneyStage.ToStop,
        DateTimeOffset? target = null,
        int secondsAgo = 30) =>
        new(stage, target ?? Target, Now.AddSeconds(-secondsAgo));

    /// <summary>The usual answer, and the one that keeps the wrist quiet.</summary>
    [Fact]
    public void NothingHasChanged_SoNothingIsPushed() =>
        Assert.Equal(PushDecision.None, PushPolicy.Decide(Published(), Approaching(), Now));

    /// <summary>§9. Only a stage change may interrupt.</summary>
    [Fact]
    public void StageChange_IsTheOnlyThingWorthABuzz()
    {
        PushDecision decision = PushPolicy.Decide(
            Published(JourneyStage.ToStop), Approaching(stage: JourneyStage.AtStop), Now);

        Assert.Equal(PushDecision.TimeSensitive, decision);
    }

    /// <summary>
    /// §9. Round the target to the minute BEFORE using it as a re-push trigger.
    /// Raw, it carries sub-second precision and moves every poll, so watching it
    /// pushes every thirty seconds to say the same thing.
    /// </summary>
    [Fact]
    public void TargetDriftingWithinTheSameDisplayedMinute_IsNotNews()
    {
        PushDecision decision = PushPolicy.Decide(
            Published(target: Target),
            Approaching(Target.AddSeconds(20)),
            Now);

        Assert.Equal(PushDecision.None, decision);
    }

    /// <summary>
    /// §9, the other half. The chronometer ticks on the phone without a push, so
    /// a target that really moved must be said — otherwise the card counts down
    /// to the old time and is confidently wrong at the worst moment.
    /// </summary>
    [Fact]
    public void TargetMovingToADifferentMinute_IsPushedQuietly()
    {
        PushDecision decision = PushPolicy.Decide(
            Published(target: Target),
            Approaching(Target.AddMinutes(3)),
            Now);

        Assert.Equal(PushDecision.Passive, decision);
    }

    /// <summary>A card may not sit indefinitely without an update.</summary>
    [Fact]
    public void Backstop_EventuallyRefreshesAQuietCard()
    {
        PushDecision decision = PushPolicy.Decide(
            Published(secondsAgo: (int)PushPolicy.Backstop.TotalSeconds + 1),
            Approaching(),
            Now);

        Assert.Equal(PushDecision.Passive, decision);
    }

    /// <summary>The backstop must not fire early, or it becomes the push storm it prevents.</summary>
    [Fact]
    public void Backstop_DoesNotFireEarly()
    {
        PushDecision decision = PushPolicy.Decide(
            Published(secondsAgo: (int)PushPolicy.Backstop.TotalSeconds - 30),
            Approaching(),
            Now);

        Assert.Equal(PushDecision.None, decision);
    }

    /// <summary>
    /// The first server-side update is a quiet confirmation: the app started the
    /// activity locally and it already shows this.
    /// </summary>
    [Fact]
    public void FirstUpdate_DoesNotBuzz() =>
        Assert.Equal(PushDecision.Passive, PushPolicy.Decide(null, Approaching(), Now));

    /// <summary>
    /// The stage machine has already held the finished card for its dwell, so
    /// reaching idle is the signal to clear it.
    /// </summary>
    [Fact]
    public void OnceTheJourneyIsOver_TheCardIsEnded() =>
        Assert.Equal(PushDecision.End, PushPolicy.Decide(Published(), Journey.Idle, Now));

    /// <summary>Nothing was ever shown, so there is nothing to take away.</summary>
    [Fact]
    public void AnIdleDay_NeverStartsAnything() =>
        Assert.Equal(PushDecision.None, PushPolicy.Decide(null, Journey.Idle, Now));

    /// <summary>
    /// §7. Lead with the TIME, not the distance — a watch truncates hard, and the
    /// distance is already drawn as the progress bar.
    /// </summary>
    [Fact]
    public void Alert_LeadsWithTheTime()
    {
        (string Title, string Body)? alert = PushPolicy.AlertFor(Approaching(), "Robin", EngineFixtures.Clock);

        Assert.NotNull(alert);
        Assert.Contains("8:01", alert.Value.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("mile", alert.Value.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Alert_HasNothingToSayAboutAnIdleDay() =>
        Assert.Null(PushPolicy.AlertFor(Journey.Idle, "Robin", EngineFixtures.Clock));
}
