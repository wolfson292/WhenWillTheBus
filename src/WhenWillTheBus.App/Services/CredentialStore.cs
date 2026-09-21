// SPDX-License-Identifier: GPL-3.0-or-later

using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.App.Services;

/// <summary>
/// The account credentials, in the iOS Keychain.
/// </summary>
/// <remarks>
/// THE PASSWORD BELONGS IN THE KEYCHAIN and nowhere else — not in preferences,
/// not in a settings file, not in a log line. <see cref="SecureStorage"/> is
/// backed by the Keychain on iOS, so it is encrypted at rest and does not leave
/// the device unless the user has Keychain sync switched on.
/// </remarks>
public sealed class CredentialStore
{
    private const string EmailKey = "wtb.email";
    private const string PasswordKey = "wtb.password";
    private const string DeviceKey = "wtb.deviceId";
    private const string ServerKey = "wtb.serverUrl";
    private const string ServerTokenKey = "wtb.serverKey";

    public async Task<WheresTheBusCredentials?> ReadAsync()
    {
        string? email = await SecureStorage.GetAsync(EmailKey);
        string? password = await SecureStorage.GetAsync(PasswordKey);

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        return new WheresTheBusCredentials(email, password, await DeviceIdAsync());
    }

    public async Task SaveAsync(string email, string password)
    {
        await SecureStorage.SetAsync(EmailKey, email);
        await SecureStorage.SetAsync(PasswordKey, password);
        await DeviceIdAsync();
    }

    public void Forget()
    {
        SecureStorage.Remove(EmailKey);
        SecureStorage.Remove(PasswordKey);
        SecureStorage.Remove(ServerKey);
        SecureStorage.Remove(ServerTokenKey);

        // The device id deliberately SURVIVES a sign-out. It is not a secret and
        // it is not tied to the account: rotating it makes every sign-in look
        // like a brand new device to a server that notices such things.
    }

    /// <summary>
    /// A stable per-install identifier, invented once and kept.
    /// </summary>
    public async Task<string> DeviceIdAsync()
    {
        string? existing = await SecureStorage.GetAsync(DeviceKey);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        string fresh = Guid.NewGuid().ToString("N");
        await SecureStorage.SetAsync(DeviceKey, fresh);
        return fresh;
    }

    /// <summary>Where the always-on worker lives, and the key it expects.</summary>
    public async Task<(string Url, string Key)?> ReadServerAsync()
    {
        string? url = await SecureStorage.GetAsync(ServerKey);
        string? key = await SecureStorage.GetAsync(ServerTokenKey);

        return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key) ? null : (url, key);
    }

    public async Task SaveServerAsync(string url, string key)
    {
        await SecureStorage.SetAsync(ServerKey, url.TrimEnd('/'));
        await SecureStorage.SetAsync(ServerTokenKey, key);
    }
}
