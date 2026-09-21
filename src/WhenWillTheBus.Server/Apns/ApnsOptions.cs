// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel.DataAnnotations;

namespace WhenWillTheBus.Server.Apns;

/// <summary>
/// What is needed to sign and address a push.
/// </summary>
/// <remarks>
/// THE SIGNING KEY IS A SECRET. Supply <see cref="PrivateKeyPath"/> pointing at
/// a mounted file, or <see cref="PrivateKeyPem"/> through an environment
/// variable or Docker secret. Never commit the .p8, and never put it in
/// appsettings.json in a repository — it can send pushes to every device your
/// app is installed on until you revoke it.
/// </remarks>
public sealed class ApnsOptions
{
    public const string Section = "Apns";

    /// <summary>The Key ID of the APNs auth key (the .p8), from the Apple Developer portal.</summary>
    [Required]
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Your ten-character Apple Developer Team ID.</summary>
    [Required]
    public string TeamId { get; set; } = string.Empty;

    /// <summary>The app's bundle identifier, e.g. com.example.whenwillthebus.</summary>
    [Required]
    public string BundleId { get; set; } = string.Empty;

    /// <summary>Path to the .p8 private key. Mount it read-only; do not bake it into an image.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The .p8 contents, if supplied directly as a secret rather than a file.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>
    /// Development builds (and the simulator) register against Apple's sandbox.
    /// A token from one environment is rejected outright by the other, which
    /// presents as a silent non-delivery — so this is worth getting right first.
    /// </summary>
    public bool UseSandbox { get; set; }

    public string Host => UseSandbox ? "api.sandbox.push.apple.com" : "api.push.apple.com";

    /// <summary>The topic a Live Activity push must carry.</summary>
    public string LiveActivityTopic => $"{BundleId}.push-type.liveactivity";
}
