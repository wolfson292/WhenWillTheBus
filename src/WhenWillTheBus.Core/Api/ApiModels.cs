// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using WhenWillTheBus.Core.Model;
using WhenWillTheBus.Core.Prediction;

namespace WhenWillTheBus.Core.Api;

/// <summary>
/// The account's credentials.
/// </summary>
/// <param name="DeviceId">
/// A stable per-install identifier you invent, sent as <c>imeiNo</c>. It must
/// not change between launches or the server treats every start as a new device.
/// </param>
/// <remarks>
/// THE PASSWORD BELONGS IN THE KEYCHAIN on device and in a secret manager
/// server-side — never in source, and never in a config file in a repository.
/// A server component holding this also knows a child's live location.
/// </remarks>
public sealed record WheresTheBusCredentials(string Email, string Password, string DeviceId);

/// <summary>What a successful login hands back.</summary>
/// <param name="BasePath">
/// The per-account shard. EVERY later call uses this, not the front door.
/// </param>
public sealed record LoginResult(
    string SessionId,
    string BasePath,
    string? ShardId,
    string? FirstName,
    string? LastName);

/// <summary>Live position and stop distance for one child.</summary>
public sealed record RiderInfo
{
    /// <summary>Where the bus is — or, when the fix is stale, where it WAS.</summary>
    public GeoPoint? Bus { get; init; }

    /// <summary>Distance from the bus to the rider's stop, converted to MILES.</summary>
    public double? DistanceMiles { get; init; }

    /// <summary>The rider's stop.</summary>
    public GeoPoint? Stop { get; init; }

    public GeoPoint? Home { get; init; }

    public GeoPoint? School { get; init; }

    /// <summary>The raw <c>stsMsg</c>, kept for display whatever it says.</summary>
    public string? RawStatus { get; init; }

    /// <summary>The GPS freshness this reading carries.</summary>
    public BusStatusKind Status { get; init; }

    /// <summary>How old the fix is, in whole minutes, where the API said.</summary>
    public int? GpsAgeMinutes { get; init; }

    /// <summary>Feed back as <c>lastServerTime</c> to receive only new breadcrumbs.</summary>
    public long ServerTime { get; init; }

    public long? CurrentSequence { get; init; }

    /// <summary>Whether the API reported distance in kilometres.</summary>
    public bool DistanceWasKilometres { get; init; }

    internal static RiderInfo From(JsonElement payload, long serverTime)
    {
        bool inKilometres = payload.Bool("isDistKm");
        double? distance = payload.Double("dist");
        (BusStatusKind kind, int? age) = BusStatus.Parse(payload.String("stsMsg"));

        return new RiderInfo
        {
            Bus = Point(payload, "busLat", "busLon"),
            DistanceMiles = distance is null
                ? null
                : inKilometres ? Geo.KilometresToMiles(distance.Value) : distance.Value,
            Stop = Point(payload, "stpLat", "stpLon"),
            Home = Point(payload, "homLat", "homLon"),
            School = Point(payload, "schLat", "schLon"),
            RawStatus = payload.String("stsMsg"),
            Status = kind,
            GpsAgeMinutes = age,
            ServerTime = serverTime,
            CurrentSequence = payload.Long("curr_seq"),
            DistanceWasKilometres = inKilometres,
        };
    }

    private static GeoPoint? Point(JsonElement payload, string latitudeKey, string longitudeKey)
    {
        double? latitude = payload.Double(latitudeKey);
        double? longitude = payload.Double(longitudeKey);

        // A zero-zero fix is the Gulf of Guinea, not a school bus. The API uses
        // it for "no position", and treating it as one would put a marker two
        // thousand miles into the Atlantic and hand the route matcher a point
        // no past journey can ever match.
        if (latitude is null || longitude is null || (latitude == 0 && longitude == 0))
        {
            return null;
        }

        return new GeoPoint(latitude.Value, longitude.Value);
    }
}

/// <summary>One badge-scan event.</summary>
/// <remarks>
/// The endpoint returns bare "ID received" events with a location and time — it
/// does NOT say whether the rider got on or off. <see cref="Kind"/> is inferred.
/// </remarks>
public sealed record ScanEvent(DateTimeOffset Timestamp, string? Location, ScanKind Kind, string? Method);
