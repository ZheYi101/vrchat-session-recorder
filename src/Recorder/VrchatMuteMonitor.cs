using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using OscQueryLibrary;
using OscQueryLibrary.Models;

namespace VrchatSessionRecorder;

internal sealed class VrchatMuteMonitor : IAsyncDisposable
{
    private const string MutePath = "/avatar/parameters/MuteSelf";
    private const string AvatarChangePath = "/avatar/change";
    private const string ClientPrefix = "VRChat-Client-";
    private const string Waiting = "等待 VRChat 的 OSC 静音状态";
    private readonly object stateLock = new();
    private Task worker = Task.CompletedTask;
    private CancellationTokenSource? gameLifetime;
    private int? gameProcessId;
    private int generation;
    private bool disposed;
    private bool? muted;
    private string status = string.Empty;
    private long unmutedUntil;

    public bool IsUnmuted => Stopwatch.GetTimestamp() < Volatile.Read(ref unmutedUntil);
    public event Action<bool>? Changed;
    public event Action<string>? StatusChanged;

    public void SetGameProcess(int? processId)
    {
        lock (stateLock)
        {
            if (disposed || gameProcessId == processId)
                return;

            RestartWhileLocked(processId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task completion;
        lock (stateLock)
        {
            if (disposed)
                return;

            disposed = true;
            RestartWhileLocked(null);
            completion = worker;
        }

        await completion.ConfigureAwait(false);
    }

    private void RestartWhileLocked(int? processId)
    {
        int nextGeneration = ++generation;
        gameProcessId = processId;
        var previous = worker;
        var previousLifetime = gameLifetime;
        previousLifetime?.Cancel();
        gameLifetime = processId.HasValue ? new CancellationTokenSource() : null;
        var cancellationToken = gameLifetime?.Token ?? CancellationToken.None;
        PublishWhileLocked(null, processId.HasValue ? Waiting : string.Empty);

        worker = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            finally
            {
                previousLifetime?.Dispose();
            }

            if (processId.HasValue)
                await MonitorGameAsync(processId.Value, nextGeneration, cancellationToken).ConfigureAwait(false);
        });
    }

    private async Task MonitorGameAsync(int processId, int currentGeneration, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await MonitorConnectionAsync(processId, currentGeneration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or HttpListenerException or HttpRequestException)
            {
                lock (stateLock)
                {
                    if (generation == currentGeneration)
                        PublishWhileLocked(null, "OSC 连接不可用，麦克风已暂停");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task MonitorConnectionAsync(int processId, int currentGeneration, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        ushort receivePort = checked((ushort)((IPEndPoint)socket.Client.LocalEndPoint!).Port);
        using var http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = ConnectLoopbackAsync
        })
        {
            Timeout = TimeSpan.FromSeconds(1),
            MaxResponseContentBufferSize = 16 * 1024
        };
        var query = new OscQueryHttpClient(http);
        using var multicast = OscQueryDiscovery.CreateMulticastService();
        using var host = new OscQueryHost(new OscQueryHostOptions
        {
            ServiceName = "VRChat Session Recorder",
            Address = IPAddress.Loopback,
            HttpBind = IPAddress.Loopback.ToString(),
            OscPort = receivePort,
            Root = new OscQueryNode
            {
                Access = AccessValues.NoValue,
                Contents = new Dictionary<string, OscQueryNode>
                {
                    ["avatar"] = new() { FullPath = "/avatar", Access = AccessValues.Write }
                }
            }
        }, multicast);
        using var discovery = new OscQueryDiscovery(new OscQueryDiscoveryOptions
        {
            InstanceFilter = name => name.StartsWith(ClientPrefix, StringComparison.OrdinalIgnoreCase),
            HttpClient = query,
            HealthCheckInterval = TimeSpan.FromSeconds(2)
        }, multicast);
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var connection = new Connection();
        multicast.Start();
        host.Start();
        discovery.Start();

        var receive = ReceiveTransitionsAsync(socket, connection, processId, currentGeneration, connectionLifetime.Token);
        var poll = PollMuteAsync(query, discovery, connection, processId, currentGeneration, connectionLifetime.Token);
        try
        {
            await Task.WhenAny(receive, poll).ConfigureAwait(false);
        }
        finally
        {
            connectionLifetime.Cancel();
            lock (stateLock)
            {
                if (generation == currentGeneration)
                    PublishWhileLocked(null, Waiting);
            }
        }

        await Task.WhenAll(receive, poll).ConfigureAwait(false);
    }

    private async Task PollMuteAsync(
        OscQueryHttpClient query, OscQueryDiscovery discovery, Connection connection,
        int processId, int currentGeneration, CancellationToken cancellationToken)
    {
        OscQueryServiceInfo? peer = null;
        long nextOwnershipCheck = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            long now = Stopwatch.GetTimestamp();
            if (now >= nextOwnershipCheck)
            {
                var matchingPeers = discovery.Services.Where(service =>
                    IPAddress.IsLoopback(service.HttpEndPoint.Address) &&
                    IPAddress.IsLoopback(service.OscEndPoint.Address) &&
                    service.HostInfo.OscPort is > 0 &&
                    OwnsUdpPort(processId, service.OscEndPoint.Port)).Take(2).ToArray();
                var verified = matchingPeers.Length == 1 ? matchingPeers[0] : null;
                nextOwnershipCheck = now + Stopwatch.Frequency / 2;

                if (peer?.ServiceId != verified?.ServiceId)
                {
                    lock (stateLock)
                    {
                        if (generation != currentGeneration)
                            return;

                        ++connection.Revision;
                        connection.HasVerifiedPeer = verified != null;
                        connection.PauseUntil = 0;
                        connection.AwaitingMuteConfirmation = false;
                        PublishWhileLocked(null, Waiting);
                    }
                }

                peer = verified;
            }

            if (peer != null)
                await ReadMuteAsync(query, peer, connection, currentGeneration, now, cancellationToken).ConfigureAwait(false);

            await connection.WakeProbe.WaitAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        } while (!cancellationToken.IsCancellationRequested);
    }

    private async Task ReadMuteAsync(OscQueryHttpClient query, OscQueryServiceInfo peer, Connection connection,
        int currentGeneration, long requestStarted, CancellationToken cancellationToken)
    {
        int requestVersion;
        lock (stateLock)
        {
            requestVersion = connection.Revision;
        }

        bool? reading = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(225));
        try
        {
            // VRChat's OSCQuery server may return the full node even for a ?VALUE request.
            var node = await query.GetNodeAsync(peer.HttpEndPoint, MutePath, timeout.Token).ConfigureAwait(false);
            if (node.FullPath == MutePath)
                reading = ReadMuteValue(node.Value);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is HttpRequestException or JsonException or OperationCanceledException or NotSupportedException)
        {
            // No status can be trusted for this sample.
        }

        lock (stateLock)
        {
            if (generation != currentGeneration || requestVersion != connection.Revision || cancellationToken.IsCancellationRequested)
                return;

            if (reading == true)
                connection.AwaitingMuteConfirmation = false;
            if (reading == false && (connection.AwaitingMuteConfirmation || requestStarted < connection.PauseUntil))
                return;

            PublishWhileLocked(reading, reading switch
            {
                true => "VRChat 麦克风已静音",
                false => "VRChat 麦克风已开启",
                null => Waiting
            }, requestStarted + Stopwatch.Frequency * 4 / 10);
        }
    }

    private async Task ReceiveTransitionsAsync(UdpClient socket, Connection connection,
        int processId, int currentGeneration, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var datagram = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (!IPAddress.IsLoopback(datagram.RemoteEndPoint.Address))
                continue;

            var transition = ReadOscTransition(datagram.Buffer);
            if (transition == OscTransition.Ignored || !OwnsUdpPort(processId, datagram.RemoteEndPoint.Port))
                continue;

            lock (stateLock)
            {
                if (generation != currentGeneration || !connection.HasVerifiedPeer || cancellationToken.IsCancellationRequested)
                    continue;

                // An event can close the gate immediately. Only a fresh read from the verified
                // local VRChat endpoint can open it, including after an avatar change.
                ++connection.Revision;
                if (transition == OscTransition.Unmuted)
                {
                    if (connection.AwaitingMuteConfirmation || muted == true)
                        connection.PauseUntil = 0;
                    connection.AwaitingMuteConfirmation = false;
                }
                else
                {
                    // A stale HTTP unmuted value must not undo a newer live mute event.
                    if (transition == OscTransition.Muted)
                        connection.AwaitingMuteConfirmation = true;
                    connection.PauseUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
                    PublishWhileLocked(transition == OscTransition.Muted ? true : null,
                        transition == OscTransition.Muted ? "VRChat 麦克风已静音" : Waiting);
                }

                if (connection.WakeProbe.CurrentCount == 0)
                    connection.WakeProbe.Release();
            }
        }
    }

    private void PublishWhileLocked(bool? nextMuted, string nextStatus, long nextExpiration = 0)
    {
        bool wasUnmuted = muted == false;
        bool isUnmuted = nextMuted == false;
        Volatile.Write(ref unmutedUntil, isUnmuted ? nextExpiration : 0);
        muted = nextMuted;
        if (wasUnmuted != isUnmuted)
            Changed?.Invoke(isUnmuted);
        if (!string.Equals(status, nextStatus, StringComparison.Ordinal))
        {
            status = nextStatus;
            StatusChanged?.Invoke(nextStatus);
        }
    }

    internal static bool? ReadMuteValue(JsonElement[]? values)
    {
        if (values is not [var value])
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetDouble(out double number) && number is 0 or 1 => number == 1,
            _ => null
        };
    }

    internal static OscTransition ReadOscTransition(ReadOnlySpan<byte> message)
    {
        if (!TryReadOscString(ref message, out var address) || !TryReadOscString(ref message, out var tags))
            return OscTransition.Ignored;

        if (address.SequenceEqual("/avatar/change"u8) && tags.SequenceEqual(",s"u8))
            return TryReadOscString(ref message, out var avatar) && !avatar.IsEmpty && message.IsEmpty
                ? OscTransition.AvatarChanged
                : OscTransition.Ignored;

        if (!address.SequenceEqual("/avatar/parameters/MuteSelf"u8))
            return OscTransition.Ignored;

        if (message.IsEmpty && tags.SequenceEqual(",T"u8))
            return OscTransition.Muted;
        if (message.IsEmpty && tags.SequenceEqual(",F"u8))
            return OscTransition.Unmuted;
        if (message.Length != sizeof(int))
            return OscTransition.Ignored;

        if (tags.SequenceEqual(",i"u8))
            return ToMuteTransition(BinaryPrimitives.ReadInt32BigEndian(message));
        if (tags.SequenceEqual(",f"u8))
            return ToMuteTransition(BinaryPrimitives.ReadSingleBigEndian(message));

        return OscTransition.Ignored;
    }

    private static OscTransition ToMuteTransition(double value) => value switch
    {
        0 => OscTransition.Unmuted,
        1 => OscTransition.Muted,
        _ => OscTransition.Ignored
    };

    private static bool TryReadOscString(ref ReadOnlySpan<byte> bytes, out ReadOnlySpan<byte> text)
    {
        text = default;
        int end = bytes.IndexOf((byte)0);
        if (end < 0)
            return false;

        int paddedEnd = (end + 4) & ~3;
        if (bytes.Length < paddedEnd || bytes[end..paddedEnd].ContainsAnyExcept((byte)0))
            return false;

        text = bytes[..end];
        bytes = bytes[paddedEnd..];
        return true;
    }

    private static async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(context.DnsEndPoint.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
            throw new HttpRequestException("OSCQuery only accepts loopback peers.");

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool OwnsUdpPort(int processId, int port)
    {
        // Official VRChat OSCQuery uses HttpListener, so HTTP.sys may own the TCP socket.
        // Verify against the game's advertised OSC UDP listener instead.
        const int addressFamilyIpv4 = 2;
        const int udpTableOwnerPid = 1;
        const uint insufficientBuffer = 122;
        int bufferSize = 0;
        if (GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, false, addressFamilyIpv4, udpTableOwnerPid, 0) != insufficientBuffer ||
            bufferSize < sizeof(int) || bufferSize > 8 * 1024 * 1024)
            return false;

        var table = Marshal.AllocHGlobal(bufferSize);
        try
        {
            int capacity = bufferSize;
            if (GetExtendedUdpTable(table, ref bufferSize, false, addressFamilyIpv4, udpTableOwnerPid, 0) != 0)
                return false;

            int count = Marshal.ReadInt32(table);
            const int rowSize = sizeof(int) * 3;
            if (count < 0 || count > (capacity - sizeof(int)) / rowSize)
                return false;

            for (int index = 0; index < count; index++)
            {
                int row = sizeof(int) + rowSize * index;
                if (Marshal.ReadInt32(table, row + 2 * sizeof(int)) != processId)
                    continue;

                ushort localPort = unchecked((ushort)IPAddress.NetworkToHostOrder(
                    unchecked((short)Marshal.ReadInt32(table, row + sizeof(int)))));
                int localAddress = Marshal.ReadInt32(table, row);
                if (localPort == port && (localAddress == 0 || (localAddress & 0xff) == 127))
                    return true;
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort,
        int addressFamily, int tableClass, uint reserved);

    internal enum OscTransition { Ignored, Muted, Unmuted, AvatarChanged }

    private sealed class Connection : IDisposable
    {
        public readonly SemaphoreSlim WakeProbe = new(0, 1);
        public int Revision;
        public bool HasVerifiedPeer;
        public long PauseUntil;
        public bool AwaitingMuteConfirmation;

        public void Dispose() => WakeProbe.Dispose();
    }
}
