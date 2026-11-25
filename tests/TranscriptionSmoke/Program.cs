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
    string subtitles = Environment.GetEnvironmentVariable("VRCHAT_SMOKE_EMPTY_SUBTITLE") == "1"
        ? string.Empty
        : "1\n00:00:00,120 --> 00:00:00,710\nSynthetic speech. テスト。\n\n";
    await File.WriteAllTextAsync(output, subtitles);
    return 0;
}

bool realGameRun = args is ["--real-game", _, _, _, _];
bool realRun = args is ["--real", _, _, _, _] || realGameRun;
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
        string track = realGameRun ? "game" : "mic";
        await GenerateOggAsync(Path.Combine(realSession, $"{track}-0-abc.ogg"), wav: args[4], oppositePhase: realGameRun);
        TranscriptionQueue.MarkClosed(realSession);
        var realQueue = new TranscriptionQueue(ffmpeg, args[2], args[3], realRoot);
        realQueue.StatusChanged += Console.WriteLine;
        await realQueue.ProcessPendingAsync(default);
        string realTranscript = await File.ReadAllTextAsync(Archive(realRoot, realId));
        Require(realTranscript.Contains("synthetic recording", StringComparison.OrdinalIgnoreCase), "Unexpected real transcript");
        Require(!realGameRun || realTranscript.Contains("VRChat（已转写）", StringComparison.Ordinal),
            "Real model did not transcribe the opposite-phase game track");
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
    Require(mergedText.Contains("录音轨道：我（已转写） · VRChat（已转写）", StringComparison.Ordinal),
        "Missing two-track transcript outcomes");
    Require(mergedText.Contains("[00:00:02] VRChat：Synthetic speech. テスト。", StringComparison.Ordinal),
        "Missing aligned game speech");
    Require(mergedText.Contains("[00:00:05] 我：Synthetic speech. テスト。", StringComparison.Ordinal),
        "Missing aligned microphone speech");
    Require(mergedText.IndexOf("VRChat：", StringComparison.Ordinal) < mergedText.IndexOf("我：", StringComparison.Ordinal),
        "Speech is out of order");
    Require(!Directory.Exists(merged), "Sources remain after a successful archive");

    const string silentGameId = "2026-11-25_01-02-11_aabbccdd";
    string silentGame = TranscriptionQueue.InitializeSession(root, silentGameId);
    await GenerateOggAsync(Path.Combine(silentGame, "mic-000000005000-abcdef.ogg"));
    await GenerateOggAsync(Path.Combine(silentGame, "game-000000002000-abcdef.ogg"), silent: true);
    TranscriptionQueue.MarkClosed(silentGame);
    await Queue(root).ProcessPendingAsync(default);
    string silentGameText = await File.ReadAllTextAsync(Archive(root, silentGameId));
    Require(silentGameText.Contains("录音轨道：我（已转写） · VRChat（音频近乎静音）", StringComparison.Ordinal),
        "The archive does not explain why a recorded game track has no cues");
    Require(!silentGameText.Contains("VRChat：", StringComparison.Ordinal) && !Directory.Exists(silentGame),
        "Silent game audio generated a cue or was left in pending");

    const string requestedGameId = "2026-11-25_01-02-13_aabbccdd";
    string requestedGame = TranscriptionQueue.InitializeSession(root, requestedGameId);
    TranscriptionQueue.MarkGameRequested(requestedGame);
    TranscriptionQueue.MarkGameRequested(requestedGame);
    await GenerateOggAsync(Path.Combine(requestedGame, "mic-0-abc.ogg"));
    TranscriptionQueue.MarkClosed(requestedGame);
    await Queue(root).ProcessPendingAsync(default);
    string requestedGameText = await File.ReadAllTextAsync(Archive(root, requestedGameId));
    Require(requestedGameText.Contains("录音轨道：我（已转写） · VRChat（已开启，未收到音频）", StringComparison.Ordinal) &&
            !requestedGameText.Contains("VRChat：", StringComparison.Ordinal) && !Directory.Exists(requestedGame),
        "An enabled game track with no packets was not reported or cleaned up");

    string stereoRoot = CreateRoot("立体声");
    const string oppositePhaseId = "2026-11-25_01-02-14_aabbccdd";
    string oppositePhase = TranscriptionQueue.InitializeSession(stereoRoot, oppositePhaseId);
    await GenerateOggAsync(
        Path.Combine(oppositePhase, "game-0-abc.ogg"),
        generator: "aevalsrc=0.15*sin(2*PI*410*t)|-0.15*sin(2*PI*410*t):s=48000");
    TranscriptionQueue.MarkClosed(oppositePhase);
    await Queue(stereoRoot).ProcessPendingAsync(default);
    string oppositePhaseText = await File.ReadAllTextAsync(Archive(stereoRoot, oppositePhaseId));
    Require(oppositePhaseText.Contains("录音轨道：VRChat（已转写）", StringComparison.Ordinal) &&
            oppositePhaseText.Contains("[00:00:00] VRChat：Synthetic speech", StringComparison.Ordinal) &&
            !Directory.Exists(oppositePhase),
        "An audible game track was turned into silence by the stereo-to-mono mix");

    const string directionalId = "2026-11-25_01-02-15_aabbccdd";
    string directional = TranscriptionQueue.InitializeSession(stereoRoot, directionalId);
    await GenerateOggAsync(
        Path.Combine(directional, "game-0-abc.ogg"),
        generator: "aevalsrc=0|0.15*sin(2*PI*410*t):s=48000");
    TranscriptionQueue.MarkClosed(directional);
    await Queue(stereoRoot).ProcessPendingAsync(default);
    Require(File.ReadAllText(Archive(stereoRoot, directionalId)).Contains("录音轨道：VRChat（已转写）", StringComparison.Ordinal) &&
            !Directory.Exists(directional),
        "Ordinary directional stereo speech was not transcribed");

    string emptySubtitleRoot = CreateRoot("有声轨道且字幕为空");
    const string emptySubtitleId = "2026-11-25_01-02-12_aabbccdd";
    string emptySubtitle = TranscriptionQueue.InitializeSession(emptySubtitleRoot, emptySubtitleId);
    string emptyMic = Path.Combine(emptySubtitle, "mic-0-abc.ogg");
    string emptyGame = Path.Combine(emptySubtitle, "game-0-abc.ogg");
    await GenerateOggAsync(emptyMic, silent: true);
    await GenerateOggAsync(emptyGame);
    TranscriptionQueue.MarkClosed(emptySubtitle);
    Environment.SetEnvironmentVariable("VRCHAT_SMOKE_EMPTY_SUBTITLE", "1");
    try
    {
        await Queue(emptySubtitleRoot).ProcessPendingAsync(default);
    }
    finally
    {
        Environment.SetEnvironmentVariable("VRCHAT_SMOKE_EMPTY_SUBTITLE", null);
    }
    Require(File.Exists(emptyMic) && File.Exists(emptyGame) && !File.Exists(Archive(emptySubtitleRoot, emptySubtitleId)),
        "Audio was discarded when a non-silent game track had no recognized cues");

    const string openedId = "2026-11-25_01-02-04_aabbccdd";
    string opened = TranscriptionQueue.InitializeSession(root, openedId);
    string openedAudio = Path.Combine(opened, "mic-0-abc.ogg");
    await GenerateOggAsync(openedAudio, silent: true);
    await Queue(root).ProcessPendingAsync(default);
    Require(File.Exists(openedAudio) && !File.Exists(Archive(root, openedId)), "An open session was processed");
    TranscriptionQueue.MarkClosed(opened);
    await Queue(root).ProcessPendingAsync(default);
    string openedText = File.ReadAllText(Archive(root, openedId));
    Require(!File.Exists(openedAudio) && openedText.Contains("没有可转写", StringComparison.Ordinal),
        "Silent closed session was not safely archived");
    Require(openedText.Contains("录音轨道：我（音频近乎静音）", StringComparison.Ordinal) &&
            !openedText.Contains("VRChat（", StringComparison.Ordinal),
        "A legacy microphone-only job reports the optional game track as attempted");

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

async Task GenerateOggAsync(string path, bool silent = false, string? wav = null, string? generator = null,
    bool oppositePhase = false)
{
    var start = new ProcessStartInfo
    {
        FileName = ffmpeg,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true
    };
    string[] input = wav is null
        ? ["-f", "lavfi", "-i", generator ?? (silent ? "anullsrc=r=16000:cl=mono" : "sine=frequency=400:sample_rate=16000"), "-t", "0.9"]
        : ["-i", wav];
    string[] layout = oppositePhase ? ["-af", "pan=stereo|c0=c0|c1=-1*c0"] : [];
    string[] arguments =
    [
        "-hide_banner", "-loglevel", "error", "-nostdin",
        ..input, ..layout, "-codec:a", "libopus", "-b:a", generator is null && !oppositePhase ? "32k" : "64k", path
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
