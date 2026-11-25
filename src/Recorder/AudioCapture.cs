using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VrchatSessionRecorder;

internal sealed record AudioDevice(string Id, string DisplayName);

internal sealed class AudioCapture : IAsyncDisposable
{
    private static readonly WaveFormat MicrophoneFormat = new(48000, 16, 1);
    private static readonly WaveFormat GameFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    private static readonly TimeSpan MicrophoneGuard = TimeSpan.FromMilliseconds(120);

    private readonly Source microphone;
    private readonly Source game;
    private readonly string ffmpegPath;
    private readonly string workDirectory;
    private readonly long sessionStart;
    private readonly Func<bool> microphoneAllowed;
    private readonly Func<bool> gameAllowed;

    public AudioCapture(
        string ffmpegPath, string workDirectory, long sessionStartTicks,
        Func<bool>? microphoneAllowed = null, Func<bool>? gameAllowed = null)
    {
        this.ffmpegPath = ffmpegPath;
        this.workDirectory = workDirectory;
        sessionStart = ToReferenceTime(sessionStartTicks);
        this.microphoneAllowed = microphoneAllowed ?? (() => true);
        this.gameAllowed = gameAllowed ?? (() => true);
        microphone = new Source("mic", ReportFailure);
        game = new Source("game", ReportFailure);
    }

    public event Action<string>? Faulted;

    public static IReadOnlyList<AudioDevice> ListMicrophones()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
                devices.Add(new AudioDevice(device.ID, device.FriendlyName));
        }

        return devices.OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public Task SetMicrophoneAsync(string? deviceId) => microphone.ChangeAsync(deviceId,
        deviceId is null ? null : () => Task.FromResult(CreateMicrophone(deviceId)));

    public Task SetGameAsync(int? processId) => game.ChangeAsync(processId?.ToString(CultureInfo.InvariantCulture),
        processId is null ? null : () => CreateGameAsync(processId.Value));

    public Task FinishAsync() => Task.WhenAll(microphone.FinishAsync(), game.FinishAsync());

    public async ValueTask DisposeAsync() => await FinishAsync().ConfigureAwait(false);

    private CaptureDevice CreateMicrophone(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        try
        {
            if (device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active)
                throw new InvalidOperationException("The selected microphone is unavailable.");

            var recorder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithFormat(MicrophoneFormat)
                .WithBufferLength(40)
                .Build();

            return new CaptureDevice(recorder, device, "mic", ReportFailure,
                abort => new CaptureSlice(MicrophoneFormat, "mic", "s16le", MicrophoneGuard,
                    ffmpegPath, workDirectory, sessionStart, microphoneAllowed, ReportFailure, abort));
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private async Task<CaptureDevice> CreateGameAsync(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Per-process audio capture requires a supported version of Windows.");

        var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback(checked((uint)processId), ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(GameFormat)
            .BuildAsync().ConfigureAwait(false);

        return new CaptureDevice(recorder, null, "game", ReportFailure,
            abort => new CaptureSlice(GameFormat, "game", "f32le", TimeSpan.Zero,
                ffmpegPath, workDirectory, sessionStart, gameAllowed, ReportFailure, abort));
    }

    private void ReportFailure(string message) => Faulted?.Invoke(message);

    // WASAPI GetBuffer exposes the QPC in units of 100 ns. Stopwatch exposes the same clock in hardware ticks.
    private static long ToReferenceTime(long ticks) =>
        (long)((Int128)ticks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

    private static long ReferenceTimeNow() => ToReferenceTime(Stopwatch.GetTimestamp());

    private sealed class Source(string name, Action<string> reportFailure)
    {
        private readonly object gate = new();
        private readonly SemaphoreSlim transitions = new(1, 1);
        private CaptureDevice? current;
        private string? requestedKey;
        private long revision;
        private Task? changeTask;
        private Task? finishTask;

        public Task ChangeAsync(string? key, Func<Task<CaptureDevice>>? create)
        {
            lock (gate)
            {
                if (finishTask is not null)
                    return finishTask;

                if (key == requestedKey)
                {
                    if (changeTask is { IsCompleted: false })
                        return changeTask;
                    if (current is { IsOpen: true } || (key is null && current is null))
                        return Task.CompletedTask;
                }

                long change = ++revision;
                requestedKey = key;
                current?.Close();
                changeTask = Task.Run(() => ApplyAsync(change, create));
                return changeTask;
            }
        }

        public Task FinishAsync()
        {
            lock (gate)
            {
                if (finishTask is not null)
                    return finishTask;

                current?.Close();
                long change = ++revision;
                finishTask = Task.Run(() => ApplyAsync(change, null));
                return finishTask;
            }
        }

        private async Task ApplyAsync(long change, Func<Task<CaptureDevice>>? create)
        {
            await transitions.WaitAsync().ConfigureAwait(false);
            CaptureDevice? pending = null;
            try
            {
                CaptureDevice? previous;
                lock (gate)
                {
                    if (change != revision)
                        return;

                    previous = current;
                    current = null;
                }

                if (previous is not null)
                    await previous.DisposeAsync().ConfigureAwait(false);
                if (create is null)
                    return;

                pending = await create().ConfigureAwait(false);
                lock (gate)
                {
                    if (change != revision)
                        return;

                    current = pending;
                }

                pending.Start();
                pending = null;
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    if (current == pending)
                        current = null;
                }

                reportFailure($"{name} capture: {exception.Message}");
            }
            finally
            {
                try
                {
                    if (pending is not null)
                        await pending.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    reportFailure($"{name} capture cleanup: {exception.Message}");
                }
                finally
                {
                    transitions.Release();
                }
            }
        }
    }

    private sealed class CaptureDevice(
        WasapiRecorder recorder, MMDevice? device, string kind,
        Action<string> reportFailure, Func<Action, CaptureSlice> createSlice) : IAsyncDisposable
    {
        private CaptureSlice? stream;
        private int closed;

        public bool IsOpen => Volatile.Read(ref closed) == 0;

        public void Start()
        {
            if (!IsOpen)
                return;

            var capture = createSlice(Close);
            Volatile.Write(ref stream, capture);
            if (!IsOpen)
            {
                capture.Close();
                return;
            }

            recorder.DataAvailable += OnData;
            recorder.RecordingStopped += OnStopped;
            recorder.StartRecording();
        }

        public void Close()
        {
            Interlocked.Exchange(ref closed, 1);
            Volatile.Read(ref stream)?.Close();
            recorder.StopRecording();
        }

        private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long _, long timestamp) =>
            stream?.OnData(data, flags, timestamp);

        private void OnStopped(object? sender, StoppedEventArgs args)
        {
            bool unexpectedlyStopped = IsOpen;
            Close();
            if (unexpectedlyStopped || args.Exception is not null)
                _ = Task.Run(() => reportFailure($"{kind} capture stopped: {args.Exception?.Message ?? "device stopped"}"));
        }

        public async ValueTask DisposeAsync()
        {
            Close();
            try
            {
                await recorder.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                device?.Dispose();
                if (stream is not null)
                    await stream.Completion.ConfigureAwait(false);
            }
        }
    }

    internal sealed class CaptureSlice
    {
        private static readonly TimeSpan EncoderStopTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan MaximumSilenceGap = TimeSpan.FromMinutes(1);
        private readonly WaveFormat format;
        private readonly string kind;
        private readonly string inputFormat;
        private readonly long guardTicks;
        private readonly string ffmpegPath;
        private readonly string workDirectory;
        private readonly long sessionStart;
        private readonly Func<bool> allowed;
        private readonly Action<string> reportFailure;
        private readonly Action abortSource;
        private readonly CancellationTokenSource encoderTimeout = new();
        private readonly Channel<Packet> packets = Channel.CreateBounded<Packet>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        private GateWindow? currentWindow;
        private int closed;
        private int droppedPackets;

        public CaptureSlice(WaveFormat format, string kind, string inputFormat, TimeSpan guard,
            string ffmpegPath, string workDirectory, long sessionStart,
            Func<bool> allowed, Action<string> reportFailure, Action abortSource)
        {
            this.format = format;
            this.kind = kind;
            this.inputFormat = inputFormat;
            guardTicks = guard.Ticks;
            this.ffmpegPath = ffmpegPath;
            this.workDirectory = workDirectory;
            this.sessionStart = sessionStart;
            this.allowed = allowed;
            this.reportFailure = reportFailure;
            this.abortSource = abortSource;
            Completion = Task.Run(EncodeAsync);
        }

        public Task Completion { get; }
        private bool IsOpen => Volatile.Read(ref closed) == 0;

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0)
                return;
            EndWindow();
            try
            {
                encoderTimeout.CancelAfter(EncoderStopTimeout);
            }
            catch (ObjectDisposedException)
            {
                // A failed writer can already have shut down before the recorder is told to stop.
            }
            packets.Writer.TryComplete();
        }

        private void EndWindow()
        {
            var window = Interlocked.Exchange(ref currentWindow, null);
            if (window is not null)
            {
                window.Close();
                // Wake the encoder to discard the held tail even if no new audio arrives while muted.
                packets.Writer.TryWrite(default);
            }
        }

        private bool CanCapture()
        {
            if (IsOpen && allowed())
                return true;

            EndWindow();
            return false;
        }

        public void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long timestamp)
        {
            if (!CanCapture() || data.IsEmpty)
                return;

            if ((flags & AudioClientBufferFlags.TimestampError) != 0 || timestamp <= 0 ||
                timestamp > ReferenceTimeNow() + TimeSpan.TicksPerMillisecond * 100)
            {
                Interlocked.Increment(ref droppedPackets);
                return;
            }

            var window = Volatile.Read(ref currentWindow);
            if (window is null)
            {
                if ((flags & AudioClientBufferFlags.Silent) != 0)
                    return;

                window = new GateWindow(ReferenceTimeNow());
                Volatile.Write(ref currentWindow, window);
            }

            int frames = data.Length / format.BlockAlign;
            int skipFrames = (int)Math.Clamp(Math.Ceiling((window.OpenedAt - timestamp) *
                (double)format.SampleRate / TimeSpan.TicksPerSecond), 0, frames);
            if (skipFrames == frames)
                return;

            var samples = data.Slice(skipFrames * format.BlockAlign, (frames - skipFrames) * format.BlockAlign);
            byte[] copy = ArrayPool<byte>.Shared.Rent(samples.Length);
            samples.CopyTo(copy);

            var packet = new Packet(copy, frames - skipFrames, timestamp + FramesToTicks(skipFrames), window);
            if (CanCapture())
            {
                if (packets.Writer.TryWrite(packet))
                    return;

                Interlocked.Increment(ref droppedPackets);
            }

            ArrayPool<byte>.Shared.Return(copy, clearArray: true);
        }

        private long FramesToTicks(long frames) => frames * TimeSpan.TicksPerSecond / format.SampleRate;

        private async Task EncodeAsync()
        {
            var held = new Queue<Packet>();
            Process? encoder = null;
            Task<string>? stderr = null;
            CancellationTokenRegistration interruptEncoder = default;
            string? finalPath = null;
            long firstTimestamp = 0;
            long cursor = 0;
            byte[] silence = new byte[format.BlockAlign * 8192];
            CancellationToken cancellation = encoderTimeout.Token;

            try
            {
                await foreach (var packet in packets.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
                {
                    if (packet.Window is not null)
                        held.Enqueue(packet);
                    await FlushAsync(final: false).ConfigureAwait(false);
                }
                await FlushAsync(final: true).ConfigureAwait(false);
                await FinishEncoderAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Close();
                StopEncoder(encoder);
                abortSource();
                reportFailure($"{kind} encoder: {exception.Message}");
            }
            finally
            {
                interruptEncoder.Dispose();
                encoder?.Dispose();
                packets.Writer.TryComplete();
                while (held.TryDequeue(out var pending))
                    ReturnBuffer(pending);
                while (packets.Reader.TryRead(out var pending))
                    ReturnBuffer(pending);

                int dropped = Volatile.Read(ref droppedPackets);
                if (dropped > 0)
                    reportFailure($"{kind} capture skipped {dropped} packets with invalid timestamps or a full encoder queue.");
                encoderTimeout.Dispose();
            }

            async Task FlushAsync(bool final)
            {
                while (held.TryPeek(out var packet))
                {
                    long windowClosedAt = packet.Window!.ClosedAt;
                    long safeThrough = Math.Min(ReferenceTimeNow(), windowClosedAt) - guardTicks;
                    long packetEnd = packet.Timestamp + FramesToTicks(packet.Frames);
                    if (!final && windowClosedAt == long.MaxValue && packetEnd > safeThrough)
                        break;

                    held.Dequeue();
                    try
                    {
                        int frames = packetEnd <= safeThrough ? packet.Frames : (int)Math.Clamp(
                            Math.Floor((safeThrough - packet.Timestamp) * (double)format.SampleRate /
                                TimeSpan.TicksPerSecond), 0, packet.Frames);
                        if (frames == 0)
                            continue;

                        // Long silent periods already have exact timestamps in the filename of the next part.
                        if (encoder is not null && packet.Timestamp - firstTimestamp - FramesToTicks(cursor) > MaximumSilenceGap.Ticks)
                        {
                            await FinishEncoderAsync().ConfigureAwait(false);
                            cursor = 0;
                        }

                        if (encoder is null)
                        {
                            firstTimestamp = packet.Timestamp;
                            long offset = Math.Max(0, (firstTimestamp - sessionStart) / TimeSpan.TicksPerMillisecond);
                            finalPath = Path.Combine(workDirectory, $"{kind}-{offset:D12}-{Guid.NewGuid():N}.ogg");
                            encoder = StartEncoder(finalPath + ".partial");
                            interruptEncoder = cancellation.Register(() => StopEncoder(encoder));
                            stderr = encoder.StandardError.ReadToEndAsync();
                        }

                        long expected = (long)Math.Round((packet.Timestamp - firstTimestamp) *
                            (double)format.SampleRate / TimeSpan.TicksPerSecond);
                        long gap = expected - cursor;
                        int tolerance = format.SampleRate / 400;
                        Stream input = encoder.StandardInput.BaseStream;
                        if (gap > tolerance)
                        {
                            while (gap > 0)
                            {
                                int pad = (int)Math.Min(gap, silence.Length / format.BlockAlign);
                                await input.WriteAsync(silence.AsMemory(0, pad * format.BlockAlign), cancellation).ConfigureAwait(false);
                                gap -= pad;
                                cursor += pad;
                            }
                        }

                        int overlap = gap < -tolerance ? (int)Math.Min(-gap, frames) : 0;
                        int bytes = (frames - overlap) * format.BlockAlign;
                        if (bytes > 0)
                        {
                            await input.WriteAsync(packet.Buffer.AsMemory(overlap * format.BlockAlign, bytes), cancellation)
                                .ConfigureAwait(false);
                            cursor += frames - overlap;
                        }
                    }
                    finally
                    {
                        ReturnBuffer(packet);
                    }
                }
            }

            async Task FinishEncoderAsync()
            {
                if (encoder is null)
                    return;

                await encoder.StandardInput.BaseStream.DisposeAsync().ConfigureAwait(false);
                await encoder.WaitForExitAsync(cancellation).ConfigureAwait(false);
                string error = await stderr!.ConfigureAwait(false);
                if (encoder.ExitCode != 0)
                    throw new InvalidOperationException($"FFmpeg exited with code {encoder.ExitCode}: {error.Trim()}");

                File.Move(finalPath + ".partial", finalPath!);
                interruptEncoder.Dispose();
                encoder.Dispose();
                encoder = null;
            }
        }

        private Process StartEncoder(string path)
        {
            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            string[] args =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", inputFormat,
                "-ar", format.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", format.Channels.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
                "-vn", "-ar", "48000", "-ac", "1", "-c:a", "libopus", "-application", "voip",
                "-b:a", "32k", "-f", "ogg", path,
            ];
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        }

        private static void StopEncoder(Process? encoder)
        {
            try
            {
                if (encoder is { HasExited: false })
                    encoder.Kill();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process may have exited just as cancellation arrived.
            }
        }

        private static void ReturnBuffer(Packet packet)
        {
            if (packet.Window is not null)
                ArrayPool<byte>.Shared.Return(packet.Buffer, clearArray: true);
        }

        private sealed class GateWindow(long openedAt)
        {
            private long closedAt = long.MaxValue;
            public long OpenedAt { get; } = openedAt;
            public long ClosedAt => Volatile.Read(ref closedAt);
            public void Close() => Interlocked.CompareExchange(ref closedAt, ReferenceTimeNow(), long.MaxValue);
        }

        private readonly record struct Packet(byte[] Buffer, int Frames, long Timestamp, GateWindow? Window);
    }
}
