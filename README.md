# VRChat 后台录音

Windows 托盘工具。开 VRChat 后自动录下自己开麦时的发言，退游后在本机转成带时间的文字稿。可以按次开启 VRChat 播放声的第二轨。成功保存文字稿后，工具立即删除本次的音频。

Windows tray recorder with local, timestamped transcription. It requires the OSC feature already provided by VRChat.

## 安装

1. 从本仓库的 Releases 下载 Windows x64 ZIP，解压到一个固定目录。
2. 安装包含 Opus 编码器的 FFmpeg。用 VideoCaptioner 下载本地 Faster-Whisper-XXL 和「large-v3-turbo」模型。工具会尝试查找这些组件，也可以在设置中直接选择程序和模型文件夹。工具使用本机已有的模型文件，无需给聊天配置云端 API。
3. 打开 **VrchatSessionRecorder.exe**。选择输出目录和你在 VRChat 中使用的麦克风。如果希望以后每次开游戏都能自动录，在设置中勾选「登录 Windows 时后台启动」。
4. 打开 VRChat，在 **Action Menu → Options → OSC** 启用 OSC。首次使用时先私下验证一轮，开麦说一句，静音再说一句，退游后检查文字稿。工具需要从 OSC 确认当前开麦状态。状态未知时，托盘会显示「麦克风关闭或等待 OSC」并暂停保存麦克风音频。 [VRChat OSC 说明](https://docs.vrchat.com/docs/osc-overview)

## 日常使用

- 照常从 Steam 打开或退出 VRChat。工具在 Windows 托盘中显示状态。游戏内静音、Windows 锁屏或托盘暂停时，麦克风音频会停止写入。
- 对方同意后，在托盘里点「已获同意 · 本次也录 VRChat 声音」。再次点击可停止第二轨。退出游戏、暂停或锁屏会清除这次选择。第二轨是当时 VRChat 播放的混音，可能同时包含多人、世界音效或游戏里的音乐。对方声音的轨道标为「VRChat」。
- 退出游戏后，托盘会显示转写状态。转写成功的会话在输出目录的「文字稿」下生成一份 Markdown。程序只清理它自己创建并成功处理的音频。转写失败时，原音频留在「.vrchat-recorder」子目录，可从托盘重试。突然中断留下的未封装录音会保留，日志会显示会话编号，方便手动检查。重新进游戏时，待完成的转写会延后到下次退出。
- 退出工具可使用托盘菜单。要停止登录启动，先在设置里取消勾选。应用日志和设置位于当前用户的「AppData\Local\VrchatSessionRecorder」。

## 想复盘时

打开想看的文字稿，挑一小段交给你自己选择的 AI。可以这样说：

> 帮我从这小段对话挑最多 3 条值得记的东西。优先找明显影响交流的用词或搭配。保留原句和时间，给我一句下次能顺口说的版本。如果我中间卡住了，先让我确认当时想表达什么。口语省略可以保留，疑似转写错的地方标为不确定。

有几场双轨稿后，可以请 AI 从「VRChat」发言里找跨场重复的词和短语，再自己勾选没听懂的几个。音频在转写成功后会删除，回顾主要看文字稿。AI 复盘由你手动发起，分享别人的话前先确认对方同意。

## 从源码构建

使用 .NET 10 SDK。在项目目录运行：

    dotnet build src/Recorder/Recorder.csproj -c Release
    dotnet publish src/Recorder/Recorder.csproj -c Release -r win-x64 --self-contained -o artifacts/win-x64

可以用合成数据运行以下检查。录音检查需要本机安装 FFmpeg。

    dotnet run --project tests/OscSmoke -c Release -- --network
    dotnet run --project tests/CaptureSmoke -c Release
    dotnet run --project tests/TranscriptionSmoke -c Release

工具通过 Windows WASAPI 共享模式录音，通过 VRChat 的只读 OSC 状态控制麦克风写入。项目采用 MIT 许可。
