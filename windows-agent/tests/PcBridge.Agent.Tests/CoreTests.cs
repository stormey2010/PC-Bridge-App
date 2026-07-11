using System.Text.Json;
using PcBridge.Agent.Core;
using PcBridge.Agent.Windows;
using Xunit;

namespace PcBridge.Agent.Tests;

public sealed class CoreTests
{
    [Fact]
    public void DuplicateCommandsAreAcceptedOnlyOnceWithinRetention()
    {
        var guard = new DuplicateCommandGuard(TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;
        Assert.True(guard.TryAccept("request-1", now));
        Assert.False(guard.TryAccept("request-1", now.AddSeconds(1)));
        Assert.True(guard.TryAccept("request-1", now.AddMinutes(6)));
    }

    [Fact]
    public void ProtocolEnvelopeUsesVersionedSnakeCaseFields()
    {
        var envelope = new ProtocolEnvelope(Protocol.Version, "state_update", "abc", DateTimeOffset.UnixEpoch, "device", new { value = 42 });
        var json = JsonSerializer.Serialize(envelope, Protocol.Json);
        Assert.Contains("\"protocol_version\":1", json);
        Assert.Contains("\"message_id\":\"abc\"", json);
        Assert.DoesNotContain("ProtocolVersion", json);
    }

    [Fact]
    public async Task SettingsRoundTripPreservesStableInstallationId()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pc-bridge-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory);
            var expected = new AgentSettings { DeviceName = "Test PC", HomeAssistantUrl = "https://ha.test" };
            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();
            Assert.Equal(expected.InstallationId, actual.InstallationId);
            Assert.Equal("Test PC", actual.DeviceName);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DpapiStoreNeverWritesPlaintextToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pc-bridge-tests", Guid.NewGuid().ToString("N"));
        const string token = "plain-secret-token-for-test";
        try
        {
            var store = new DpapiCredentialStore(directory);
            await store.SaveTokenAsync(token);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "credential.bin"));
            Assert.DoesNotContain(token, System.Text.Encoding.UTF8.GetString(bytes));
            Assert.Equal(token, await store.GetTokenAsync());
            await store.RemoveTokenAsync();
            Assert.Null(await store.GetTokenAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DestructiveControlsDefaultToDisabled()
    {
        var settings = new AgentSettings();
        Assert.False(settings.EnabledControls["system.restart"]);
        Assert.False(settings.EnabledControls["system.shutdown"]);
        Assert.False(settings.EnabledControls["system.hibernate"]);
        Assert.False(settings.EnabledControls["system.logoff"]);
        Assert.False(settings.EnabledControls["custom.run"]);
        Assert.True(settings.EnabledControls["system.lock"]);
        Assert.True(settings.EnabledControls["app.launch"]);
    }

    [Fact]
    public void StateChangeTrackerSkipsUnchangedValues()
    {
        var tracker = new StateChangeTracker();
        var first = tracker.FilterChanged([new("cpu", 10), new("keep_awake", true)]);
        Assert.Equal(2, first.Count);
        var second = tracker.FilterChanged([new("cpu", 10), new("keep_awake", true)]);
        Assert.Empty(second);
        var third = tracker.FilterChanged([new("cpu", 11), new("keep_awake", true)]);
        Assert.Single(third);
        Assert.Equal("cpu", third[0].Key);
    }

    [Fact]
    public void FileConnectionStatusStoreRoundTripsSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pc-bridge-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileConnectionStatusStore(directory);
            store.Set(new ConnectionSnapshot(ConnectionStatus.Connected, DateTimeOffset.UnixEpoch, null));
            var loaded = FileConnectionStatusStore.TryRead(directory);
            Assert.NotNull(loaded);
            Assert.Equal(ConnectionStatus.Connected, loaded!.Status);
            Assert.Equal(DateTimeOffset.UnixEpoch, loaded.LastConnected);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
