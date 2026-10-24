// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using WhenWillTheBus.Core.Api;

namespace WhenWillTheBus.Core.Tests;

/// <summary>
/// How the client treats a session the server has stopped accepting.
/// </summary>
/// <remarks>
/// 23 Oct 2026: the session expired as a bare HTTP 401, which was taken as
/// "wrong password". The worker never logged in again and the live position
/// stopped all day while the vendor's own app kept working.
/// </remarks>
public sealed class WheresTheBusClientTests
{
    private static readonly WheresTheBusCredentials Credentials = new("parent@example.com", "secret", "device");

    private const string LoginOk = """{"resCode":0,"payload":{"sessionId":"S","basePath":"https://shard.example/"}}""";
    private const string RidersOk = """{"resCode":0,"payload":[]}""";

    /// <summary>THE REPORTED FAULT. A 401 on a live call is a dead session.</summary>
    [Fact]
    public async Task AnExpiredSessionIsReplacedNotReportedAsBadCredentials()
    {
        ScriptedHandler handler = new(
            ("login", HttpStatusCode.OK, LoginOk),
            ("getAllRiders", HttpStatusCode.Unauthorized, ""),
            ("login", HttpStatusCode.OK, LoginOk),
            ("getAllRiders", HttpStatusCode.OK, RidersOk));
        WheresTheBusClient client = new(new HttpClient(handler), Credentials);

        using var riders = await client.GetAllRidersAsync();

        Assert.Equal(0, riders.RootElement.GetProperty("resCode").GetInt32());
        Assert.Equal(["login", "getAllRiders", "login", "getAllRiders"], handler.Calls);
    }

    /// <summary>
    /// A 401 that survives a fresh login is the account, not the session, and
    /// must not become a loop of logins.
    /// </summary>
    [Fact]
    public async Task ARefusalAfterAFreshLoginIsFinal()
    {
        ScriptedHandler handler = new(
            ("login", HttpStatusCode.OK, LoginOk),
            ("getAllRiders", HttpStatusCode.Unauthorized, ""),
            ("login", HttpStatusCode.OK, LoginOk),
            ("getAllRiders", HttpStatusCode.Unauthorized, ""));
        WheresTheBusClient client = new(new HttpClient(handler), Credentials);

        await Assert.ThrowsAsync<WheresTheBusAuthException>(() => client.GetAllRidersAsync());
        Assert.Equal(4, handler.Calls.Count);
    }

    /// <summary>A 401 from the login itself is still the credentials.</summary>
    [Fact]
    public async Task ARefusedLoginIsNotRetried()
    {
        ScriptedHandler handler = new(("login", HttpStatusCode.Unauthorized, ""));
        WheresTheBusClient client = new(new HttpClient(handler), Credentials);

        await Assert.ThrowsAsync<WheresTheBusAuthException>(() => client.GetAllRidersAsync());
        Assert.Equal(["login"], handler.Calls);
    }

    /// <summary>Answers each request with the next scripted reply, checking its endpoint.</summary>
    private sealed class ScriptedHandler(params (string Endpoint, HttpStatusCode Status, string Body)[] script)
        : HttpMessageHandler
    {
        private int _next;

        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string endpoint = request.RequestUri!.AbsolutePath.Split('/')[^1];
            Calls.Add(endpoint);

            Assert.True(_next < script.Length, $"unexpected extra request to {endpoint}");
            (string expected, HttpStatusCode status, string body) = script[_next++];
            Assert.Equal(expected, endpoint);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
