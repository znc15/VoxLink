# VoxLink

VoxLink 是面向 Windows 10/11 x64 多人在线游戏的双向语音翻译桌面应用。

- 首次启动提供三步引导，可在首页快速选择 `OSC 文字翻译` 或 `VRChat 语音翻译`
- 输入文字后翻译到对方语言；麦克风和系统回环也可独立启用
- 支持本地 Whisper、DashScope/Soniox 流式 ASR、OpenAI 兼容 multipart 及 MiMo `input_audio`
- 流式识别实时显示 partial，final 才进入翻译、TTS 和发布流程；桌面与 SteamVR 字幕可同时显示主、次译文
- 仅转写模式直接显示原文，不调用翻译、TTS 或 VRChat Chatbox
- 将最终的我的麦克风主译文和手动输入主译文通过 VRChat OSC 发送到 Chatbox；入站语音和次译文不会发送
- VRChat 语音模式可将识别原话或主译文经 TTS 输出到 VB-CABLE、Voicemeeter 等虚拟声卡
- 面向简体中文的识别、翻译和 LLM 润色结果统一转换为简体字形
- 可跟随 VRChat `/avatar/parameters/MuteSelf` 暂停麦克风路径，不影响系统回环
- 可选 LLM 译文润色、入站 TTS，以及关闭/本地匿名聚类/Soniox 云端 speaker ID 三种说话人模式
- 默认无需 API Key：本地 Whisper + 免密翻译故障转移 + Edge/Google/Windows TTS
- API Key 和自定义请求头使用当前 Windows 用户 DPAPI 保存；云 ASR 必须由用户显式允许上传原始音频
- 原生 WinUI 3 工作台，已验证的 .NET WASAPI/Whisper 引擎作为受控子进程运行
- 启动时后台检查 GitHub Releases 更新：发现新版本在首页提示，高级设置页可手动检查并打开下载页
![VoxLink 主界面](artifacts/voxlink-main.png)

## 快速开始

### 直接运行发布包

1. 解压 `VoxLink-win-x64.zip`，不要只从压缩包内直接运行程序。
2. 双击包根目录的 `VoxLink.exe`。
3. 首次启动按新手引导选择模式、语言和真实麦克风：
   - `OSC 文字翻译`：识别麦克风并把主译文发送到 VRChat Chatbox，不播放 TTS。
   - `VRChat 语音翻译`：在 Chatbox 文字之外，将识别原话或主译文经 TTS 送入虚拟声卡。
4. 在引导最后一步测试 Chatbox；语音模式还应测试虚拟声卡路由。
5. 点击“开始会话”。默认本地 Whisper 首次使用会下载所选模型；选择云 ASR 时必须填写相应服务配置并显式开启原始音频上传。
发布包包含 WinUI 3、Windows App SDK、.NET 运行时和 `engine/VoxLink.Engine.exe`，无需安装 .NET、Windows App SDK 或开发工具。

默认快捷键：

- `Ctrl+Alt+Space`：开始或停止双向语音翻译
- `Ctrl+Alt+Enter`：翻译当前输入

普通设置保存在 `%APPDATA%\VoxLink\settings.json`。API Key 和自定义请求头不进入该 JSON，而是使用当前 Windows 用户的 DPAPI 加密后保存在 `%APPDATA%\VoxLink\secrets.dat`。WinUI 版本首次启动时会迁移旧 Flutter 版本的普通设置和加密密钥；旧配置若没有 `quickStartMode`，会根据原有 `speakMyTranslation` 一次性推断文字或语音模式。DPAPI 密钥绑定当前 Windows 用户，不能靠复制 `secrets.dat` 迁移到其他账户或电脑。

## 让 VRChat 听到译后语音

VRChat OSC 不能传输音频。`VRChat 语音翻译` 模式因此要求虚拟声卡播放端，不能直接选择桌面扬声器：

1. 安装 [VB-CABLE](https://vb-audio.com/Cable/)、Voicemeeter 或其他虚拟声卡。
2. 在 VoxLink 的新手引导或“VRChat”页面，将“语音输出”设为 `CABLE Input`、`Voicemeeter Input` 等播放端。
3. 在 VRChat `Settings > Audio > Microphone` 中选择配对的录音端，例如 `CABLE Output`。
4. VoxLink 的“麦克风输入”仍选择真实麦克风。
5. 选择朗读“翻译后内容”或“识别原话”，再点击“测试语音路由”并观察 VRChat 麦克风电平。

两个首页模式都只自动启用麦克风并关闭系统回环，避免 TTS 被再次识别。需要翻译其他玩家语音时，可在“音频设备”页单独启用系统音频回环；虚拟声卡只负责把 VoxLink TTS 送入 VRChat。

## VRChat 集成

1. 在 VRChat 的 Action Menu 中打开 `Options > OSC > Enabled`。
2. 在 VoxLink 首页选择 `OSC 文字翻译` 或 `VRChat 语音翻译`；两者都会启用 Chatbox 输出。
3. 在“VRChat”页面保持目标地址 `127.0.0.1`、端口 `9000`，点击“发送测试消息”。
4. 语音模式按上一节选择虚拟声卡播放端，并在 VRChat 中选择配对录音端。
5. 需要 VoxLink 跟随 VRChat 麦克风静音时，启用 `MuteSelf` 监听；默认监听 `127.0.0.1:9001`。
6. 需要头显内独立字幕时，先启动 SteamVR 并连接头显，再启用“SteamVR 头显字幕”并点击测试。
OSC Chatbox 只发送 VoxLink 产生的最终、非仅转写的我的麦克风主译文、手动输入主译文和 AI 生成文字。系统回环入站译文、流式 partial、说话人标签和第二译文不会以用户身份发送。VRChat OSC 不提供其他玩家语音或聊天内容，因此对方语音仍来自本机 WASAPI 系统回环。
SteamVR 字幕使用 Valve OpenVR Overlay。Meta/Oculus 头显通过 SteamVR 运行 VRChat 时可以使用；原生 Oculus 模式和非 SteamVR OpenXR 运行时不在当前支持范围内。SteamVR 未安装、未运行或未检测到头显时，桌面字幕和翻译流程仍正常工作。
## 数据流

识别路径由 ASR 协议决定：

- **本地 Whisper**：WASAPI -> 16 kHz 单声道 PCM -> VAD/智能断句 -> 本地识别。
- **分段云 ASR**：同样先在本机断句，再以 WAV multipart 上传到 SiliconFlow/OpenAI 兼容端点，或以 MiMo `input_audio` 上传。
- **持续流式 ASR**：每个启用音源拥有独立的 DashScope 或 Soniox WebSocket；音频回调只写入有界队列。partial 仅更新字幕，同一句 partial/final 共用稳定 `utteranceId`。连接中断时该音源按 1/2/4/8 秒退避重连，并丢弃断线期间的陈旧待发送音频。

方向行为：

- **我的声音**：麦克风 -> ASR -> 对方语言主译文及可选次译文 -> 可选 TTS -> 所选输出设备；final 主译文可发送到 VRChat Chatbox。出站 TTS 可选择识别原话及我的语言，或主译文及对方语言。
- **对方声音**：系统输出回环 -> ASR -> 我的语言主译文及可选次译文 -> 会话记录、桌面字幕、可选 SteamVR 字幕及可选入站 TTS。入站 TTS 始终朗读主译文。
- **仅转写**：两个方向都显示 ASR 原文；不翻译、不润色、不朗读、不发送 Chatbox。

本地 Whisper 路径不上传原始音频。任何云 ASR 路径都必须先在设置中显式启用“允许上传原始音频”；所选服务会收到麦克风或系统回环音频。在线翻译只接收识别文本，在线 TTS 接收当前实际朗读的主译文或出站原话。系统回环是所选输出设备的混合轨道，关闭背景音乐或将游戏语音单独路由到一个输出设备可提高识别效果。
## 翻译、润色与语音后端

默认翻译会在免密服务间故障转移。免密端点依赖网络，可能受地区、限流或服务协议变化影响；需要更稳定的游戏术语翻译时，可选择 DashScope、DeepSeek、OpenAI 兼容或自定义服务。本地 Ollama/LM Studio 的 API Key 可留空。

使用 AI 翻译后端时，可启用 LLM 译文润色并填写术语提示。若设置第二目标语言，final 会分别生成主、次译文；三套字幕目标都显示两者，但 VRChat Chatbox 只使用主译文，TTS 也不会朗读第二译文。免密公共翻译模式不会启用 LLM 润色。

目标语言为中文（简体）时，公开翻译 provider 使用 `zh-CN`，LLM 提示词明确要求简体中文，最终 ASR 原文、主次译文和润色结果还会通过 Windows `LCMapStringEx` 统一转换为简体字形。DashScope、Soniox 和 MiMo ASR 仍按各自协议发送裸语言码 `zh`。

TTS 默认依次尝试 Microsoft Edge 在线神经语音、Google 在线语音和 Windows 已安装语音。也可配置 DashScope、MiMo/OpenAI 兼容或自定义 TTS 端点；远程失败仍回退到默认链路。我的出站朗读与入站译文朗读可独立控制；“识别原话/翻译后内容”只作用于最终 `Outbound` 和 `Typed`，入站始终朗读主译文。若目标语言没有本地语音，请在 Windows“时间和语言”中安装对应语音包。

## 说话人标签

- **关闭**：不显示说话人。
- **本地**：仅用于具有完整 VAD 音频的分段识别；首次启用时下载并校验 CAMPPlus 模型，在当前会话中显示匿名“说话人 A/B…”。
- **云端**：仅 Soniox 流式协议提供 provider speaker ID；其他 ASR 会在本次会话中安全降级为无标签。

说话人标签来自混合音轨和声纹聚类，不代表 VRChat 用户身份，也不能映射到玩家名称。
## 从源码运行

要求：

- Windows 10 2004（build 19041）或更高版本，x64
- .NET 10 SDK
- 可访问 NuGet 以还原 Windows App SDK 和音频依赖

构建整个解决方案并启动 WinUI 工作台：

```bash
dotnet build VoxLink.slnx -c Release
dotnet run --project src/VoxLink.UI/VoxLink.UI.csproj -c Release -r win-x64
```

开发模式下，工作台会从仓库中的 `src/VoxLink.Engine/bin` 自动定位 Engine。也可通过 `VOXLINK_ENGINE_PATH` 指定 `VoxLink.Engine.exe`。

运行验证：

```bash
dotnet test tests/VoxLink.Tests/VoxLink.Tests.csproj -c Release
dotnet build VoxLink.slnx -c Release
```

真实 Whisper、WASAPI、VRChat、SteamVR 和云服务硬件烟测仅在显式启用时执行；默认测试使用 mock HTTP/WebSocket/OSC，不需要 API Key：
```bash
VOXLINK_RUN_LIVE_TESTS=1 dotnet test tests/VoxLink.Tests/VoxLink.Tests.csproj --filter 'Category=Live'
```

生成自包含便携包：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```

输出为 `artifacts/release/VoxLink-win-x64/`、`VoxLink-win-x64.zip`、`Setup-VoxLink-<版本>.exe` 安装包及各自的 `.sha256` 文件。安装包以当前用户方式安装（无需管理员，写入 `%LOCALAPPDATA%\Programs\VoxLink`），未签名，首次运行可能触发 SmartScreen 提示。

发布时同时生成安装包和便携包：`publish.ps1` 会调用 `scripts/tools/InnoSetup/iscc.exe`（首次运行 `scripts/fetch-inno.ps1` 自动下载官方便携版 Inno Setup）。

## 更新检查与 CI

- 应用启动后在后台查询 `https://api.github.com/repos/znc15/VoxLink/releases/latest`（见 `src/VoxLink.UI.Core/Services/ReleaseMetadata.cs`）；仓库还没有发布时显示“尚未发布版本”。
- 高级设置页的“版本与更新”面板显示当前版本并提供“检查更新”与“打开下载页”。
- `.github/workflows/ci.yml` 在 push/PR 时构建、测试并生成 ZIP 与安装包产物；推送到 `v*` 标签时自动创建 GitHub Release 并上传全部产物。
- 发布新版本：修改 `src/VoxLink.UI/VoxLink.UI.csproj` 的 `<Version>`，打标签 `v1.0.1` 并推送，CI 完成后发布页即出现新版本。
## 已知边界

- 免密翻译和 Edge/Google 在线语音没有商业 SLA，可能受网络、地区、协议变化和配额影响。
- `tiny` Whisper 模型优先低延迟；口音较重或环境嘈杂时可在“音频设备”中切换 `base` 或 `small`。
- 云 ASR 会把原始音频交给所选服务；上传许可、账户条款、数据保留和费用由用户负责。
- 流式 ASR 会自动重连，但断线期间的音频不重放；连续故障会持续显示错误，必要时停止并重新开始会话。
- 系统回环捕获的是一个输出设备的混合音频，不能将匿名聚类或 provider speaker ID 映射到 VRChat 用户名。
- 本地说话人标签仅适用于 VAD 分段路径；DashScope 不提供云端说话人标签，Soniox 才支持云端 speaker ID。
- VRChat OSC 只提供 Chatbox 输入和本机 avatar 参数广播，不能读取其他玩家的聊天或语音；Chatbox 文本受 VRChat 的长度和发送频率限制。
- VRChat 语音模式必须借助第三方虚拟声卡；VoxLink 不安装虚拟音频驱动，也不会向 OSC 发送音频流。
- 两个快速模式默认关闭系统回环；翻译其他玩家语音需在“音频设备”页单独开启，且回环混合轨道可能包含游戏音效和其他应用。
- 桌面字幕是普通顶层窗口，不注入 DirectX 游戏；头显内独立字幕仅支持 SteamVR/OpenVR，不支持原生 Oculus/OpenXR。
- 音频引擎异常退出后不会自动重启；工作台会显示错误，需要重新启动 VoxLink。

## 技术栈

- WinUI 3 / Windows App SDK 2.3.1 原生 Windows 工作台
- .NET 10 + WPF sidecar（WASAPI、全局快捷键和悬浮字幕）
- NAudio 2.3.0 / WASAPI + Whisper.net 1.9.1 / whisper.cpp CPU runtime
- DashScope/Soniox WebSocket、OpenAI multipart 与 MiMo `input_audio` ASR
- sherpa-onnx 1.13.4 + ONNX Runtime 1.27.0（可选本地匿名说话人标签）
- EdgeTTS.DotNet 0.4.0 / Microsoft Edge、Google、Windows TTS 回退
- Valve OpenVR 2.15.6 / SteamVR Overlay
- VRChat OSC `/chatbox/input`、`/avatar/parameters/MuteSelf` / UDP
- 当前用户 DPAPI 敏感数据存储

详细进程、协议和发布布局见 [docs/architecture.md](docs/architecture.md)。项目采用 MIT License。
