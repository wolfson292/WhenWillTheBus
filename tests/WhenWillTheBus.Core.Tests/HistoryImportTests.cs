// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Storage;

namespace WhenWillTheBus.Core.Tests;

public sealed class HistoryImportTests
{
    /// <summary>A bundle in the shape §13 documents.</summary>
    private static string Bundle(
        string units = "miles",
        string ladder = "[3.0, 2.0, 1.0, 0.5]",
        string legs = """{"0": 570, "1": 420, "2": 240, "3": 95}""") => $$"""
        {
          "export_version": 1,
          "exported_at": "2026-09-18T07:40:00-04:00",
          "source": "home-assistant custom_components/wheresthebus",
          "arrival_schema": 7,
          "units": "{{units}}",
          "anchor_ladder": {{ladder}},
          "track_point_format": ["seconds_before_arrival", "latitude", "longitude"],
          "riders": [
            {
              "child_id": "9911",
              "summary": { "am": { "arrivals": 2, "with_track": 2 } },
              "arrivals": [
                {
                  "run": "am",
                  "arrival": "2026-09-17T12:04:34+00:00",
                  "closest": 0.04,
                  "legs": {{legs}},
                  "substitute": false,
                  "track": [[1200, 40.73501, -74.02003], [1170, 40.73381, -74.01913]],
                  "recedes": 0, "stale": 2,
                  "boarded": null, "replayed": false
                },
                {
                  "run": "pm",
                  "arrival": "2026-09-17T21:20:00+00:00",
                  "closest": 0.08,
                  "legs": {},
                  "substitute": true,
                  "track": [],
                  "recedes": 1, "stale": 0,
                  "boarded": "2026-09-17T20:35:00+00:00", "replayed": true
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsTheBundleShape()
    {
        IReadOnlyList<ImportedRider> riders = HistoryImport.Parse(Bundle());

        ImportedRider rider = Assert.Single(riders);
        Assert.Equal("9911", rider.ChildId);
        Assert.Equal(2, rider.Arrivals.Count);

        RunArrival morning = rider.Arrivals[0];
        Assert.Equal(Run.Am, morning.Run);
        Assert.Equal(0.04, morning.ClosestMiles, precision: 6);
        Assert.Equal(2, morning.Track.Count);
        Assert.Equal(2, morning.Stale);
        Assert.False(morning.Substitute);
        Assert.Null(morning.Boarded);
    }

    [Fact]
    public void Parse_CarriesTheFieldsTheEstimateLearnsFrom()
    {
        RunArrival afternoon = HistoryImport.Parse(Bundle())[0].Arrivals[1];

        Assert.Equal(Run.Pm, afternoon.Run);
        Assert.True(afternoon.Substitute);
        Assert.Equal(1, afternoon.Recedes);
        Assert.True(afternoon.Replayed);
        Assert.NotNull(afternoon.Boarded);
    }

    /// <summary>The track is the most valuable field in the bundle: oldest first.</summary>
    [Fact]
    public void Parse_OrdersTrackPointsOldestFirst()
    {
        RunArrival morning = HistoryImport.Parse(Bundle())[0].Arrivals[0];

        Assert.Equal(1200, morning.Track[0].SecondsBeforeArrival);
        Assert.Equal(1170, morning.Track[1].SecondsBeforeArrival);
    }

    [Fact]
    public void Parse_MapsLegsOntoTheLadderTheyWereRecordedAgainst()
    {
        RunArrival morning = HistoryImport.Parse(Bundle())[0].Arrivals[0];

        Assert.Equal(570, morning.Legs[0]);   // the 3.0 mi rung
        Assert.Equal(95, morning.Legs[3]);    // the 0.5 mi rung
    }

    /// <summary>
    /// Leg keys are indices into the EXPORT'S ladder, not into ours. A bundle
    /// written against a ladder in a different order must still land each leg on
    /// the rung it was actually measured at — otherwise a three-mile leg is
    /// silently relabelled as a half-mile one, and every anchored estimate built
    /// on it is wrong by the difference.
    /// </summary>
    [Fact]
    public void Parse_RemapsLegsByDistance_NotByIndex()
    {
        // The same four rungs, written smallest first.
        string reversed = HistoryImport.Parse(
            Bundle(ladder: "[0.5, 1.0, 2.0, 3.0]", legs: """{"0": 95, "1": 240, "2": 420, "3": 570}"""))
            [0].Arrivals[0].Legs is { } legs
                ? $"{legs[0]}/{legs[3]}"
                : "missing";

        // Our ladder is 3.0, 2.0, 1.0, 0.5 — so the 570-second leg belongs at
        // index 0 and the 95-second one at index 3, exactly as before.
        Assert.Equal("570/95", reversed);
    }

    /// <summary>A rung this build does not have is dropped, not guessed at.</summary>
    [Fact]
    public void Parse_DropsRungsThisBuildDoesNotHave()
    {
        RunArrival morning = HistoryImport.Parse(
            Bundle(ladder: "[5.0, 3.0]", legs: """{"0": 900, "1": 570}"""))
            [0].Arrivals[0];

        Assert.Equal(570, morning.Legs[0]);
        Assert.Single(morning.Legs);

        // The track survives regardless, which is what the route basis needs.
        Assert.Equal(2, morning.Track.Count);
    }

    /// <summary>A kilometre account's export is in kilometres; everything here works in miles.</summary>
    [Fact]
    public void Parse_ConvertsAKilometreBundleToMiles()
    {
        RunArrival morning = HistoryImport.Parse(
            Bundle(units: "km", ladder: "[4.8, 3.2, 1.6, 0.8]"))
            [0].Arrivals[0];

        // 0.04 km is about 0.025 miles.
        Assert.Equal(0.0249, morning.ClosestMiles, precision: 3);

        // 4.8 km is 2.98 miles, which is our 3.0 rung within tolerance.
        Assert.Equal(570, morning.Legs[0]);
    }

    [Fact]
    public void Parse_RejectsSomethingThatIsNotAnExport()
    {
        Assert.Throws<InvalidDataException>(() => HistoryImport.Parse("""{"hello": "world"}"""));
        Assert.Throws<InvalidDataException>(() => HistoryImport.Parse("not json at all"));
    }

    /// <summary>An arrival without a usable timestamp is skipped, not fatal.</summary>
    [Fact]
    public void Parse_SkipsAnArrivalItCannotDate()
    {
        const string Broken = """
            {"riders": [{"child_id": "1", "arrivals": [
              {"run": "am", "arrival": "not a date"},
              {"run": "am", "arrival": "2026-09-17T12:04:34+00:00", "closest": 0.1}
            ]}]}
            """;

        Assert.Single(HistoryImport.Parse(Broken)[0].Arrivals);
    }
}
