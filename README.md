# VoxLink

VoxLink 是面向 Windows 10/11 x64 的双向实时语音翻译桌面应用，专为 VRChat 等多人游戏设计：识别你的麦克风和系统输出语音，把译文发送到 VRChat Chatbox，或通过虚拟声卡把译后语音送入 VRChat 麦克风。

本地优先：默认使用本地 Whisper 识别，原始音频不离开电脑；云服务需显式授权并提供自己的 API Key。

<div align="center">
    <img src="artifacts/voxlink-main.png" alt="VoxLink 主界面" style="max-width: 100%;">
</div>

## 特性

- 双向翻译：麦克风（我的声音）与系统输出回环（对方声音）各走独立识别通道
- 两种一键模式，首次引导即选：
  - **仅文字**：识别麦克风 → 翻译 → 发送到 VRChat Chatbox
  - **VRChat 语音**：在 Chatbox 文字之外，把识别原话或主译文经 TTS 送入虚拟声卡，VRChat 作为麦克风采集
- 本地 Whisper（tiny/base/small）或云端流式 ASR：DashScope、Soniox、SiliconFlow、MiMo、OpenAI 兼容
- 免密翻译故障转移 + 可选 LLM 润色与第二目标语言；Chatbox 同时显示主、次译文（开启第二目标语言时）
- 桌面悬浮字幕与可选 SteamVR 头显字幕，主/次译文同屏
- 面向简体中文：识别、翻译与润色结果统一转换为简体字形
- 可选说话人标签：关闭 / 本地匿名聚类 / Soniox 云端 speaker ID
- 跟随 VRChat `MuteSelf` 暂停麦克风，不影响系统回环
- 启动时静默检查更新，发现新版本在首页提示
- API Key 与自定义请求头使用当前 Windows 用户 DPAPI 加密保存

## 安装

从 [Releases](https://github.com/znc15/VoxLink/releases) 下载最新版本，二选一：

- **安装包** `Setup-VoxLink-x.x.x.exe`：按当前用户安装到 `%LOCALAPPDATA%\Programs\VoxLink`，无需管理员权限（未签名，首次运行可能触发 SmartScreen 提示）
- **便携包** `VoxLink-win-x64.zip`：解压后双击包根目录的 `VoxLink.exe`（不要从压缩包内直接运行）

两种包都自带 .NET 运行时与 Windows App SDK，无需预装任何运行时。

首次启动按三步引导选择模式、语言与设备：

1. 选择模式：`仅文字` 或 `VRChat 语音`
2. 选择我的/对方语言、麦克风；语音模式还需选择虚拟声卡播放端
3. 测试 Chatbox 与语音路由，然后开始会话

> 两个模式都只启用麦克风并关闭系统音频回环（避免 TTS 被再次识别）。翻译其他玩家语音时，在「音频设备」页单独开启系统音频回环。

## 让 VRChat 听到译后语音

VRChat OSC 不能传输音频，语音模式需要虚拟声卡：

1. 安装 [VB-CABLE](https://vb-audio.com/Cable/)、Voicemeeter 或其他虚拟声卡
2. 在引导或「VRChat」页把“语音输出”设为 `CABLE Input`、`Voicemeeter Input` 等播放端
3. 在 VRChat `Settings > Audio > Microphone` 中选择配对的录音端（如 `CABLE Output`）
4. 麦克风输入仍选真实麦克风；选择朗读“翻译后内容”或“识别原话”后点击“测试语音路由”

## 配置

所有设置都在应用内完成，无需编辑任何文件；改动即时生效（音频采集类改动需重新开始会话）。

| 页面 | 内容 |
| --- | --- |
| 实时翻译 | 模式切换、语言与第二目标语言、会话控制、会话记录、手动输入翻译/AI 生成 |
| 音频设备 | 采集来源与设备路由、本地 Whisper 模型、断句阈值 |
| AI 与语音 | ASR/翻译/文本生成/语音输出服务、API Key、自定义请求头 |
| VRChat | Chatbox 开关与地址、语音路由、MuteSelf 联动、字幕设置 |
| 高级设置 | 会话行为、出站语音内容、全局快捷键、版本与更新 |

普通设置保存在 `%APPDATA%\VoxLink\settings.json`；API Key 与自定义请求头不进入该 JSON，而是以当前 Windows 用户 DPAPI 加密保存在 `%APPDATA%\VoxLink\secrets.dat`（绑定当前用户，不能复制到其他账户）。

默认快捷键：`Ctrl+Alt+Space` 开始/停止会话，`Ctrl+Alt+Enter` 翻译当前输入。

## 隐私与数据边界

- 默认本地 Whisper：原始音频不离开电脑；本地说话人模型也仅保存在本机
- 任何云端 ASR 都必须先在设置中显式启用“允许上传原始音频”，并受所选服务账户、费用与保留政策约束
- 在线翻译只接收识别文本；在线 TTS 只接收实际朗读的译文文本
- 系统回环是所选输出设备的混合轨道：关闭背景音乐或将游戏语音单独路由到一个输出设备可提高识别效果

## 从源码构建

要求：Windows 10 2004（build 19041）或更高版本 x64、.NET 10 SDK、可访问 NuGet。

```bash
dotnet build VoxLink.slnx -c Release
dotnet test tests/VoxLink.Tests/VoxLink.Tests.csproj -c Release
```

真实 Whisper、WASAPI、VRChat、SteamVR 与云服务烟测仅在显式启用时执行；默认测试使用 mock，不需要 API Key：

```bash
VOXLINK_RUN_LIVE_TESTS=1 dotnet test tests/VoxLink.Tests/VoxLink.Tests.csproj --filter 'Category=Live'
```

## 发布

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```

输出到 `artifacts/release/`：自包含目录、`VoxLink-win-x64.zip`、`Setup-VoxLink-<版本>.exe` 安装包及各自的 `.sha256`。首次运行会通过 `scripts/fetch-inno.ps1` 自动下载官方便携版 Inno Setup 到 `scripts/tools/`。

## 开发约定

- 提交信息使用中文，并采用约定式提交格式：`类型(范围): 描述`，例如 `fix(ui): 修复标题栏按钮样式`、`feat: 支持第二目标语言`、`docs: 更新架构说明`。
- 类型包括 `feat`（新功能）、`fix`（修复）、`docs`（文档）、`refactor`（重构）、`test`（测试）、`chore`（杂务）。
- 每次推送前若功能或修复有实质变化，同步更新 `src/VoxLink.UI/VoxLink.UI.csproj` 中的 `<Version>`；需要发布时打对应 `vX.Y.Z` 标签，CI 会自动生成 Release。

## 更新检查与 CI

- 应用启动后在后台查询 `https://api.github.com/repos/znc15/VoxLink/releases/latest`；高级设置页可手动检查并打开下载页
- `.github/workflows/ci.yml` 在 push/PR 时构建、测试并生成 ZIP 与安装包产物；推送 `v*` 标签时自动创建 GitHub Release 并上传全部产物
- 发布新版本：修改 `src/VoxLink.UI/VoxLink.UI.csproj` 的 `<Version>`，打标签 `v1.0.1` 并推送

## 已知边界

- 免密翻译与 Edge/Google 在线语音没有商业 SLA，可能受网络、地区、协议变化与配额影响
- 系统回环识别的是混合音轨，不能将说话人标签映射到 VRChat 用户名
- VRChat OSC 只提供 Chatbox 输入与本地 avatar 参数广播，不能读取其他玩家的聊天或语音；Chatbox 文本受 VRChat 长度与发送频率限制
- VRChat 语音模式必须借助第三方虚拟声卡；VoxLink 不安装虚拟音频驱动，也不向 OSC 发送音频流
- 桌面字幕是普通顶层窗口，不注入 DirectX 游戏；头显字幕仅支持 SteamVR/OpenVR
- 音频引擎异常退出后不会自动重启，工作台会显示错误，需要重新启动 VoxLink

## 技术栈

- WinUI 3 / Windows App SDK 2.3.1 原生 Windows 工作台
- .NET 10 独立 Engine：WASAPI 采集、全局快捷键、悬浮字幕、VRChat OSC
- NAudio / WASAPI + Whisper.net + whisper.cpp CPU 运行时
- sherpa-onnx + ONNX Runtime（可选本地说话人标签）
- DashScope/Soniox WebSocket、OpenAI multipart 与 MiMo `input_audio` ASR
- EdgeTTS.DotNet / Edge、Google、Windows TTS 回退
- Valve OpenVR / SteamVR Overlay
- 当前用户 DPAPI 敏感数据存储

详细进程与协议说明见 [docs/architecture.md](docs/architecture.md)。本项目采用 MIT License，参考项目（realtime-subtitle、VRCTTP）遵循 AGPL-3.0，本实现为独立清洁实现。
