namespace VrchatSessionRecorder;

internal sealed class SettingsDialog : Form
{
    private readonly TextBox _root;
    private readonly ComboBox _microphone;
    private readonly TextBox _ffmpeg;
    private readonly TextBox _whisper;
    private readonly TextBox _model;
    private readonly CheckBox _autoStart;

    public RecorderSettings? Result { get; private set; }

    public SettingsDialog(RecorderSettings settings)
    {
        Text = "VRChat 录音设置";
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 384);
        ClientSize = new Size(680, 345);
        ShowInTaskbar = true;
        MaximizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 3,
            RowCount = 8,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var introduction = new Label
        {
            Text = "选择和 VRChat 相同的麦克风，并在游戏的操作菜单中启用 OSC。",
            AutoSize = true,
            Margin = new Padding(3, 2, 3, 16),
        };
        layout.Controls.Add(introduction, 0, 0);
        layout.SetColumnSpan(introduction, 3);

        _root = AddPath(layout, 1, "保存位置", settings.RecordingRoot, folder: true);
        AddLabel(layout, 2, "麦克风");
        _microphone = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DisplayMember = nameof(AudioDevice.DisplayName),
            DataSource = AudioCapture.ListMicrophones().ToArray(),
            AccessibleName = "麦克风",
        };
        _microphone.SelectedItem = _microphone.Items.OfType<AudioDevice>()
            .FirstOrDefault(device => device.Id == settings.MicrophoneId);
        layout.Controls.Add(_microphone, 1, 2);
        layout.SetColumnSpan(_microphone, 2);

        _ffmpeg = AddPath(layout, 3, "FFmpeg", settings.FfmpegPath, folder: false);
        _whisper = AddPath(layout, 4, "Faster-Whisper", settings.WhisperPath, folder: false);
        _model = AddPath(layout, 5, "模型文件夹", settings.ModelPath, folder: true);

        _autoStart = new CheckBox
        {
            Text = "登录 Windows 时后台启动",
            Checked = RecorderSettings.HasAutoStart(),
            AutoSize = true,
            Margin = new Padding(3, 10, 3, 3),
        };
        layout.Controls.Add(_autoStart, 1, 6);
        layout.SetColumnSpan(_autoStart, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 12, 0, 0),
        };
        var save = new Button { Text = "保存", AutoSize = true };
        var cancel = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveSelection(settings);
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 7);
        layout.SetColumnSpan(buttons, 3);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    private void SaveSelection(RecorderSettings previous)
    {
        if (_microphone.SelectedItem is not AudioDevice device)
        {
            ShowValidation("请先选择麦克风。");
            return;
        }

        var settings = previous with
        {
            RecordingRoot = CleanPath(_root.Text),
            MicrophoneId = device.Id,
            FfmpegPath = CleanPath(_ffmpeg.Text),
            WhisperPath = CleanPath(_whisper.Text),
            ModelPath = CleanPath(_model.Text),
        };

        if (!settings.Configured)
        {
            ShowValidation("请填写完整路径。");
            return;
        }

        if (!File.Exists(settings.FfmpegPath) || !File.Exists(settings.WhisperPath) ||
            !File.Exists(Path.Combine(settings.ModelPath, "model.bin")))
        {
            ShowValidation("请检查 FFmpeg、Faster-Whisper 和本地模型文件夹。");
            return;
        }

        try
        {
            Directory.CreateDirectory(settings.RecordingRoot);
            settings.Save();
            RecorderSettings.SetAutoStart(_autoStart.Checked);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowValidation(error.Message);
            return;
        }

        Result = settings;
        DialogResult = DialogResult.OK;
    }

    private void ShowValidation(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static string CleanPath(string input) => input.Trim().Trim('"');

    private static TextBox AddPath(TableLayoutPanel parent, int row, string label, string value, bool folder)
    {
        AddLabel(parent, row, label);
        var box = new TextBox { Text = value, Dock = DockStyle.Fill, AccessibleName = label };
        var browse = new Button { Text = "浏览…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            if (folder)
            {
                using var dialog = new FolderBrowserDialog { SelectedPath = CleanPath(box.Text) };
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    box.Text = dialog.SelectedPath;
                }
            }
            else
            {
                using var dialog = new OpenFileDialog { Filter = "应用程序 (*.exe)|*.exe", CheckFileExists = true };
                if (File.Exists(CleanPath(box.Text)))
                {
                    dialog.FileName = CleanPath(box.Text);
                }

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    box.Text = dialog.FileName;
                }
            }
        };
        parent.Controls.Add(box, 1, row);
        parent.Controls.Add(browse, 2, row);
        return box;
    }

    private static void AddLabel(TableLayoutPanel parent, int row, string text) =>
        parent.Controls.Add(new Label
        {
            Text = text,
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Margin = new Padding(3, 8, 8, 3),
        }, 0, row);
}
