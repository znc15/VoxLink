# VoxLink Architecture

## 进程模型

```text
VoxLink.exe (WinUI 3 / .NET 10)
  |  stdin/stdout UTF-8 JSON Lines
  |  request: { id, method, params }
  |  response: { id, result | error }
  |  event: { event, data }
  v
engine/VoxLink.Engine.exe (.NET 10, self-contained win-x64)
  |-- independent WASAPI microphone and render-loopback capture
  |-- local, segmented-cloud, or streaming-cloud ASR
  |-- translation, optional LLM refinement, and TTS providers
  |-- optional local speaker embedding / cloud speaker IDs
  |-- VRChat OSC Chatbox sender and MuteSelf listener
  |-- RegisterHotKey / WM_HOTKEY
  |-- WPF topmost desktop subtitle overlay
  `-- Valve OpenVR / SteamVR subtitle overlay
```

WinUI 进程负责可见工作台、设置持久化、校验和应用生命周期。Engine 进程负责 Windows 音频、语音识别、翻译编排、语音输出、快捷键与两类字幕宿主。UI 启动一个 Engine，按数字 ID 关联请求，消费异步事件，并在退出前发送 `shutdown`。所有 stdin 写入由 `EngineClient` 串行化；进程关闭、崩溃、启动超时或 stdin 失败时，等待中的请求会立即结束。

普通设置保存在 `%APPDATA%\VoxLink\settings.json`。ASR、翻译和 TTS 的 API Key 及自定义请求头使用当前 Windows 用户 DPAPI 加密后写入 `%APPDATA%\VoxLink\secrets.dat`。首次启动的三步 `OnboardingDialog` 配置快速模式、语言、麦克风和可选虚拟声卡；WPF 仅存在于 Engine 子进程中，用于桌面字幕和 Windows 消息循环，主窗口完全由 WinUI 3 实现。

## 语音管线

麦克风和系统回环是独立音源，可单独启用。二者先转换为 16 kHz 单声道浮点 PCM，再按 ASR 协议进入不同路径：

```text
Microphone (outbound) -----+
                           +--> PCM 16 kHz mono
System loopback (inbound) -+          |
                                      +-- Local Whisper
                                      |     VAD / smart segmentation
                                      |     -> bounded final queue
                                      |
                                      +-- Segmented cloud
                                      |     VAD / smart segmentation
                                      |     -> WAV multipart or MiMo input_audio
                                      |     -> bounded final queue
                                      |
                                      `-- Streaming cloud
                                            one WebSocket per enabled source
                                            -> bounded audio queue (40, drop oldest)
                                            -> partial UI events
                                            -> final queue (8, drop oldest)
                                                        |
                                                        v
                                               serial final processing
                                            transcription-only OR translation
                                              + optional secondary target
                                              + optional LLM refinement
                                              + optional selected-text TTS
                                                        |
                         +------------------------------+-------------------+
                         |                              |                   |
                    WinUI history                desktop/SteamVR       VRChat Chatbox
                 partial + final messages          subtitles          eligible final
```

本地 Whisper 和分段云 ASR 消费 VAD 生成的完整 `AudioUtterance`。分段云协议包括 OpenAI/SiliconFlow 风格 WAV multipart，以及 MiMo `input_audio`。持续流式协议包括 DashScope 和 Soniox；每个启用音源持有独立 `IAsrStream`，WASAPI 回调只进行非阻塞 `TryWrite`，不会等待网络。

流式 partial 只发布字幕事件，不触发翻译、润色、TTS 或 Chatbox。每句话的 partial 与 final 共用稳定 `utteranceId`；WinUI 优先按 ID 原位更新，避免迟到 final 覆盖下一句 partial。final 和分段识别结果进入单读者有界队列，串行执行翻译和输出，限制陈旧语音积压。

流式连接异常时，仅对应音源按 1、2、4、8 秒退避重新建立连接；重连期间清空该音源尚未发送的音频，避免把旧音频错误拼到新会话。正常停止完成输入并给远端最多 6 秒完成关闭。断线期间音频不重放。

## 输出策略
- **快速模式**：`OscText` 和 `VrChatVoice` 都启用麦克风、关闭系统回环并启用 Chatbox；`VrChatVoice` 额外启用出站 TTS，并要求名称可识别的 VB-CABLE、Voicemeeter 或其他虚拟播放设备。系统回环仍可在高级音频页独立开启。
- **正常翻译**：final 原文翻译到主目标语言；设置第二目标语言时再生成第二译文。可选 LLM 润色分别处理两种译文。
- **仅转写**：原文作为显示文本并标记 `TranscriptionOnly=true`；不创建翻译服务、不润色、不播放 TTS、不发送 Chatbox。
- **TTS**：出站和入站由独立开关控制。最终 `Outbound` 与 `Typed` 可朗读识别原话及源语言，或主译文及目标语言；`Inbound` 始终朗读主译文，第二译文和 partial 永不朗读。默认回退顺序是 Edge、Google、Windows 已安装语音，实际播放设备通过 `VoiceOutputDeviceId` 传入。
- **简体中文**：公开翻译 provider 对简体中文使用 `zh-CN`，LLM 提示明确要求简体；面向 `zh-CN` 的 ASR 原文、主次译文和润色结果再由 `ChineseTextNormalizer` 调用 Windows `LCMapStringEx` 做最终简体归一化。DashScope、Soniox 和 MiMo ASR 的协议语言码仍为 `zh`。
- **字幕**：WinUI Live、桌面 Overlay 和 SteamVR Overlay 使用相同消息语义，显示 speaker、partial/final、主次译文和原文。仅转写不会重复显示“译文”行。
- **Chatbox**：只发送 final、非仅转写的 `Outbound` 或 `Typed` 主译文，可按设置附加原文。`Inbound`、partial、第二译文和 speaker 标签永不发送。发送器使用有界队列、短时去重、1.5 秒节流和 144 文本元素截断。

## 说话人标签

`SpeakerLabelMode` 有三种状态：

- **Off**：不创建 speaker 组件。
- **Local**：仅对入站、具有完整 VAD 音频的分段路径提取 CAMPPlus embedding，在当前会话内按余弦相似度聚类为匿名“说话人 A/B…”。模型首次使用时按固定 URL 下载，并验证文件大小和 SHA-256；模型不随发行包分发。流式 ASR 没有完整 utterance 音频，因此本地模式在该路径安全降级。
- **Cloud**：仅消费 provider 返回的 speaker ID。Soniox 支持该能力；DashScope 和其他协议不伪造标签，能力不匹配时安全降级。

系统回环是所选输出设备的混合轨道。匿名聚类和 provider speaker ID 都不能映射到 VRChat 玩家名称，也不构成可靠身份识别。

## VRChat 与反馈控制

Chatbox 通过 UDP OSC `/chatbox/input` 输出。可选 MuteSelf 监听器在独立本地 UDP 端点接收 `/avatar/parameters/MuteSelf`，支持 OSC message/bundle 的 bool、int 和 float 表示；静音时只抑制麦克风捕获并重置其 VAD，不影响系统回环。VRChat OSC 不提供其他玩家语音或聊天输入，也不能传输 TTS 音频。

语音模式将 TTS 写入用户选择的虚拟声卡播放端，VRChat 必须选择配对录音端。控制器按设备名称验证 VB-CABLE、VB-Audio、Cable Input、Voicemeeter 或 Virtual Cable 等已知虚拟路由，拒绝把桌面扬声器当作 VRChat 语音路由。两个快速模式关闭系统回环以防 TTS 递归识别；用户单独启用回环时，Engine 在自身 TTS 播放前重置捕获状态以进一步降低反馈。

## 关键决策

- **WinUI 3 工作台 + .NET sidecar**：保留成熟的 C# WASAPI、Whisper、快捷键、WPF 字幕和 OpenVR 链路。
- **本地优先与显式上传**：默认使用本地 Whisper。任何云 ASR 必须由用户显式开启 `AllowCloudAudioUpload`；Engine 也执行防御性校验。在线翻译只接收文本，在线 TTS 只接收当前实际朗读的出站原话或主译文。
- **协议决定传输形态**：DashScope/Soniox 使用持续 WebSocket；OpenAI/SiliconFlow 使用断句后 multipart；MiMo 使用 `input_audio`。Custom provider 必须明确选择协议。
- **两级有界队列**：流式音频队列按音源隔离并丢弃最旧块，final 工作队列单读者串行处理，避免阻塞 WASAPI 回调和无限积压。
- **能力驱动降级**：本地 speaker 只处理完整分段音频；云 speaker 只接受 Soniox 能力。SteamVR、说话人标签和 MuteSelf 失败均隔离为可选功能错误。
- **自动故障转移**：默认翻译使用免密端点故障转移；TTS 依次尝试 Edge、Google 和 Windows；Whisper 模型下载使用镜像和官方源。
- **敏感数据隔离**：普通配置和 DPAPI secrets 分离；PasswordBox 与请求头编辑器不把秘密写入 XAML Key 或普通可绑定对象。
- **字段存在性感知迁移**：新仓库缺失时，`SettingsRepository` 只读迁移旧 Flutter 普通设置和 DPAPI 安全存储。若 JSON 没有 `quickStartMode`，才从旧 `speakMyTranslation` 一次性推断模式并重写设置；字段存在时，显式 `QuickStartMode` 是权威值并同步朗读开关，避免旧 OR 逻辑覆盖用户选择。
- **显式关闭协调**：关闭先阻止新操作，停止并释放 capture、stream、recognizer 和 sidecar，再等待设置保存收敛。
- **SteamVR 可选输出**：OpenVR 在 WPF STA 线程按需初始化；SteamVR 缺失、未运行、无头显或运行时错误只关闭 VR 输出，不影响桌面字幕和翻译。

## 主要模块

### WinUI 3

- `src/VoxLink.UI/App.xaml.cs`：单实例启动、依赖组装和工作台生命周期
- `src/VoxLink.UI/MainWindow.xaml`：Mica 标题栏、NavigationView 和响应式导航壳
- `src/VoxLink.UI/Controls/OnboardingDialog.xaml`：首次启动三步模式、语言、设备及 VRChat 路由测试
- `src/VoxLink.UI/Pages/LivePage.xaml`：快速模式、输入、会话控制、partial/final 与双目标消息显示
- `src/VoxLink.UI/Pages/ProvidersPage.xaml`：翻译、润色、ASR provider/protocol、云上传许可和 TTS 配置
- `src/VoxLink.UI/Pages/AudioPage.xaml`：独立音源、设备、Whisper、仅转写和智能断句
- `src/VoxLink.UI/Pages/AdvancedPage.xaml`：入站/出站 TTS、speaker 模式、字幕与快捷键
- `src/VoxLink.UI/Pages/VRChatPage.xaml`：Chatbox、虚拟声卡语音路由、MuteSelf 监听和 SteamVR 设置
- `src/VoxLink.UI/Controls/HeaderEditor.xaml`：不暴露秘密值的自定义请求头编辑器
- `src/VoxLink.UI.Core/ViewModels/AppController.cs`：状态、校验、partial 合并、启动/关闭和命令分派
- `src/VoxLink.UI.Core/Services/EngineClient.cs`：sidecar 定位、进程生命周期和 JSON Lines 协议
- `src/VoxLink.UI.Core/Services/SettingsRepository.cs`：普通设置、DPAPI secrets 与旧前端迁移

`src/voxlink_app` 是退出构建和发布路径的旧 Flutter 实现，仅保留为迁移参考。

### .NET Engine

- `src/VoxLink.Engine/Program.cs`：JSON Lines 协议循环与错误出口
- `src/VoxLink.Engine/EngineHost.cs`：命令分派、事件 payload、Chatbox 门禁和秘密脱敏
- `src/VoxLink.Engine/UiHost.cs`：STA WPF 桌面字幕、SteamVR 字幕和全局快捷键宿主
- `src/VoxLink/Audio/WasapiSpeechCapture.cs`：麦克风/回环捕获、VAD utterance 和非阻塞 PCM chunk 事件
- `src/VoxLink/Audio/VoiceActivitySegmenter.cs`：预滚、RMS 门限、智能静音和最大时长断句
- `src/VoxLink/Services/AsrRecognizerFactory.cs`：按协议创建本地、分段云或流式云 recognizer
- `src/VoxLink/Services/SegmentedCloudSpeechRecognizer.cs`：multipart 与 MiMo `input_audio` 请求
- `src/VoxLink/Services/StreamingCloudSpeechRecognizer.cs`：DashScope/Soniox WebSocket、partial/final 和停止握手
- `src/VoxLink/Services/LocalSpeakerLabeler.cs`：模型下载校验、embedding 提取和会话内匿名聚类
- `src/VoxLink/Services/TranslationSession.cs`：双音源、两级队列、重连、翻译/润色、出站原话或译文 TTS 和生命周期
- `src/VoxLink/Services/VrChatOscSender.cs`：Chatbox 编码、队列、节流与 UDP 输出
- `src/VoxLink/Services/VrChatOscListener.cs`：MuteSelf OSC message/bundle 解码
- `src/VoxLink/Services/SteamVrOverlayHost.cs`：OpenVR Overlay 生命周期、字幕纹理和故障隔离

## 发布布局

```text
VoxLink-win-x64/
  VoxLink.exe
  VoxLink.dll
  VoxLink.UI.Core.dll
  Microsoft.WindowsAppRuntime.dll
  Microsoft.ui.xaml.dll
  App.xbf / MainWindow.xbf / Controls/*.xbf / Pages/*.xbf
  VoxLink.pri
  .NET and Windows App SDK runtime files (including WinAppSDK ML assets)
  Assets/AppIcon.ico
  engine/
    VoxLink.Engine.exe
    VoxLink.Engine.runtimeconfig.json
    VoxLink.dll
    openvr_api.dll
    sherpa-onnx.dll
    sherpa-onnx-c-api.dll
    onnxruntime.dll
    OPENVR-LICENSE.txt
    SHERPA-ONNX-LICENSE.txt
    SHERPA-ONNX-NOTICES.md
    ONNXRUNTIME-LICENSE.txt
    ONNXRUNTIME-THIRD-PARTY-NOTICES.txt
    .NET Desktop runtime, win-x64 Whisper, and native audio dependencies
  README.md
  LICENSE
  THIRD-PARTY-NOTICES.md
  WINDOWS-APP-SDK-LICENSE.txt
  WINDOWS-APP-SDK-NOTICES.txt
```

`scripts/publish.ps1` 以 `win-x64 --self-contained true` 分别发布 WinUI 和 Engine，将 Engine 放入 `engine/`，清理非 x64 的 Whisper 原生目录，校验 XBF/PRI、OpenVR、sherpa-onnx、ONNX Runtime、x64 Whisper 及对应许可，拒绝旧 Flutter DLL 和旧 WPF apphost，再生成 ZIP 和小写 SHA-256 文件。根目录的 ONNX Runtime/ML 文件由 Windows App SDK 自包含包提供并受 `WINDOWS-APP-SDK-NOTICES.txt` 覆盖；`engine/onnxruntime.dll` 由 sherpa runtime 使用并附带独立 ONNX Runtime notices。发布脚本兼容 Windows PowerShell 5.1；最终用户无需预装 .NET、Windows App SDK、Flutter、ATL 或开发工具。

## 更新检查、安装包与 CI

- **更新检查**：`VoxLink.UI.Core/Services/ReleaseChecker.cs` 查询 GitHub Releases API（`ReleaseMetadata.cs` 配置仓库 `znc15/VoxLink`），比较语义化版本号；404（尚无发布）视为“已是最新”，网络/解析失败返回可读错误而不抛出。`AppController` 启动时静默检查一次（`autoCheckForUpdates` 注入开关），`IsUpdateAvailable` 驱动实时翻译页提示条；高级设置页“版本与更新”面板显示当前版本、手动检查按钮与“打开下载页”（默认浏览器打开 Release 页）。
- **会话内采集改动提示**：`NeedsSessionRestart` 在会话运行中修改 `CaptureMicrophone`/`CaptureSystemAudio`/两个设备 ID 时置位；实时翻译页与音频设备页显示提示条，重新开始会话后清除。引擎 `configure` 不会重建采集链路，因此来源类改动必须重开会话才生效。
- **设备回退告警**：`WasapiSpeechCapture.ResolveDevice` 在已保存设备 ID 失效回退到 Windows 默认设备前触发 `DeviceFallbackOccurred`，`TranslationSession` 以会话错误形式提示，避免静默监听错误设备。
- **安装包**：`scripts/installer.iss`（Inno Setup 6）生成按用户安装（`{localappdata}\Programs\VoxLink`，无需管理员）的 `Setup-VoxLink-<版本>.exe`，递归包含整个自包含发布目录；`scripts/fetch-inno.ps1` 从 GitHub Releases 下载官方便携版编译器到 `scripts/tools/InnoSetup`。`publish.ps1` 从 `VoxLink.UI.csproj` 读取 `<Version>`，编译安装包并为 ZIP 与安装包各写一行 LF 结尾的 SHA-256 sidecar。
- **CI**：`.github/workflows/ci.yml` 在 main push/PR 上构建、测试、发布并上传 ZIP+安装包产物；`v*` 标签额外调用 `gh release create` 上传全部产物，应用更新检查即指向该 Release。
## 范围边界

VoxLink 不注入游戏、不 hook DirectX、不修改目标进程、不安装虚拟音频驱动。VRChat 语音模式依赖用户安装的虚拟声卡，不能通过 OSC 传输音频。桌面字幕是普通顶层窗口；SteamVR Overlay 不等同于通用 OpenXR 或原生 Oculus Overlay。VRChat OSC 不能读取其他玩家语音或聊天，入站语音始终来自本机 WASAPI 回环。

默认本地 Whisper 不上传原始音频。分段或流式云 ASR 会把用户明确选择的麦克风或回环音频交给所选 provider，并受其账户、费用、保留和地区政策约束。免密翻译和在线 TTS 是便利端点，不提供可用性 SLA。
