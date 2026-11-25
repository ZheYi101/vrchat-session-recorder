using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VrchatSessionRecorder;

if (args.Contains("--task", StringComparer.Ordinal))
{
    string device = Option(args, "--device");
    if (device == "cuda")
        return 42; // Exercise CPU fallback without making the smoke test GPU-dependent.

    if (Environment.GetEnvironmentVariable("VRCHAT_SMOKE_BLOCK_CHILD") is { } childFlag)
    {
        File.WriteAllText(childFlag, "ready");
        await Task.Delay(TimeSpan.FromMinutes(2));
    }
    string output = Path.Combine(Option(args, "--output_dir"), "input.srt");
    await File.WriteAllTextAsync(output, "1\n00:00:00,120 --> 00:00:00,710\nSynthetic speech. テスト。\n\n");
    return 0;
}

bool realRun = args is ["--real", _, _, _, _];
string ffmpeg = realRun ? args[1] : args.Length > 0 ? args[0] : "ffmpeg";
string executable = Environment.ProcessPath!;
string tempParent = Path.GetFullPath(Path.GetTempPath());
string temp = Path.GetFullPath(Path.Combine(tempParent, $"vrchat-transcription-smoke-{Guid.NewGuid():N}"));
Require(Path.GetDirectoryName(temp) == Path.TrimEndingDirectorySeparator(tempParent), "Smoke-test directory escaped temp");
Directory.CreateDirectory(temp);
string model = Path.Combine(temp, "faster-whisper-large-v3-turbo");
Directory.CreateDirectory(model);
foreach (string name in new[] { "model.bin", "config.json", "tokenizer.json", "vocabulary.json" })
    File.WriteAllText(Path.Combine(model, name), "Stub model");

try
{
    if (realRun)
    {
        const string realId = "2026-11-25_01-01-01_aabbccdd";
        string realRoot = CreateRoot("真实模型");
        string realSession = TranscriptionQueue.InitializeSession(realRoot, realId);
        await GenerateOggAsync(Path.Combine(realSession, "mic-0-abc.ogg"), wav: args[4]);
        TranscriptionQueue.MarkClosed(realSession);
        var realQueue = new TranscriptionQueue(ffmpeg, args[2], args[3], realRoot);
        realQueue.StatusChanged += Console.WriteLine;
        await realQueue.ProcessPendingAsync(default);
        string realTranscript = await File.ReadAllTextAsync(Archive(realRoot, realId));
        Require(realTranscript.Contains("synthetic recording", StringComparison.OrdinalIgnoreCase), "Unexpected real transcript");
        Require(!Directory.Exists(realSession), "Real model did not archive the generated source");
        Console.WriteLine("Real local-model transcription and deletion passed.");
        return 0;
    }

    string root = CreateRoot("基本流程");
    const string mergedId = "2026-11-25_01-02-03_aabbccdd";
    string merged = TranscriptionQueue.InitializeSession(root, mergedId);
    await GenerateOggAsync(Path.Combine(merged, "mic-000000005000-abcdef.ogg"));
    await GenerateOggAsync(Path.Combine(merged, "game-000000002000-abcdef.ogg"));
    TranscriptionQueue.MarkClosed(merged);
    await Queue(root).ProcessPendingAsync(default);
    string mergedText = await File.ReadAllTextAsync(Archive(root, mergedId));
    Require(mergedText.Contains("[00:00:02] VRChat：Synthetic speech. テスト。", StringComparison.Ordinal),
        "Missing aligned game speech");
    Require(mergedText.Contains("[00:00:05] 我：Synthetic speech. テスト。", StringComparison.Ordinal),
        "Missing aligned microphone speech");
    Require(mergedText.IndexOf("VRChat：", StringComparison.Ordinal) < mergedText.IndexOf("我：", StringComparison.Ordinal),
        "Speech is out of order");
    Require(!Directory.Exists(merged), "Sources remain after a successful archive");

    const string openedId = "2026-11-25_01-02-04_aabbccdd";
    string opened = TranscriptionQueue.InitializeSession(root, openedId);
    string openedAudio = Path.Combine(opened, "mic-0-abc.ogg");
    await GenerateOggAsync(openedAudio, silent: true);
    await Queue(root).ProcessPendingAsync(default);
    Require(File.Exists(openedAudio) && !File.Exists(Archive(root, openedId)), "An open session was processed");
    TranscriptionQueue.MarkClosed(opened);
    await Queue(root).ProcessPendingAsync(default);
    Require(!File.Exists(openedAudio) && File.ReadAllText(Archive(root, openedId)).Contains("没有可转写", StringComparison.Ordinal),
        "Silent closed session was not safely archived");

    const string corruptId = "2026-11-25_01-02-05_aabbccdd";
    string corrupt = TranscriptionQueue.InitializeSession(root, corruptId);
    string corruptAudio = Path.Combine(corrupt, "mic-0-bad.ogg");
    File.WriteAllText(corruptAudio, "not a recording");
    TranscriptionQueue.MarkClosed(corrupt);
    await Queue(root).ProcessPendingAsync(default);
    Require(File.Exists(corruptAudio) && !File.Exists(Archive(root, corruptId)),
        "A corrupt recording was lost or published");

    const string ownedId = "2026-11-25_01-02-06_aabbccdd";
    string unowned = Path.Combine(TranscriptionQueue.WorkDirectory(root), ownedId);
    Directory.CreateDirectory(unowned);
    string unownedAudio = Path.Combine(unowned, "mic-0-abc.ogg");
    File.WriteAllText(Path.Combine(unowned, "ready"), "");
    await GenerateOggAsync(unownedAudio, silent: true);
    await Queue(root).ProcessPendingAsync(default);
    Require(File.Exists(unownedAudio), "An unowned recording was deleted");

    string collisionRoot = CreateRoot("稿件冲突");
    const string collisionId = "2026-11-25_01-02-07_aabbccdd";
    string collided = TranscriptionQueue.InitializeSession(collisionRoot, collisionId);
    string collidedAudio = Path.Combine(collided, "mic-0-abc.ogg");
    await GenerateOggAsync(collidedAudio, silent: true);
    TranscriptionQueue.MarkClosed(collided);
    Directory.CreateDirectory(TranscriptionQueue.ArchiveDirectory(collisionRoot));
    File.WriteAllText(Archive(collisionRoot, collisionId), "existing transcript");
    await Queue(collisionRoot).ProcessPendingAsync(default);
    Require(File.Exists(collidedAudio) && File.ReadAllText(Archive(collisionRoot, collisionId)) == "existing transcript",
        "An existing transcript was overwritten or its pending source removed");

    string writeFailureRoot = CreateRoot("保存失败");
    const string writeFailureId = "2026-11-25_01-02-08_aabbccdd";
    string writeFailure = TranscriptionQueue.InitializeSession(writeFailureRoot, writeFailureId);
    string writeFailureAudio = Path.Combine(writeFailure, "mic-0-abc.ogg");
    await GenerateOggAsync(writeFailureAudio, silent: true);
    TranscriptionQueue.MarkClosed(writeFailure);
    File.WriteAllText(TranscriptionQueue.ArchiveDirectory(writeFailureRoot), "blocks archive directory");
    await Queue(writeFailureRoot).ProcessPendingAsync(default);
    Require(File.Exists(writeFailureAudio), "Recording disappeared after an archive path failure");

    string cancellationRoot = CreateRoot("中断后重试");
    const string cancellationId = "2026-11-25_01-02-09_aabbccdd";
    string interrupted = TranscriptionQueue.InitializeSession(cancellationRoot, cancellationId);
    string interruptedAudio = Path.Combine(interrupted, "mic-0-abc.ogg");
    await GenerateOggAsync(interruptedAudio);
    TranscriptionQueue.MarkClosed(interrupted);
    string signal = Path.Combine(temp, "child.started");
    Environment.SetEnvironmentVariable("VRCHAT_SMOKE_BLOCK_CHILD", signal);
    try
    {
        using var cancellation = new CancellationTokenSource();
        Task work = Queue(cancellationRoot).ProcessPendingAsync(cancellation.Token);
        for (int count = 0; count < 100 && !File.Exists(signal) && !work.IsCompleted; count++)
            await Task.Delay(50);
        bool childStarted = File.Exists(signal);
        cancellation.Cancel();
        try
        {
            await work;
            throw new Exception("The running job did not observe cancellation");
        }
        catch (OperationCanceledException)
        {
            Require(childStarted, "Cancellation did not exercise a running recognizer");
            Require(File.Exists(interruptedAudio) && !File.Exists(Archive(cancellationRoot, cancellationId)),
                "Interrupted work was published or removed");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("VRCHAT_SMOKE_BLOCK_CHILD", null);
    }
    await Queue(cancellationRoot).ProcessPendingAsync(default);
    Require(!File.Exists(interruptedAudio) && File.Exists(Archive(cancellationRoot, cancellationId)),
        "Interrupted job could not be retried");

    string resumeRoot = CreateRoot("提交后继续");
    const string resumeId = "2026-11-25_01-02-10_aabbccdd";
    string resumed = TranscriptionQueue.InitializeSession(resumeRoot, resumeId);
    const string remainingName = "mic-0-abc.ogg";
    string remainingPath = Path.Combine(resumed, remainingName);
    await GenerateOggAsync(remainingPath);
    TranscriptionQueue.MarkClosed(resumed);
    Directory.CreateDirectory(TranscriptionQueue.ArchiveDirectory(resumeRoot));
    string trustedTranscript = Archive(resumeRoot, resumeId);
    File.WriteAllText(trustedTranscript, "durable synthetic transcript");
    string scratch = Path.Combine(resumed, ".transcription");
    Directory.CreateDirectory(scratch);
    var commit = new
    {
        TranscriptHash = Hash(trustedTranscript),
        Audio = new[]
        {
            new { Name = remainingName, Hash = Hash(remainingPath) },
            new { Name = "game-0-abc.ogg", Hash = new string('A', 64) }
        }
    };
    string originalTranscript = File.ReadAllText(trustedTranscript);
    await File.WriteAllTextAsync(Path.Combine(scratch, "commit.json"), JsonSerializer.Serialize(commit));
    File.WriteAllText(trustedTranscript, "externally changed");
    await Queue(resumeRoot).ProcessPendingAsync(default);
    Require(File.Exists(remainingPath), "Changed published transcript failed to protect remaining audio");
    File.WriteAllText(trustedTranscript, originalTranscript);
    await Queue(resumeRoot).ProcessPendingAsync(default);
    Require(!Directory.Exists(resumed) && File.ReadAllText(trustedTranscript) == originalTranscript,
        "A partial cleanup could not resume from a verified transcript");
    Console.WriteLine("Transcription smoke checks passed.");
    return 0;
}
finally
{
    Require(Path.GetDirectoryName(temp) == Path.TrimEndingDirectorySeparator(tempParent), "Unsafe cleanup path");
    Directory.Delete(temp, recursive: true);
}

TranscriptionQueue Queue(string root) => new(ffmpeg, executable, model, root);

string CreateRoot(string name) => Directory.CreateDirectory(Path.Combine(temp, name)).FullName;

static string Archive(string root, string id) => Path.Combine(TranscriptionQueue.ArchiveDirectory(root), id + ".md");

static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

static string Option(string[] values, string name) => values[Array.IndexOf(values, name) + 1];

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

async Task GenerateOggAsync(string path, bool silent = false, string? wav = null)
{
    var start = new ProcessStartInfo
    {
        FileName = ffmpeg,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true
    };
    string[] input = wav is null
        ? ["-f", "lavfi", "-i", silent ? "anullsrc=r=16000:cl=mono" : "sine=frequency=400:sample_rate=16000", "-t", "0.9"]
        : ["-i", wav];
    string[] arguments =
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        ..input, "-codec:a", "libopus", "-b:a", "32k", path
    ];
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }
    using Process process = Process.Start(start)!;
    Task<string> error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Require(process.ExitCode == 0, await error);
}
