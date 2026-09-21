// SPDX-License-Identifier: GPL-3.0-or-later

namespace WhenWillTheBus.Core.Api;

/// <summary>The API could not be reached, or returned something unusable.</summary>
public class WheresTheBusException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Credentials or the session were rejected.</summary>
public sealed class WheresTheBusAuthException(string message, Exception? inner = null)
    : WheresTheBusException(message, inner);
