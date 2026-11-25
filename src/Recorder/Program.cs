namespace VrchatSessionRecorder;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var singleInstance = new Mutex(true, @"Local\VrchatSessionRecorder", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return 2;
        }

        try
        {
            if (args is ["--configure", var path, var deviceName])
            {
                if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                {
                    return 3;
                }

                var devices = AudioCapture.ListMicrophones().Where(device =>
                    device.DisplayName.Contains(deviceName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (string.IsNullOrWhiteSpace(deviceName) || devices.Length != 1)
                {
                    return 4;
                }

                (RecorderSettings.Load() with
                {
                    RecordingRoot = Path.GetFullPath(path),
                    MicrophoneId = devices[0].Id,
                }).Save();
                return 0;
            }

            if (args is ["--set-autostart", "on"] or ["--set-autostart", "off"])
            {
                RecorderSettings.SetAutoStart(args[1] == "on");
                return 0;
            }

            if (args.Length != 0)
            {
                return 3;
            }

            ApplicationConfiguration.Initialize();
            using var tray = new RecorderTray(RecorderSettings.Load());
            Application.Run(tray);
            return 0;
        }
        catch (Exception error)
        {
            RecorderSettings.Log(error.Message);
            if (args.Length == 0)
            {
                MessageBox.Show(error.Message, "VRChat 录音", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }
        finally
        {
            singleInstance.ReleaseMutex();
        }
    }
}
