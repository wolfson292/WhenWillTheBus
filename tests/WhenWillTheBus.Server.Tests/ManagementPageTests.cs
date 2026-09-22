// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;
using WhenWillTheBus.Server.Api;
using WhenWillTheBus.Server.Devices;

namespace WhenWillTheBus.Server.Tests;

public sealed class ManagementPageTests
{
    private static readonly LocalClock Clock = new(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static string Render(
        IReadOnlyList<ClientIdentity>? clients = null,
        IReadOnlyCollection<RegisteredActivity>? activities = null,
        Student? student = null)
    {
        Dictionary<long, Student> students = [];
        if (student is not null)
        {
            students[student.ChildId] = student;
        }

        return ManagementPage.Render(
            students,
            new PredictionEngine(Clock),
            clients ?? [],
            activities ?? [],
            Clock,
            Now.AddHours(-3),
            Now);
    }

    private static ClientIdentity Phone(string? label) =>
        new("VENDOR-1", label, "iPhone17,1", "26.0", "1.0", "202609221211", false, Now.AddDays(-2), Now, 7);

    /// <summary>
    /// A LABEL IS UNTRUSTED INPUT however friendly its source: it is typed on
    /// somebody's phone, sent over the network, and rendered into the page an
    /// administrator reads while signed in. That is the whole shape of a stored
    /// cross-site scripting bug.
    /// </summary>
    [Fact]
    public void APhoneCannotPutScriptIntoThePage()
    {
        string html = Render([Phone("<script>alert(1)</script>")]);

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApostropheInANameIsNotMangled()
    {
        // Every phone in this house is named this way, so it had better survive.
        string html = Render([Phone("Angela's iPhone")]);

        Assert.Contains("iPhone", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnnamedPhoneStillAppears()
    {
        string html = Render([Phone(null)]);

        Assert.Contains("unnamed", html, StringComparison.Ordinal);
        Assert.Contains("iPhone17,1", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The identifier is shortened for display. Rendering the whole vendor UUID
    /// would put a device identifier on a page and in any screenshot of it, to
    /// answer a question the first few characters already answer.
    /// </summary>
    [Fact]
    public void TheVendorIdentifierIsNotShownInFull()
    {
        string html = Render([Phone("Angela's iPhone") with { Id = "0123456789ABCDEF-FULL-UUID" }]);

        Assert.DoesNotContain("0123456789ABCDEF-FULL-UUID", html, StringComparison.Ordinal);
        Assert.Contains("01234567", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyWorkerSaysSoRatherThanRenderingNothing()
    {
        string html = Render();

        Assert.Contains("No roster yet", html, StringComparison.Ordinal);
        Assert.Contains("No phone has introduced itself", html, StringComparison.Ordinal);
        Assert.Contains("None registered", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page knows where a child is and must never say so. /status has the
    /// same rule for the same reason; this one is likelier to be left open on a
    /// laptop.
    /// </summary>
    [Fact]
    public void NoCoordinatesReachThePage()
    {
        Student student = new()
        {
            ChildId = 42,
            Name = "Rider",
            BusNumber = "2563",
            SchoolName = "Example Middle",
            StopAddress = "PINE RD & BEAULIEU CT",
            AmScheduled = new TimeOnly(7, 56),
            PmScheduled = new TimeOnly(17, 48),
        };

        string html = Render(student: student);

        Assert.Contains("Rider", html, StringComparison.Ordinal);
        Assert.DoesNotContain("latitude", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("longitude", html, StringComparison.OrdinalIgnoreCase);

        // A degree value would look like this, and nothing on the page should.
        Assert.DoesNotContain("26.5", html, StringComparison.Ordinal);
        Assert.DoesNotContain("-81.8", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A household runs both environments at once, and a card pushed to the
    /// wrong host fails silently -- so which is which has to be visible.
    /// </summary>
    [Fact]
    public void ThePushEnvironmentIsShownForEveryCard()
    {
        string html = Render(
            [Phone("Scott's iPhone") with { Sandbox = true }],
            [new RegisteredActivity("20260922-am", 42, "TOKEN", Now.AddMinutes(-5), Sandbox: false)]);

        Assert.Contains("sandbox", html, StringComparison.Ordinal);
        Assert.Contains("production", html, StringComparison.Ordinal);
        Assert.Contains("20260922-am", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageIsSelfContained()
    {
        string html = Render([Phone("Angela's iPhone")]);

        // No CDN, no font service, nothing that tells a third party when a page
        // about a child's whereabouts was opened.
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }
}
