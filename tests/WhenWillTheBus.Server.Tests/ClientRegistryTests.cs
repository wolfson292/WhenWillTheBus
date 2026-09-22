// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using WhenWillTheBus.Server.Devices;

namespace WhenWillTheBus.Server.Tests;

public sealed class ClientRegistryTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private static ClientRegistry New() => new(NullLogger<ClientRegistry>.Instance);

    private static ClientIdentity Phone(
        string id = "VENDOR-1",
        string? label = "Angela's iPhone",
        string? model = "iPhone17,1",
        string? system = "26.0",
        string? app = "1.0",
        string? build = "202609221211",
        bool sandbox = false) =>
        new(id, label, model, system, app, build, sandbox, Monday, Monday, 1);

    [Fact]
    public void APhoneSayingHelloTwiceIsOnePhone()
    {
        ClientRegistry registry = New();
        registry.Greet(Phone(), Monday);
        registry.Greet(Phone(), Monday.AddHours(1));

        Assert.Single(registry.All);
        Assert.Equal(2, registry.All[0].Visits);
    }

    /// <summary>
    /// The registry owns when a phone arrived, not the phone. Taking the
    /// caller's word would let a device that has been talking for a month
    /// report itself as new on every check-in.
    /// </summary>
    [Fact]
    public void FirstSeenBelongsToTheRegistry()
    {
        ClientRegistry registry = New();
        registry.Greet(Phone(), Monday);

        ClientIdentity again = registry.Greet(
            Phone() with { FirstSeen = Monday.AddYears(1) }, Monday.AddDays(3));

        Assert.Equal(Monday, again.FirstSeen);
        Assert.Equal(Monday.AddDays(3), again.LastSeen);
    }

    /// <summary>
    /// A phone that omits a field does not erase what is already known. iOS
    /// returns no vendor identifier before the first unlock after a restart,
    /// and other fields can be missing for reasons just as ordinary — a partial
    /// hello must not blank the row somebody is reading.
    /// </summary>
    [Fact]
    public void APartialHelloKeepsWhatIsAlreadyKnown()
    {
        ClientRegistry registry = New();
        registry.Greet(Phone(), Monday);

        ClientIdentity merged = registry.Greet(
            Phone(label: null, model: null, system: null, app: null, build: null),
            Monday.AddMinutes(30));

        Assert.Equal("Angela's iPhone", merged.Label);
        Assert.Equal("iPhone17,1", merged.Model);
        Assert.Equal("26.0", merged.SystemVersion);
    }

    [Fact]
    public void RenamingAPhoneTakesEffect()
    {
        ClientRegistry registry = New();
        registry.Greet(Phone(), Monday);

        ClientIdentity renamed = registry.Greet(Phone(label: "Willow's iPhone"), Monday.AddMinutes(1));

        Assert.Equal("Willow's iPhone", renamed.Label);
        Assert.Single(registry.All);
    }

    /// <summary>
    /// A household runs both: the development phone mints sandbox push tokens
    /// and the TestFlight phones mint production ones. Which is which has to
    /// survive, because it decides the host every push goes to.
    /// </summary>
    [Fact]
    public void TheApnsEnvironmentFollowsTheBuild()
    {
        ClientRegistry registry = New();
        registry.Greet(Phone(sandbox: true), Monday);

        Assert.False(registry.Greet(Phone(sandbox: false), Monday.AddMinutes(1)).Sandbox);
    }

    [Fact]
    public void PhonesThatStoppedCallingAreForgotten()
    {
        ClientRegistry registry = New();
        registry.Greet(Phone(id: "GONE"), Monday);
        registry.Greet(Phone(id: "HERE"), Monday);

        DateTimeOffset later = Monday + ClientRegistry.Retention + TimeSpan.FromDays(1);
        registry.Greet(Phone(id: "HERE"), later);

        Assert.Equal(1, registry.Prune(later));
        Assert.Equal("HERE", Assert.Single(registry.All).Id);
    }

    [Fact]
    public async Task ARestartDoesNotLoseThePhones()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wwtb-clients-{Guid.NewGuid():N}.json");
        try
        {
            ClientRegistry before = New();
            await before.LoadAsync(path);
            before.Greet(Phone(), Monday);
            before.Greet(Phone(id: "VENDOR-2", label: "Scott's iPhone", sandbox: true), Monday);
            await before.SaveAsync();

            ClientRegistry after = New();
            await after.LoadAsync(path);

            Assert.Equal(2, after.All.Count);
            ClientIdentity angela = after.All.Single(client => client.Id == "VENDOR-1");
            Assert.Equal("Angela's iPhone", angela.Label);
            Assert.Equal("iPhone17,1", angela.Model);
            Assert.False(angela.Sandbox);
            Assert.True(after.All.Single(client => client.Id == "VENDOR-2").Sandbox);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnUnreadableRegistryStartsEmptyRatherThanThrowing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wwtb-clients-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ this is not the file you are looking for");
        try
        {
            ClientRegistry registry = New();
            await registry.LoadAsync(path);
            Assert.Empty(registry.All);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
