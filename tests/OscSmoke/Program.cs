using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OscQueryLibrary;
using OscQueryLibrary.Models;
using VrchatSessionRecorder;
using static VrchatSessionRecorder.VrchatMuteMonitor.OscTransition;

const string mutePath = "/avatar/parameters/MuteSelf";
const string avatarPath = "/avatar/change";

if (args.Contains("--live", StringComparer.Ordinal))
{
    await ObserveLiveGameAsync();
    return;
}

Expect(VrchatMuteMonitor.ReadMuteValue([JsonSerializer.SerializeToElement(false)]) == false, "JSON false");
Expect(VrchatMuteMonitor.ReadMuteValue([JsonSerializer.SerializeToElement(1)]) == true, "JSON numeric true");
Expect(VrchatMuteMonitor.ReadMuteValue([JsonSerializer.SerializeToElement("false")]) == null, "JSON string is unknown");
Expect(VrchatMuteMonitor.ReadMuteValue([]) == null, "No JSON value is unknown");
Expect(VrchatMuteMonitor.ReadOscTransition(Message(mutePath, ",T")) == Muted, "OSC mute");
Expect(VrchatMuteMonitor.ReadOscTransition(Message(mutePath, ",F")) == Unmuted, "OSC unmute");
Expect(VrchatMuteMonitor.ReadOscTransition(Message(avatarPath, ",s", OscString("avtr_synthetic"))) == AvatarChanged,
    "OSC avatar change");

var integer = new byte[sizeof(int)];
BinaryPrimitives.WriteInt32BigEndian(integer, 1);
Expect(VrchatMuteMonitor.ReadOscTransition(Message(mutePath, ",i", integer)) == Muted, "OSC numeric mute");
Expect(VrchatMuteMonitor.ReadOscTransition(Message(mutePath, ",F", [99])) == Ignored, "Malformed OSC packet");
Expect(VrchatMuteMonitor.ReadOscTransition(Message("/avatar/parameters/Voice", ",T")) == Ignored, "Unrelated OSC packet");
Console.WriteLine("OSC parser checks passed.");

if (!args.Contains("--network", StringComparer.Ordinal))
    return;

using var oscPeer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
using var host = new OscQueryHost(new OscQueryHostOptions
{
    ServiceName = $"VRChat-Client-OscSmoke-{Environment.ProcessId}",
    Address = IPAddress.Loopback,
    HttpBind = IPAddress.Loopback.ToString(),
    OscPort = checked((ushort)((IPEndPoint)oscPeer.Client.LocalEndPoint!).Port),
    Root = SyntheticRoot(false)
});
host.Start();

using var receiverDiscovery = new OscQueryDiscovery(new OscQueryDiscoveryOptions
{
    InstanceFilter = name => name == "VRChat Session Recorder",
    HealthCheckInterval = TimeSpan.FromSeconds(1)
});
receiverDiscovery.Start();

await using var monitor = new VrchatMuteMonitor();
Expect(!monitor.IsUnmuted, "Starts closed without a game process");
monitor.SetGameProcess(Environment.ProcessId);
await WaitForAsync(() => monitor.IsUnmuted, "OSCQuery reads the initial unmuted value");
await WaitForAsync(() => receiverDiscovery.Services.Count != 0, "Recorder advertises an independent OSC receiver");
var receiver = receiverDiscovery.Services.Single().OscEndPoint;
await oscPeer.SendAsync(Message(mutePath, ",T"), receiver);
await WaitForAsync(() => !monitor.IsUnmuted, "Live mute closes the gate despite a stale HTTP value");
await Task.Delay(350);
Expect(!monitor.IsUnmuted, "Stale HTTP unmute cannot undo live mute");
await oscPeer.SendAsync(Message(mutePath, ",F"), receiver);
await WaitForAsync(() => monitor.IsUnmuted, "Verified live unmute permits a fresh authoritative value");

host.Root = SyntheticRoot(true);
await WaitForAsync(() => !monitor.IsUnmuted, "Authoritative mute closes the gate");
host.Root = SyntheticRoot(false);
await WaitForAsync(() => monitor.IsUnmuted, "Authoritative unmute reopens the gate");
await oscPeer.SendAsync(Message(avatarPath, ",s", OscString("avtr_synthetic")), receiver);
await WaitForAsync(() => !monitor.IsUnmuted, "Avatar change invalidates the previous mute state");
await WaitForAsync(() => monitor.IsUnmuted, "New avatar accepts a fresh authoritative value");
host.Root = SyntheticRoot(null);
await WaitForAsync(() => !monitor.IsUnmuted, "Missing VALUE closes the gate");
host.Root = SyntheticRoot(false);
await WaitForAsync(() => monitor.IsUnmuted, "Gate reopens once it has a known value");

monitor.SetGameProcess(null);
Expect(!monitor.IsUnmuted, "Game exit closes the gate synchronously");
monitor.SetGameProcess(Environment.ProcessId);
await WaitForAsync(() => monitor.IsUnmuted, "Reopened game re-verifies its local peer");
host.Dispose();
await WaitForAsync(() => !monitor.IsUnmuted, "Stopped OSCQuery service expires the known state");
Console.WriteLine("Loopback OSCQuery checks passed.");

static async Task ObserveLiveGameAsync()
{
    using var currentProcess = Process.GetCurrentProcess();
    var games = Process.GetProcessesByName("VRChat");
    try
    {
        int sessionId = currentProcess.SessionId;
        var matches = games.Where(game => IsInSession(game, sessionId)).ToArray();
        if (matches.Length != 1)
        {
            Console.Error.WriteLine("Live diagnostics requires exactly one running VRChat process in this Windows session.");
            Environment.ExitCode = 2;
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            timeout.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var game = matches[0];
            await using var monitor = new VrchatMuteMonitor();
            monitor.Changed += unmuted => LogLive(unmuted ? "Gate open" : "Gate closed");
            monitor.StatusChanged += state =>
            {
                if (state.Length != 0)
                    LogLive(state);
            };
            LogLive($"Observing VRChat (PID {game.Id}) for up to 60 seconds. Toggle mute in game, or press Ctrl+C to stop.");
            LogLive("Initial gate closed. Diagnostics only.");
            monitor.SetGameProcess(game.Id);
            try
            {
                await game.WaitForExitAsync(timeout.Token);
                LogLive("VRChat exited.");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                LogLive("Observation ended.");
            }
            finally
            {
                monitor.SetGameProcess(null);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }
    finally
    {
        foreach (var game in games)
            game.Dispose();
    }
}

static bool IsInSession(Process process, int sessionId)
{
    try
    {
        return !process.HasExited && process.SessionId == sessionId;
    }
    catch (InvalidOperationException)
    {
        // The process may have exited while it was being enumerated.
        return false;
    }
}

static void LogLive(string message) => Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {message}");

static void Expect(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}

static async Task WaitForAsync(Func<bool> condition, string description)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try
    {
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }
    catch (OperationCanceledException)
    {
        throw new TimeoutException(description);
    }
}

static byte[] Message(string path, string tags, byte[]? payload = null) =>
    [.. OscString(path), .. OscString(tags), .. payload ?? []];

static byte[] OscString(string value)
{
    byte[] contents = Encoding.ASCII.GetBytes(value);
    Array.Resize(ref contents, (contents.Length + 4) & ~3);
    return contents;
}

static OscQueryNode SyntheticRoot(bool? muted) => new()
{
    Contents = new()
    {
        ["avatar"] = new()
        {
            FullPath = "/avatar",
            Contents = new()
            {
                ["parameters"] = new()
                {
                    FullPath = "/avatar/parameters",
                    Contents = new()
                    {
                        ["MuteSelf"] = new()
                        {
                            FullPath = mutePath,
                            Access = AccessValues.Read,
                            Type = "T",
                            Value = muted.HasValue ? [JsonSerializer.SerializeToElement(muted.Value)] : null
                        }
                    }
                }
            }
        }
    }
};
