// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace WhenWillTheBus.Server.Apns;

/// <summary>
/// Holds the APNs signing key and the provider token minted from it.
/// </summary>
/// <remarks>
/// A SINGLETON, and that is the whole point of the class.
///
/// Apple rate-limits how often a provider token may be regenerated and answers
/// <c>429 TooManyProviderTokenUpdates</c> when you exceed it. The token cache
/// therefore has to outlive a single request. <see cref="ApnsClient"/> is a
/// typed HTTP client and so is transient by design: caching the token inside it
/// meant every call minted a fresh JWT, which works perfectly until two pushes
/// happen close together and then fails for everything, including the pushes
/// that matter.
///
/// Found by pushing twice in a minute while testing.
/// </remarks>
public sealed class ApnsTokenProvider(IOptions<ApnsOptions> options)
{
    /// <summary>
    /// Apple rejects a token older than an hour, and rejects regenerating one
    /// more often than every twenty minutes. Fifty sits clear of both.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(50);

    private readonly ApnsOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ECDsa? _key;
    private string? _token;
    private DateTimeOffset _issued = DateTimeOffset.MinValue;

    /// <summary>The current provider token, minting a new one only when due.</summary>
    public async Task<string> TokenAsync(CancellationToken cancellationToken = default)
    {
        if (_token is not null && DateTimeOffset.UtcNow - _issued < Lifetime)
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow - _issued < Lifetime)
            {
                return _token;
            }

            DateTimeOffset issued = DateTimeOffset.UtcNow;
            string header = ToBase64Url($$"""{"alg":"ES256","kid":"{{_options.KeyId}}"}""");
            string claims = ToBase64Url($$"""{"iss":"{{_options.TeamId}}","iat":{{issued.ToUnixTimeSeconds()}}}""");
            string signingInput = $"{header}.{claims}";

            _key ??= LoadKey();

            // SignData returns IEEE P1363 (r||s), which is exactly what JWS ES256
            // wants -- no DER unwrapping needed.
            byte[] signature = _key.SignData(
                Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            _token = $"{signingInput}.{ToBase64Url(signature)}";
            _issued = issued;
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ECDsa LoadKey()
    {
        string pem = _options.PrivateKeyPem
            ?? (_options.PrivateKeyPath is not null && File.Exists(_options.PrivateKeyPath)
                ? File.ReadAllText(_options.PrivateKeyPath)
                : throw new InvalidOperationException(
                    "No APNs signing key. Set Apns:PrivateKeyPath to a mounted .p8, "
                    + "or Apns:PrivateKeyPem as a secret."));

        ECDsa key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    private static string ToBase64Url(string value) => ToBase64Url(Encoding.UTF8.GetBytes(value));

    private static string ToBase64Url(byte[] value) => Base64Url.EncodeToString(value);
}
