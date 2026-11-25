using System.Diagnostics;
using Microsoft.Win32;

namespace VrchatSessionRecorder;

internal sealed class RecorderTray : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    private readonly VrchatMuteMonitor _mute = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _notice = new() { Enabled = false, Visible = false };
    private readonly ToolStripMenuItem _gameAudio = new();
    private readonly ToolStripMenuItem _pause = new();
    private readonly ToolStripMenuItem _settingsItem = new("设置…");
    private readonly NotifyIcon _tray;
    private readonly int _windowsSessionId = Process.GetCurrentProcess().SessionId;
    private RecorderSettings _settings;
    private AudioCapture? _capture;
    private string? _sessionDirectory;
    private string? _requestedMicrophone;
    private int? _requestedGame;
    private long _nextGameTrackCheck;
    private int _captureProcessId;
    private int _observedProcessId;
    private int _optedInProcessId;
    private bool _paused;
    private bool _locked;
    private bool _exiting;
    private bool _updating;
    private bool _settingsOpen;
    private bool _processPending = true;
    private string? _captureError;
    private string? _lastNotice;
    private string? _gamePlaybackMuteNotice;
    private CancellationTokenSource? _queueCancellation;
    private Task? _queueTask;

    public RecorderTray(RecorderSettings settings)
    {
        _settings = settings;
        _ = _dispatcher.Handle;
        _status.Text = "等待 VRChat";
        _gameAudio.Click += (_, _) => ChangeGameAudio();
        _pause.Click += (_, _) => TogglePause();
        _settingsItem.Click += (_, _) => ShowSettings();
        _menu.Items.AddRange([
            _status, _notice, new ToolStripSeparator(), _gameAudio, _pause,
            new ToolStripMenuItem("打开文字稿文件夹", null, (_, _) => OpenArchive()),
            new ToolStripMenuItem("重试待转写录音", null, (_, _) => _processPending = true),
            _settingsItem, new ToolStripSeparator(),
            new ToolStripMenuItem("退出录音工具", null, async (_, _) => await ShutDownAsync()),
        ]);
        _tray = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "VRChat 录音",
        };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _mute.Changed += _ => Dispatch(UpdateStatus);
        _mute.StatusChanged += notice => Dispatch(() =>
        {
            if (_observedProcessId != 0)
            {
                _lastNotice = notice;
                UpdateStatus();
            }
        });
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        UpdateStatus();
        if (!_settings.Configured)
        {
            _dispatcher.BeginInvoke(ShowSettings);
        }
    }

    private async Task TickAsync()
    {
        if (_exiting)
        {
            return;
        }

        ObserveGame();
        if (_updating || _settingsOpen)
        {
            return;
        }

        _updating = true;
        try
        {
            var processId = _observedProcessId;
            if (_captureProcessId != processId)
            {
                await CloseCaptureAsync();
                _captureProcessId = processId;
                _captureError = null;
            }

            if (processId != 0 && MayRecordProcess(processId) && _settings.Configured && _captureError is null)
            {
                await UpdateCaptureAsync(processId, _settings.MicrophoneId, _optedInProcessId == processId);
            }
            else if (_capture is not null && (_paused || _locked || _captureError is not null))
            {
                await SetTracksAsync(null, null);
            }

            if (!_exiting && _observedProcessId == 0 && _captureProcessId == 0 && _capture is null &&
                _queueTask is null && _processPending && _settings.Configured)
            {
                StartQueue();
            }
        }
        catch (Exception error)
        {
            _captureError = "录音遇到问题，请查看日志。";
            RecorderSettings.Log(error.Message);
        }
        finally
        {
            _updating = false;
            UpdateStatus();
        }
    }

    private void ObserveGame()
    {
        var found = FindGameProcess();
        if (found == _observedProcessId)
        {
            return;
        }

        Volatile.Write(ref _observedProcessId, found);
        Volatile.Write(ref _optedInProcessId, 0);
        _lastNotice = null;
        _mute.SetGameProcess(found == 0 || _locked ? null : found);
        if (found != 0)
        {
            _queueCancellation?.Cancel();
        }
        else
        {
            _processPending = true;
        }
        UpdateStatus();
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        if (eventArgs.Reason == SessionSwitchReason.SessionLock)
        {
            Volatile.Write(ref _locked, true);
            Volatile.Write(ref _optedInProcessId, 0);
            Dispatch(() =>
            {
                _mute.SetGameProcess(null);
                UpdateStatus();
            });
        }
        else if (eventArgs.Reason == SessionSwitchReason.SessionUnlock)
        {
            Dispatch(() =>
            {
                _mute.SetGameProcess(_observedProcessId == 0 ? null : _observedProcessId);
                Volatile.Write(ref _locked, false);
                UpdateStatus();
            });
        }
    }

    private int FindGameProcess()
    {
        var processes = Process.GetProcessesByName("VRChat");
        var selected = 0;
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != _windowsSessionId || process.HasExited)
                    {
                        continue;
                    }
                    if (selected == 0 || process.Id == _observedProcessId)
                    {
                        selected = process.Id;
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process can exit while Windows is enumerating it.
                }
            }
        }
        return selected;
    }

    private async Task UpdateCaptureAsync(int processId, string? deviceId, bool includeGame)
    {
        if (_capture is null)
        {
            var id = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..8]}";
            _sessionDirectory = TranscriptionQueue.InitializeSession(_settings.RecordingRoot, id);
            _capture = new AudioCapture(
                _settings.FfmpegPath, _sessionDirectory, Stopwatch.GetTimestamp(),
                () => MayRecordProcess(processId) && _mute.IsUnmuted,
                () => MayRecordProcess(processId) && Volatile.Read(ref _optedInProcessId) == processId);
            _capture.Faulted += error => Dispatch(() =>
            {
                _lastNotice = "一条音轨遇到问题，请查看日志。";
                RecorderSettings.Log(error);
                UpdateStatus();
            });
        }
        await SetTracksAsync(deviceId, includeGame ? processId : null);
    }

    private bool MayRecordProcess(int processId) =>
        Volatile.Read(ref _observedProcessId) == processId &&
        !Volatile.Read(ref _paused) && !Volatile.Read(ref _locked) && !Volatile.Read(ref _exiting);

    private async Task SetTracksAsync(string? mic, int? game)
    {
        if (_capture is null)
        {
            return;
        }
        Task micChange = Task.CompletedTask;
        if (mic != _requestedMicrophone)
        {
            _requestedMicrophone = mic;
            micChange = _capture.SetMicrophoneAsync(mic);
        }

        Task gameChange = Task.CompletedTask;
        Task<string?>? playbackCheck = null;
        long now = Stopwatch.GetTimestamp();
        if (game != _requestedGame || (game.HasValue && now >= _nextGameTrackCheck))
        {
            if (game.HasValue && game != _requestedGame)
                TranscriptionQueue.MarkGameRequested(_sessionDirectory!);

            _requestedGame = game;
            _nextGameTrackCheck = now + 3 * Stopwatch.Frequency;
            gameChange = _capture.SetGameAsync(game);
            if (game is int gameProcessId)
                playbackCheck = Task.Run(() => AudioCapture.GetGamePlaybackMuteNotice(gameProcessId));
            else
                _gamePlaybackMuteNotice = null;
        }

        await Task.WhenAll(micChange, gameChange);
        if (playbackCheck is not null)
            _gamePlaybackMuteNotice = await playbackCheck;
    }

    private async Task CloseCaptureAsync()
    {
        var capture = _capture;
        var directory = _sessionDirectory;
        _capture = null;
        _sessionDirectory = null;
        _requestedMicrophone = null;
        _requestedGame = null;
        _gamePlaybackMuteNotice = null;
        if (capture is null || directory is null)
        {
            return;
        }

        try
        {
            await capture.FinishAsync();
            TranscriptionQueue.MarkClosed(directory);
        }
        catch (Exception error)
        {
            _lastNotice = "有一段录音待恢复。已保存可用数据。";
            RecorderSettings.Log(error.Message);
        }
        finally
        {
            await capture.DisposeAsync();
            _processPending = true;
        }
    }

    private void StartQueue()
    {
        _processPending = false;
        var settings = _settings;
        var cancellation = new CancellationTokenSource();
        _queueCancellation = cancellation;
        _queueTask = Task.Run(async () =>
        {
            try
            {
                var workDirectory = TranscriptionQueue.WorkDirectory(settings.RecordingRoot);
                if (Directory.Exists(workDirectory))
                {
                    foreach (var directory in Directory.EnumerateDirectories(workDirectory))
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (!TranscriptionQueue.HasValidSessionMarker(directory))
                        {
                            continue;
                        }
                        try
                        {
                            TranscriptionQueue.MarkClosed(directory);
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            var session = Path.GetFileName(directory);
                            RecorderSettings.Log($"录音 {session} 待恢复，已保留原文件。{error.Message}");
                            Dispatch(() =>
                            {
                                if (_observedProcessId == 0)
                                {
                                    _lastNotice = $"有未完成的录音 {session}。详情见日志。";
                                    UpdateStatus();
                                }
                            });
                        }
                    }
                }
                var queue = new TranscriptionQueue(settings.FfmpegPath, settings.WhisperPath, settings.ModelPath, settings.RecordingRoot);
                queue.StatusChanged += notice => Dispatch(() =>
                {
                    if (_observedProcessId == 0)
                    {
                        _lastNotice = notice;
                        UpdateStatus();
                    }
                });
                await queue.ProcessPendingAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Dispatch(() => _processPending = true);
            }
            catch (Exception error)
            {
                RecorderSettings.Log(error.Message);
                Dispatch(() => _lastNotice = "转写遇到问题。录音已保留，可从托盘重试。");
            }
            finally
            {
                Dispatch(() =>
                {
                    _queueTask = null;
                    _queueCancellation = null;
                    cancellation.Dispose();
                    UpdateStatus();
                });
            }
        });
    }

    private void ChangeGameAudio()
    {
        if (_observedProcessId == 0 || _paused || _locked || !_settings.Configured)
        {
            return;
        }
        var state = _optedInProcessId == _observedProcessId ? 0 : _observedProcessId;
        Volatile.Write(ref _optedInProcessId, state);
        UpdateStatus();
    }

    private void TogglePause()
    {
        Volatile.Write(ref _paused, !_paused);
        if (!_paused)
        {
            _captureError = null;
        }
        else
        {
            Volatile.Write(ref _optedInProcessId, 0);
        }
        UpdateStatus();
    }

    private void ShowSettings()
    {
        if (_settingsOpen || _updating || _exiting || (_settings.Configured && _observedProcessId != 0))
        {
            return;
        }

        _settingsOpen = true;
        try
        {
            using var dialog = new SettingsDialog(_settings);
            if (dialog.ShowDialog() == DialogResult.OK && dialog.Result is { } result)
            {
                _queueCancellation?.Cancel();
                _settings = result;
                _processPending = true;
                _captureError = null;
            }
        }
        catch (Exception error)
        {
            RecorderSettings.Log(error.Message);
            _lastNotice = "打不开设置。请查看日志。";
        }
        finally
        {
            _settingsOpen = false;
            UpdateStatus();
        }
    }

    private void OpenArchive()
    {
        try
        {
            var directory = Directory.CreateDirectory(TranscriptionQueue.ArchiveDirectory(_settings.RecordingRoot));
            Process.Start(new ProcessStartInfo { FileName = directory.FullName, UseShellExecute = true });
        }
        catch (Exception error)
        {
            RecorderSettings.Log(error.Message);
            _lastNotice = "打不开文字稿目录。请检查设置。";
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        var active = _observedProcessId != 0;
        var includeGame = active && _optedInProcessId == _observedProcessId;
        _settingsItem.Enabled = !active || !_settings.Configured;
        _gameAudio.Enabled = active && !_paused && !_locked && _settings.Configured;
        _gameAudio.Text = includeGame ? "停止录本次 VRChat 声音" : "已获同意 · 本次也录 VRChat 声音";
        _pause.Text = _paused ? "恢复录音" : "暂停录音";

        var gameStatus = _capture?.HasRecentGameSignal == true
            ? "已收到 VRChat 声音"
            : _gamePlaybackMuteNotice ?? "第二轨已开启，等待 VRChat 声音";
        _status.Text = !_settings.Configured ? "请先完成设置" :
            _paused || _locked ? "已暂停录音" :
            _captureError is not null ? _captureError :
            active ? (_mute.IsUnmuted ? "正在录自己的麦克风" : "麦克风关闭或等待 OSC") +
                (includeGame ? " · " + gameStatus : "") :
            _queueTask is not null ? "正在检查或转写录音" :
            "等待 VRChat";
        _tray.Text = _status.Text.Length > 63 ? _status.Text[..63] : _status.Text;

        _notice.Text = _lastNotice ?? string.Empty;
        _notice.Available = !string.IsNullOrWhiteSpace(_lastNotice);
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.IsDisposed)
        {
            return;
        }
        try
        {
            _dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Windows may be closing the tray at the same time.
        }
    }

    private async Task ShutDownAsync()
    {
        if (_exiting)
        {
            return;
        }
        Volatile.Write(ref _exiting, true);
        _timer.Stop();
        _queueCancellation?.Cancel();
        while (_updating)
        {
            await Task.Delay(30);
        }
        await CloseCaptureAsync();
        if (_queueTask is { } task)
        {
            await task;
        }
        await _mute.DisposeAsync();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _timer.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
