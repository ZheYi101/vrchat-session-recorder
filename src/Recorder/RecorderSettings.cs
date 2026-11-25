using System.Text.Json;
using Microsoft.Win32;

namespace VrchatSessionRecorder;

internal sealed record RecorderSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "VrchatSessionRecorder";
    private static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string SettingsFolder = Path.Combine(LocalData, AppName);
    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.json");
    private static readonly string CaptionerRoot = Path.Combine(LocalData, "VideoCaptioner", "VideoCaptioner");

    public string RecordingRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VRChat 录音");
    public string? MicrophoneId { get; init; }
    public string FfmpegPath { get; init; } = FindOnPath("ffmpeg.exe");
    public string WhisperPath { get; init; } = Path.Combine(
        CaptionerRoot, "resource", "bin", "Faster-Whisper-XXL", "faster-whisper-xxl.exe");
    public string ModelPath { get; init; } = Path.Combine(
        CaptionerRoot, "models", "faster-whisper-large-v3-turbo");

    public bool Configured => !string.IsNullOrWhiteSpace(MicrophoneId) &&
        IsAbsolute(RecordingRoot) && IsAbsolute(FfmpegPath) &&
        IsAbsolute(WhisperPath) && IsAbsolute(ModelPath);

    public static RecorderSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<RecorderSettings>(File.ReadAllBytes(SettingsPath)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Log("读取设置失败。请重新保存设置。");
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsFolder);
        var temp = SettingsPath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this));
        File.Move(temp, SettingsPath, overwrite: true);
    }

    public static bool HasAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string command &&
            string.Equals(command, $"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(AppName, $"\"{Environment.ProcessPath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            var log = Path.Combine(SettingsFolder, "recorder.log");
            if (File.Exists(log) && new FileInfo(log).Length > 128 * 1024)
            {
                File.Move(log, log + ".old", overwrite: true);
            }

            File.AppendAllText(log, $"[{DateTimeOffset.Now:O}] {message.ReplaceLineEndings(" ")}\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FindOnPath(string fileName)
    {
        var entries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
        foreach (var entry in entries)
        {
            var directory = entry.Trim().Trim('"');
            if (Path.IsPathFullyQualified(directory))
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return string.Empty;
    }

    private static bool IsAbsolute(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
}
