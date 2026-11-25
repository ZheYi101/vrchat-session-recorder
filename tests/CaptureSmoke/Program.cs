using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VrchatSessionRecorder;

const int sampleRate = 48000;
const int toneMs = 40;
string ffmpeg = args.Length == 0 ? "ffmpeg" : args[0];
var directory = Directory.CreateTempSubdirectory("vrchat-capture-smoke-");
try
{
    var faults = new ConcurrentQueue<string>();
    int allowed = 0;
    long sessionStart = Now() - TimeSpan.FromMilliseconds(250).Ticks;
    var source = CreateSlice();
    source.OnData(Tone(440), AudioClientBufferFlags.None, Now());
    source.Close();
    await source.Completion.WaitAsync(TimeSpan.FromSeconds(20));
    Check(!Directory.EnumerateFiles(directory.FullName).Any(), "A muted source opened an output file.");

    Volatile.Write(ref allowed, 1);
    source = CreateSlice();
    PrimeGate(source);
    await Task.Delay(25);
    long first = Now();
    source.OnData(Tone(440), AudioClientBufferFlags.None, first);
    await Task.Delay(175);
    source.OnData(Tone(1200), AudioClientBufferFlags.None, Now());
    Volatile.Write(ref allowed, 0);
    source.OnData(Tone(1600), AudioClientBufferFlags.None, Now());
    await Task.Delay(175);

    Volatile.Write(ref allowed, 1);
    PrimeGate(source);
    await Task.Delay(25);
    long second = Now();
    source.OnData(Tone(660), AudioClientBufferFlags.None, second);
    await Task.Delay(175);
    source.OnData(Tone(1200), AudioClientBufferFlags.None, Now());
    source.Close();
    await source.Completion.WaitAsync(TimeSpan.FromSeconds(20));

    Check(faults.IsEmpty, string.Join(Environment.NewLine, faults));
    string file = Directory.EnumerateFiles(directory.FullName).Single();
    Check(file.EndsWith(".ogg", StringComparison.Ordinal), "Completed output was not finalized as Ogg.");
    long offset = long.Parse(Path.GetFileName(file).Split('-')[1]);
    Check(offset == (first - sessionStart) / TimeSpan.TicksPerMillisecond, "First packet timestamp was not retained.");

    byte[] samples = await DecodeAsync(ffmpeg, file);
    long secondMs = (second - first) / TimeSpan.TicksPerMillisecond;
    double durationMs = samples.Length * 500d / sampleRate;
    Check(Math.Abs(durationMs - (secondMs + toneMs)) <= 3, "Silence gap or held mute tail has the wrong length.");
    Check(MeanAmplitude(samples, 10, 20) > 1000, "The first synthetic tone was inaudible.");
    Check(MeanAmplitude(samples, 100, (int)secondMs - 20) < 200, "The gap contained audio from the muted interval.");
    Check(MeanAmplitude(samples, (int)secondMs + 10, (int)secondMs + 20) > 1000, "The second tone was not aligned.");
    Console.WriteLine($"The microphone gate, trailing mute guard, and {secondMs - toneMs} ms timeline gap passed.");

    AudioCapture.CaptureSlice CreateSlice() => new(new WaveFormat(sampleRate, 16, 1), "mic", "s16le",
        TimeSpan.FromMilliseconds(120), ffmpeg, directory.FullName, sessionStart,
        () => Volatile.Read(ref allowed) == 1, faults.Enqueue, () => faults.Enqueue("Encoder stopped the source."));
}
finally
{
    foreach (string file in Directory.EnumerateFiles(directory.FullName))
        File.Delete(file);
    directory.Delete();
}

static long Now() => (long)((Int128)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

static void PrimeGate(AudioCapture.CaptureSlice source) =>
    source.OnData(Tone(1000), AudioClientBufferFlags.None, Now() - TimeSpan.FromMilliseconds(50).Ticks);

static byte[] Tone(double hertz)
{
    byte[] samples = new byte[toneMs * sampleRate / 1000 * 2];
    for (int frame = 0; frame < samples.Length / 2; frame++)
    {
        short amplitude = (short)(10000 * Math.Sin(2 * Math.PI * frame * hertz / sampleRate));
        BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(frame * 2), amplitude);
    }
    return samples;
}

static double MeanAmplitude(byte[] samples, int startMs, int endMs)
{
    int start = startMs * sampleRate / 1000;
    int end = endMs * sampleRate / 1000;
    long sum = 0;
    for (int frame = start; frame < end; frame++)
        sum += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(frame * 2)));
    return sum / (double)(end - start);
}

static async Task<byte[]> DecodeAsync(string ffmpeg, string file)
{
    var start = new ProcessStartInfo(ffmpeg)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-i", file, "-f", "s16le", "pipe:1" })
        start.ArgumentList.Add(argument);

    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
    var error = process.StandardError.ReadToEndAsync();
    using var samples = new MemoryStream();
    await process.StandardOutput.BaseStream.CopyToAsync(samples);
    await process.WaitForExitAsync();
    Check(process.ExitCode == 0, await error);
    return samples.ToArray();
}

static void Check(bool passed, string message)
{
    if (!passed)
        throw new InvalidOperationException(message);
}
