using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VrchatSessionRecorder;

internal sealed class TranscriptionQueue(
    string ffmpegPath,
    string fasterWhisperPath,
    string modelPath,
    string rootDirectory)
{
    private const string StorageName = ".vrchat-recorder";
    private const string PendingName = "pending";
    private const string SessionMarkerName = "session.json";
    private const string ReadyName = "ready";
    private const string ScratchName = ".transcription";
    private const string InputName = "input.wav";
    private const string SubtitleName = "input.srt";
    private const string DraftName = "archive.pending.md";
    private const string CommitName = "commit.json";
    private const string CommitDraftName = "commit.pending.json";
    private const string ModelDirectoryName = "faster-whisper-large-v3-turbo";
    private const double SilencePeakDb = -80;

    private static readonly Regex SessionIdPattern = new(
        @"\A\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_[a-f0-9]{8}\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex AudioNamePattern = new(
        @"\A(?<kind>mic|game)-(?<offset>[0-9]{1,14})-[a-zA-Z0-9]{1,40}\.ogg\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex SubtitleTimePattern = new(
        @"\A(?<start>\d{2,5}:\d{2}:\d{2}[,.]\d{3})[ \t]+-->[ \t]+(?<end>\d{2,5}:\d{2}:\d{2}[,.]\d{3})(?:[ \t]+.*)?\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex PeakPattern = new(
        @"max_volume:\s+(?<peak>-?[\d.]+) dB", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new(@"\A[A-F0-9]{64}\z", RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding ProcessEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim processGate = new(1, 1);

    public event Action<string>? StatusChanged;

    public static string WorkDirectory(string rootDirectory) =>
        Path.Combine(Path.GetFullPath(rootDirectory), StorageName, PendingName);

    public static string ArchiveDirectory(string rootDirectory) =>
        Path.Combine(Path.GetFullPath(rootDirectory), "文字稿");

    public static string InitializeSession(string rootDirectory, string sessionId)
    {
        if (!IsSessionId(sessionId))
            throw new ArgumentException("录音会话编号无效。", nameof(sessionId));

        string root = Path.GetFullPath(rootDirectory);
        RequireSafeDirectory(root);
        string pending = WorkDirectory(root);
        EnsureSafeDirectory(pending);
        string session = Path.Combine(pending, sessionId);
        if (Path.Exists(session))
            throw new IOException("录音会话目录已存在。");

        EnsureSafeDirectory(session);
        string marker = Path.Combine(session, SessionMarkerName);
        WriteNewFile(marker, """{"schema":1}"""u8);
        return session;
    }

    public static void MarkClosed(string workDir)
    {
        string session = Path.GetFullPath(workDir);
        RequireOwnedSession(session);
        string ready = Path.Combine(session, ReadyName);
        if (TryAttributes(ready) is not null)
        {
            RequirePlainFile(ready);
            return;
        }

        _ = ReadAudioSlices(session, allowScratch: false);
        WriteNewFile(ready, []);
    }

    public static bool HasValidSessionMarker(string workDir)
    {
        try
        {
            RequireOwnedSession(Path.GetFullPath(workDir));
            return true;
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            return false;
        }
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await processGate.WaitAsync(cancellationToken);
        try
        {
            string pending = WorkDirectory(rootDirectory);
            if (TryAttributes(pending) is null)
                return;

            RequireSafeDirectory(pending);
            foreach (string session in Directory.EnumerateDirectories(pending).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSessionId(Path.GetFileName(session)) || !HasValidSessionMarker(session))
                    continue;

                try
                {
                    string ready = Path.Combine(session, ReadyName);
                    if (TryAttributes(ready) is null)
                        continue;

                    RequirePlainFile(ready);
                    StatusChanged?.Invoke($"正在转写 {Path.GetFileName(session)}");
                    await ProcessSessionAsync(session, cancellationToken);
                    StatusChanged?.Invoke($"已保存文字稿 {Path.GetFileName(session)}");
                }
                catch (Exception error) when (IsExpectedFailure(error))
                {
                    StatusChanged?.Invoke($"{Path.GetFileName(session)} 待重试。{error.Message}");
                }
            }
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            StatusChanged?.Invoke($"无法读取待转写目录。{error.Message}");
        }
        finally
        {
            processGate.Release();
        }
    }

    private async Task ProcessSessionAsync(string session, CancellationToken cancellationToken)
    {
        var audio = ReadAudioSlices(session, allowScratch: true);
        string scratch = Path.Combine(session, ScratchName);
        EnsureSafeDirectory(scratch);
        RequireKnownScratchFiles(scratch);

        string commitPath = Path.Combine(scratch, CommitName);
        string draftPath = Path.Combine(scratch, DraftName);
        string commitDraftPath = Path.Combine(scratch, CommitDraftName);
        string inputPath = Path.Combine(scratch, InputName);
        string subtitlePath = Path.Combine(scratch, SubtitleName);
        string archive = ArchiveDirectory(rootDirectory);
        EnsureSafeDirectory(archive);
        string sessionId = Path.GetFileName(session);
        string transcript = Path.Combine(archive, $"{sessionId}.md");

        if (TryAttributes(commitPath) is null)
        {
            if (TryAttributes(transcript) is not null)
                throw new IOException("同名文字稿已存在，已保留录音。");

            DeletePlainFileIfPresent(draftPath);
            DeletePlainFileIfPresent(commitDraftPath);
            var speech = new List<Cue>();
            var sources = new List<CommittedAudio>();
            try
            {
                foreach (AudioSlice slice in audio)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DeletePlainFileIfPresent(inputPath);
                    DeletePlainFileIfPresent(subtitlePath);
                    string hash = await HashFileAsync(slice.Path, cancellationToken);
                    bool hasNonSilentAudio = await DecodeAsync(slice.Path, inputPath, scratch, cancellationToken);
                    if (hasNonSilentAudio)
                    {
                        CheckLocalModel();
                        IReadOnlyList<Cue> cues = await TranscribeAsync(
                            slice, inputPath, subtitlePath, scratch, cancellationToken);
                        speech.AddRange(cues);
                    }
                    sources.Add(new CommittedAudio(Path.GetFileName(slice.Path), hash));
                }
            }
            finally
            {
                DeletePlainFileIfPresent(inputPath);
                DeletePlainFileIfPresent(subtitlePath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] markdown = Utf8.GetBytes(BuildMarkdown(sessionId, speech));
            var commit = new ArchiveCommit(Convert.ToHexString(SHA256.HashData(markdown)), sources.ToArray());
            WriteNewFile(draftPath, markdown);
            WriteNewFile(commitDraftPath, JsonSerializer.SerializeToUtf8Bytes(commit));
            File.Move(commitDraftPath, commitPath);
        }

        // The commit is durable before publication. A later run can finish cleanup
        // even if several source files were already removed before interruption.
        RequirePlainFile(commitPath);
        ArchiveCommit committed = JsonSerializer.Deserialize<ArchiveCommit>(
            await File.ReadAllBytesAsync(commitPath, cancellationToken))
            ?? throw new InvalidDataException("文字稿提交标记无效。");
        ValidateCommit(committed);

        if (TryAttributes(transcript) is null)
        {
            await VerifyHashAsync(draftPath, committed.TranscriptHash, cancellationToken);
            RequireSafeDirectory(archive);
            File.Move(draftPath, transcript);
        }
        await VerifyHashAsync(transcript, committed.TranscriptHash, cancellationToken);

        var committedFiles = committed.Audio.ToDictionary(source => source.Name, StringComparer.Ordinal);
        foreach (AudioSlice slice in ReadAudioSlices(session, allowScratch: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!committedFiles.TryGetValue(Path.GetFileName(slice.Path), out CommittedAudio? source))
                throw new InvalidDataException("录音文件与已保存文字稿的清单不同。");

            await VerifyHashAsync(slice.Path, source.Hash, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        RequireOwnedSession(session);
        await VerifyHashAsync(transcript, committed.TranscriptHash, cancellationToken);
        foreach (CommittedAudio source in committed.Audio)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletePlainFileIfPresent(Path.Combine(session, source.Name));
        }

        DeletePlainFileIfPresent(inputPath);
        DeletePlainFileIfPresent(subtitlePath);
        DeletePlainFileIfPresent(draftPath);
        DeletePlainFileIfPresent(commitDraftPath);
        DeletePlainFileIfPresent(Path.Combine(session, ReadyName));
        DeletePlainFileIfPresent(commitPath);
        Directory.Delete(scratch);
        DeletePlainFileIfPresent(Path.Combine(session, SessionMarkerName));
        Directory.Delete(session);
    }

    private async Task<bool> DecodeAsync(
        string source, string destination, string workingDirectory, CancellationToken cancellationToken)
    {
        CommandResult result = await RunAsync(
            ffmpegPath,
            [
                "-hide_banner", "-nostdin", "-loglevel", "info", "-xerror", "-protocol_whitelist", "file,pipe",
                "-f", "ogg", "-i", source, "-map", "0:a:0",
                "-af", "aformat=sample_fmts=s16:sample_rates=16000:channel_layouts=mono,volumedetect",
                "-c:a", "pcm_s16le", "-rf64", "auto", "-n", destination
            ],
            workingDirectory,
            cancellationToken);
        if (result.ExitCode != 0)
            throw new IOException($"无法读取录音（FFmpeg 退出码 {result.ExitCode}）。");

        RequirePlainFile(destination);
        Match peakMatch = PeakPattern.Match(result.Error);
        if (!peakMatch.Success ||
            !double.TryParse(peakMatch.Groups["peak"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double peak))
        {
            throw new InvalidDataException("无法验证解码后的音频。");
        }

        return peak > SilencePeakDb;
    }

    private async Task<IReadOnlyList<Cue>> TranscribeAsync(
        AudioSlice slice, string input, string subtitle, string workingDirectory, CancellationToken cancellationToken)
    {
        async Task<CommandResult> RunWhisperAsync(string device, string computeType) =>
            await RunAsync(
                fasterWhisperPath,
                [
                    input, "--model", "large-v3-turbo", "--model_dir", Path.GetDirectoryName(FullModelPath())!,
                    "--device", device, "--compute_type", computeType, "--task", "transcribe",
                    "--multilingual", "true", "--output_format", "srt", "--output_dir", workingDirectory,
                    "--beep_off"
                ],
                workingDirectory,
                cancellationToken);

        if (!Path.Exists(fasterWhisperPath))
            throw new FileNotFoundException("找不到 Faster-Whisper-XXL，请检查转写程序路径。");

        CommandResult result = await RunWhisperAsync("cuda", "int8_float16");
        if (result.ExitCode != 0 || TryAttributes(subtitle) is null)
        {
            DeletePlainFileIfPresent(subtitle);
            cancellationToken.ThrowIfCancellationRequested();
            result = await RunWhisperAsync("cpu", "int8");
        }
        if (result.ExitCode != 0 || TryAttributes(subtitle) is null)
            throw new IOException($"本地转写未完成（退出码 {result.ExitCode}），已保留录音。");

        RequirePlainFile(subtitle);
        string srt = await File.ReadAllTextAsync(subtitle, Utf8, cancellationToken);
        IReadOnlyList<Cue> cues = ParseSubtitles(srt, slice);
        if (cues.Count == 0)
            throw new InvalidDataException("转写没有识别出可保存的语句，已保留有声录音。");

        return cues;
    }

    private void CheckLocalModel()
    {
        string directory = FullModelPath();
        if (!string.Equals(Path.GetFileName(directory), ModelDirectoryName, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(directory))
        {
            throw new FileNotFoundException("找不到已下载的 large-v3-turbo 模型目录。");
        }

        foreach (string name in new[] { "model.bin", "config.json", "tokenizer.json", "vocabulary.json" })
        {
            var file = new FileInfo(Path.Combine(directory, name));
            if (!file.Exists || file.Length == 0)
                throw new FileNotFoundException($"本地模型缺少 {name}。");
        }
    }

    private string FullModelPath() => Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelPath));

    private static async Task<CommandResult> RunAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ProcessEncoding,
            StandardErrorEncoding = ProcessEncoding
        };
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8:replace";
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new IOException("无法启动本地音频处理程序。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already finished.
            }
            await process.WaitForExitAsync();
            throw;
        }

        await stdout;
        return new CommandResult(process.ExitCode, await stderr);
    }

    private static IReadOnlyList<Cue> ParseSubtitles(string srt, AudioSlice slice)
    {
        using var reader = new StringReader(srt);
        var cues = new List<Cue>();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!int.TryParse(line.TrimStart('\uFEFF').Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("无法读取转写字幕的段落编号。");
            string timeLine = reader.ReadLine() ?? string.Empty;
            Match time = SubtitleTimePattern.Match(timeLine.Trim());
            if (!time.Success)
                throw new InvalidDataException("无法读取转写字幕的时间戳。");
            long start = ParseSrtTime(time.Groups["start"].Value);
            long end = ParseSrtTime(time.Groups["end"].Value);
            if (end < start)
                throw new InvalidDataException("转写字幕的时间范围无效。");

            var words = new List<string>();
            while (reader.ReadLine() is { } text && !string.IsNullOrWhiteSpace(text))
                words.Add(text.Trim());

            if (words.Count > 0)
                cues.Add(new Cue(slice.OffsetMs + start, slice.Speaker, string.Join(" ", words)));
        }
        return cues;
    }

    private static long ParseSrtTime(string value)
    {
        string[] fields = value.Split(':', ',', '.');
        int hours = int.Parse(fields[0], CultureInfo.InvariantCulture);
        int minutes = int.Parse(fields[1], CultureInfo.InvariantCulture);
        int seconds = int.Parse(fields[2], CultureInfo.InvariantCulture);
        int milliseconds = int.Parse(fields[3], CultureInfo.InvariantCulture);
        if (minutes >= 60 || seconds >= 60)
            throw new InvalidDataException("转写字幕的时间戳无效。");

        return ((hours * 60L + minutes) * 60 + seconds) * 1000 + milliseconds;
    }

    private static string BuildMarkdown(string sessionId, IEnumerable<Cue> cues)
    {
        var text = new StringBuilder();
        foreach (Cue cue in cues.OrderBy(cue => cue.OffsetMs).ThenBy(cue => cue.Speaker, StringComparer.Ordinal))
        {
            long seconds = cue.OffsetMs / 1000;
            text.Append(CultureInfo.InvariantCulture,
                $"[{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}] {cue.Speaker}：{cue.Text}\n");
        }

        string heading = $"# VRChat 文字稿\n\n会话 {sessionId}\n\n";
        if (text.Length == 0)
            return heading + "本次没有可转写的语音。\n";

        // Keep recognized speech literal even if it contains Markdown or HTML.
        string body = text.ToString();
        int longest = 2;
        int run = 0;
        foreach (char character in body)
        {
            run = character == '\u0060' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string fence = new('\u0060', longest + 1);
        return $"{heading}{fence}text\n{body}{fence}\n";
    }

    private static IReadOnlyList<AudioSlice> ReadAudioSlices(string session, bool allowScratch)
    {
        var slices = new List<AudioSlice>();
        RequireSafeDirectory(session);
        foreach (string path in Directory.EnumerateFileSystemEntries(session).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (name is SessionMarkerName or ReadyName)
            {
                RequirePlainFile(path);
                continue;
            }
            if (name == ScratchName && allowScratch)
            {
                RequireSafeDirectory(path);
                continue;
            }

            Match match = AudioNamePattern.Match(name);
            if (!match.Success)
                throw new InvalidDataException("会话中有未完成或无法识别的录音文件。");

            RequirePlainFile(path);
            string speaker = match.Groups["kind"].Value == "mic" ? "我" : "VRChat";
            long offset = long.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture);
            slices.Add(new AudioSlice(path, offset, speaker));
        }
        return slices;
    }

    private static void RequireKnownScratchFiles(string scratch)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(scratch))
        {
            if (Path.GetFileName(path) is not (InputName or SubtitleName or DraftName or CommitName or CommitDraftName))
                throw new InvalidDataException("转写目录里有无法识别的文件。");
            RequirePlainFile(path);
        }
    }

    private static void ValidateCommit(ArchiveCommit commit)
    {
        if (commit.TranscriptHash is null || !HashPattern.IsMatch(commit.TranscriptHash) || commit.Audio is null)
            throw new InvalidDataException("文字稿校验清单无效。");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CommittedAudio source in commit.Audio)
        {
            if (source is null || source.Name is null || source.Hash is null ||
                !AudioNamePattern.IsMatch(source.Name) || !HashPattern.IsMatch(source.Hash) || !seen.Add(source.Name))
            {
                throw new InvalidDataException("音频校验清单无效。");
            }
        }
    }

    private static async Task VerifyHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (!string.Equals(await HashFileAsync(path, cancellationToken), expected, StringComparison.Ordinal))
            throw new InvalidDataException("文件与保存时的校验值不同，已保留仍存在的音频。");
    }

    private static async Task<string> HashFileAsync(string file, CancellationToken cancellationToken)
    {
        RequirePlainFile(file);
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool IsSessionId(string name) =>
        SessionIdPattern.IsMatch(name) &&
        DateTime.TryParseExact(name.AsSpan(0, 19), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    private static void RequireOwnedSession(string session)
    {
        string? pending = Path.GetDirectoryName(session);
        string? storage = pending is null ? null : Path.GetDirectoryName(pending);
        if (!IsSessionId(Path.GetFileName(session)) ||
            Path.GetFileName(pending) != PendingName || Path.GetFileName(storage) != StorageName)
        {
            throw new InvalidDataException("会话目录不属于此录音工具。");
        }

        RequireSafeDirectory(session);
        string marker = Path.Combine(session, SessionMarkerName);
        RequirePlainFile(marker);
        using var file = File.OpenRead(marker);
        if (file.Length > 4096)
            throw new InvalidDataException("录音会话标记无效。");

        using JsonDocument data = JsonDocument.Parse(file);
        if (data.RootElement.ValueKind != JsonValueKind.Object ||
            !data.RootElement.TryGetProperty("schema", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1)
        {
            throw new InvalidDataException("录音会话标记无效。");
        }
    }

    private static void WriteNewFile(string path, ReadOnlySpan<byte> bytes)
    {
        RequireSafeDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void RequirePlainFile(string path)
    {
        RequireSafeDirectory(Path.GetDirectoryName(path)!);
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("路径不是可处理的普通文件。");
    }

    private static void DeletePlainFileIfPresent(string path)
    {
        if (TryAttributes(path) is null)
            return;

        RequirePlainFile(path);
        File.Delete(path);
    }

    private static void RequireSafeDirectory(string path) => CheckDirectoryTree(path, allowCreate: false);

    private static void EnsureSafeDirectory(string path) => CheckDirectoryTree(path, allowCreate: true);

    private static void CheckDirectoryTree(string path, bool allowCreate)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parents = new Stack<string>();
        for (string? current = fullPath; current is not null; current = Path.GetDirectoryName(current))
            parents.Push(current);

        while (parents.TryPop(out string? current))
        {
            FileAttributes? attributes = TryAttributes(current);
            if (attributes is null && allowCreate)
            {
                Directory.CreateDirectory(current);
                attributes = File.GetAttributes(current);
            }

            if (attributes is null ||
                (attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            {
                throw new InvalidDataException("数据目录不存在或包含目录链接。");
            }
        }
    }

    private static FileAttributes? TryAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool IsExpectedFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            Win32Exception or DecoderFallbackException;

    private sealed record AudioSlice(string Path, long OffsetMs, string Speaker);
    private sealed record Cue(long OffsetMs, string Speaker, string Text);
    private sealed record CommandResult(int ExitCode, string Error);
    private sealed record CommittedAudio(string Name, string Hash);
    private sealed record ArchiveCommit(string TranscriptHash, CommittedAudio[] Audio);
}
