using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VrchatSessionRecorder;

internal static class GameLoopbackSmoke
{
    private const int SampleRate = 48000;
    private const int TargetFrequency = 520;
    private const int DistractorFrequency = 1370;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task ProbeAsync(int processId)
    {
        using var target = Process.GetProcessById(processId);
        if (AudioCapture.GetGamePlaybackMuteNotice(processId) is { } notice)
            Console.WriteLine(notice);

        var recorder = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
            .BuildAsync();
        var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        long packets = 0;
        long activeFrames = 0;
        float peak = 0;
        recorder.RecordingStopped += (_, args) => ended.TrySetResult(args.Exception);
        recorder.DataAvailable += (data, flags, _, _) =>
        {
            packets++;
            if ((flags & AudioClientBufferFlags.Silent) != 0)
                return;

            var samples = MemoryMarshal.Cast<byte, float>(data);
            for (int index = 0; index + 1 < samples.Length; index += 2)
            {
                float framePeak = Math.Max(Math.Abs(samples[index]), Math.Abs(samples[index + 1]));
                if (float.IsFinite(framePeak) && framePeak > 0.0001f)
                {
                    activeFrames++;
                    peak = Math.Max(peak, framePeak);
                }
            }
        };

        try
        {
            recorder.StartRecording();
            var observation = Task.Delay(TimeSpan.FromSeconds(7));
            await Task.WhenAny(observation, ended.Task);
        }
        finally
        {
            await recorder.DisposeAsync();
        }

        if (await ended.Task.WaitAsync(Timeout) is { } fault)
            throw new InvalidOperationException("WASAPI observation stopped with an error.", fault);

        Console.WriteLine($"PID {processId}: {packets} packets, {activeFrames} non-silent stereo frames, peak {peak:F4}. Audio was not saved.");
    }

    public static async Task RunAsync(string ffmpeg, string endpointName)
    {
        string endpointId = FindRenderEndpoint(endpointName);
        var scratch = Directory.CreateTempSubdirectory("vrchat-loopback-smoke-");
        var targetFiles = scratch.CreateSubdirectory("target");
        var controlFiles = scratch.CreateSubdirectory("control");

        try
        {
            using var target = StartRenderer(endpointId, "target");
            using var distractor = StartRenderer(endpointId, "distractor");
            var errors = new ConcurrentQueue<string>();
            var targetError = target.StandardError.ReadToEndAsync();
            var distractorError = distractor.StandardError.ReadToEndAsync();

            try
            {
                await Task.WhenAll(WaitReadyAsync(target), WaitReadyAsync(distractor));
                long started = Stopwatch.GetTimestamp();
                await using var capture = new AudioCapture(ffmpeg, targetFiles.FullName, started,
                    microphoneAllowed: () => false, gameAllowed: () => true);
                await using var control = new AudioCapture(ffmpeg, controlFiles.FullName, started,
                    microphoneAllowed: () => false, gameAllowed: () => true);
                capture.Faulted += errors.Enqueue;
                control.Faulted += errors.Enqueue;

                await Task.WhenAll(capture.SetGameAsync(target.Id), control.SetGameAsync(distractor.Id)).WaitAsync(Timeout);
                await Task.WhenAll(RequestPlaybackAsync(target), RequestPlaybackAsync(distractor));
                await Task.WhenAll(target.WaitForExitAsync(), distractor.WaitForExitAsync()).WaitAsync(Timeout);
                Check(target.ExitCode == 0 && distractor.ExitCode == 0,
                    $"Renderers failed: {(await targetError).Trim()} {(await distractorError).Trim()}");
                await Task.WhenAll(capture.FinishAsync(), control.FinishAsync()).WaitAsync(Timeout);
                Check(errors.IsEmpty, string.Join(Environment.NewLine, errors));

                string targetFile = SingleGameRecording(targetFiles);
                string controlFile = SingleGameRecording(controlFiles);
                byte[] targetedStereo = await DecodeStereoAsync(ffmpeg, targetFile);
                byte[] controlStereo = await DecodeStereoAsync(ffmpeg, controlFile);
                var targetLeft = Coefficient(targetedStereo, 0, TargetFrequency);
                var targetRight = Coefficient(targetedStereo, 1, TargetFrequency);
                var controlLeft = Coefficient(controlStereo, 0, DistractorFrequency);

                Check(controlLeft.Magnitude > 100, $"The independent process did not produce a usable control signal ({controlLeft.Magnitude:F1}).");
                Check(targetLeft.Magnitude > 100 && targetRight.Magnitude > 100,
                    $"The game track did not retain the anti-phase signal on both channels (left {targetLeft.Magnitude:F1}, right {targetRight.Magnitude:F1}).");
                Check((targetLeft + targetRight).Magnitude < targetLeft.Magnitude / 5,
                    "The left and right channels lost the original opposing phase.");
                Check(Coefficient(targetedStereo, 0, DistractorFrequency).Magnitude < targetLeft.Magnitude / 10 &&
                      Coefficient(targetedStereo, 1, DistractorFrequency).Magnitude < targetRight.Magnitude / 10,
                    "The game track contains the unrelated process.");
                Check(Coefficient(controlStereo, 0, TargetFrequency).Magnitude < controlLeft.Magnitude / 10,
                    "The control process contains the target process.");
                Console.WriteLine("WASAPI process loopback preserved stereo anti-phase audio and excluded the unrelated render process with the microphone gate closed.");
            }
            finally
            {
                StopChild(target);
                StopChild(distractor);
            }
        }
        finally
        {
            ClearScratch(targetFiles);
            ClearScratch(controlFiles);
            scratch.Delete();
        }
    }

    public static async Task RenderAsync(string endpointId, bool isTarget)
    {
        using var devices = new MMDeviceEnumerator();
        using var endpoint = devices.GetDevice(endpointId);
        using var player = new WasapiPlayerBuilder().WithDevice(endpoint).WithSharedMode().WithLatency(40).Build();
        var done = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, args) => done.TrySetResult(args.Exception);
        player.Init(new Tone(isTarget));
        Console.WriteLine("ready");
        if (await Console.In.ReadLineAsync().WaitAsync(Timeout) != "play")
            throw new InvalidOperationException("The render process did not receive its playback signal.");

        player.Play();
        var fault = await done.Task.WaitAsync(Timeout);
        if (fault is not null)
            throw new InvalidOperationException("Virtual endpoint playback failed.", fault);
    }

    private static string FindRenderEndpoint(string name)
    {
        using var devices = new MMDeviceEnumerator();
        foreach (var endpoint in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint)
            {
                if (endpoint.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return endpoint.ID;
            }
        }

        throw new InvalidOperationException($"No active render endpoint matching '{name}'. Pass an explicit virtual endpoint name as the last argument.");
    }

    private static Process StartRenderer(string endpointId, string role)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Could not find the test executable."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--render-loopback-smoke");
        start.ArgumentList.Add(endpointId);
        start.ArgumentList.Add(role);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not launch a render child.");
    }

    private static async Task WaitReadyAsync(Process process)
    {
        if (await process.StandardOutput.ReadLineAsync().WaitAsync(Timeout) != "ready")
            throw new InvalidOperationException("The render child exited before opening the virtual audio endpoint.");
    }

    private static async Task RequestPlaybackAsync(Process process)
    {
        await process.StandardInput.WriteLineAsync("play");
        await process.StandardInput.FlushAsync();
    }

    private static void StopChild(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit(2000);
        }
    }

    private static string SingleGameRecording(DirectoryInfo directory)
    {
        Check(!directory.EnumerateFiles("mic-*").Any(), "A microphone file was created with the microphone gate closed.");
        var recordings = directory.EnumerateFiles("game-*.ogg").ToArray();
        Check(recordings.Length == 1, $"Expected one game Ogg, found {recordings.Length} in {directory.Name}.");
        return recordings[0].FullName;
    }

    private static async Task<byte[]> DecodeStereoAsync(string ffmpeg, string file)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-i", file,
                     "-ac", "2", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "s16le", "pipe:1" })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var error = process.StandardError.ReadToEndAsync();
        using var samples = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(samples).WaitAsync(Timeout);
        await process.WaitForExitAsync().WaitAsync(Timeout);
        Check(process.ExitCode == 0, await error);
        return samples.ToArray();
    }

    private static System.Numerics.Complex Coefficient(byte[] samples, int channel, int frequency)
    {
        int start = SampleRate / 5;
        int end = SampleRate;
        Check(samples.Length / 4 >= end, "The decoded game recording is shorter than the test tone.");
        double real = 0;
        double imaginary = 0;
        for (int frame = start; frame < end; frame++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(frame * 4 + channel * 2));
            double phase = 2 * Math.PI * frequency * frame / SampleRate;
            real += sample * Math.Cos(phase);
            imaginary += sample * Math.Sin(phase);
        }

        return new System.Numerics.Complex(real, imaginary) / (end - start);
    }

    private static void ClearScratch(DirectoryInfo directory)
    {
        foreach (var file in directory.EnumerateFiles())
            file.Delete();
        directory.Delete();
    }

    private static void Check(bool passed, string message)
    {
        if (!passed)
            throw new InvalidOperationException(message);
    }

    private sealed class Tone(bool isTarget) : IWaveProvider
    {
        private const int ToneFrames = SampleRate * 8 / 5;
        private const int FadeFrames = SampleRate / 20;
        private int currentFrame;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public int Read(Span<byte> buffer)
        {
            int frames = Math.Min(buffer.Length / WaveFormat.BlockAlign, ToneFrames - currentFrame);
            for (int index = 0; index < frames; index++, currentFrame++)
            {
                double phase = 2 * Math.PI * (isTarget ? TargetFrequency : DistractorFrequency) * currentFrame / SampleRate;
                double envelope = Math.Min(1, Math.Min(currentFrame, ToneFrames - currentFrame) / (double)FadeFrames);
                float left = (float)(0.075 * envelope * Math.Sin(phase));
                var frame = buffer.Slice(index * WaveFormat.BlockAlign, WaveFormat.BlockAlign);
                BinaryPrimitives.WriteSingleLittleEndian(frame, left);
                BinaryPrimitives.WriteSingleLittleEndian(frame[4..], isTarget ? -left : left);
            }

            return frames * WaveFormat.BlockAlign;
        }
    }
}
