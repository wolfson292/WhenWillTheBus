// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Prediction;

/// <summary>
/// Decide whether the rider is on the bus when they did not scan on.
/// </summary>
/// <remarks>
/// Boarding is normally a fact: the rider scans a badge, and that scan is what
/// the afternoon ride is measured from. But the scan was missed three times in
/// the first nine school days observed, and a missed scan used to cost the whole
/// afternoon's notification.
///
/// The phone answers the same question independently, from two positions: where
/// it was while the bus loaded at school, and where it is once the bus has left.
/// </remarks>
public static class Aboard
{
    /// <summary>Return whether the phone has left with the bus.</summary>
    /// <param name="origin">Where the phone was when the run's window opened.</param>
    /// <param name="phone">Where the phone is now.</param>
    /// <param name="bus">Where the bus is now.</param>
    /// <remarks>
    /// BOTH HALVES ARE REQUIRED. Distance travelled alone cannot tell a bus from
    /// a lift home in a car. Proximity alone cannot tell riding from standing in
    /// a school car park while the bus loads twenty metres away — which is
    /// exactly the moment and place the question gets asked, so proximity alone
    /// reads as aboard every single afternoon.
    ///
    /// A missing fix is NOT an answer of "no": it has not been shown that they
    /// are on the bus, but it has not been shown they are at school either, so
    /// the caller falls back to the scan.
    ///
    /// This deliberately says nothing about WHEN boarding happened, only whether
    /// it did. A phone fix arrives when it arrives; the bus leaving school is
    /// what is being detected, not the step onto it.
    /// </remarks>
    public static bool TravelledWithTheBus(
        GeoPoint? origin,
        GeoPoint? phone,
        GeoPoint? bus,
        double movedMiles = Tuning.AboardMovedMiles,
        double togetherMiles = Tuning.AboardTogetherMiles)
    {
        if (origin is null || phone is null || bus is null)
        {
            return false;
        }

        double gone = Geo.DistanceMiles(origin.Value, phone.Value);
        double apart = Geo.DistanceMiles(phone.Value, bus.Value);
        return gone >= movedMiles && apart <= togetherMiles;
    }
}
