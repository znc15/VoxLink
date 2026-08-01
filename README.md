<div align="center">

# VoxLink

#### 面向 Windows 的双向实时语音翻译桌面应用，为 VRChat 等多人游戏设计

[![CI](https://github.com/znc15/VoxLink/actions/workflows/ci.yml/badge.svg)](https://github.com/znc15/VoxLink/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/znc15/VoxLink)](https://github.com/znc15/VoxLink/releases)
[![License](https://img.shields.io/github/license/znc15/VoxLink)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D4)](https://github.com/znc15/VoxLink/releases)

[下载](https://github.com/znc15/VoxLink/releases) •
[架构说明](docs/architecture.md) •
[问题反馈](https://github.com/znc15/VoxLink/issues) •
[第三方声明](THIRD-PARTY-NOTICES.md)

</div>

识别你的麦克风与系统输出语音，把译文发送到 VRChat Chatbox，或经虚拟声卡把译后语音送入 VRChat 麦克风。

**本地优先**：默认使用本地 Whisper，原始音频不离开电脑；云服务需显式授权并提供你自己的 API Key。

<div align="center">
    <img src="artifacts/voxlink-main.png" alt="VoxLink 主界面" width="720">
</div>

## ✨ 特性

- 🎙️ **双向翻译**：麦克风（我的声音）与系统回环（对方声音）各走独立识别通道
- 🎮 **两种一键模式**：`仅文字` 发译文到 Chatbox；`VRChat 语音` 额外把识别原话或译文经 TTS 送入虚拟声卡
- 🧠 **本地或云端识别**：Whisper tiny/base/small，或 DashScope、Soniox、SiliconFlow、MiMo、OpenAI 兼容
- 🌐 **翻译与润色**：免密翻译故障转移、可选 LLM 润色与第二目标语言（Chatbox 同屏显示主、次译文）
- 📺 **字幕**：桌面悬浮字幕与可选 SteamVR 头显字幕；面向简体中文统一字形
- 👥 **说话人标签**：关闭 / 本地匿名聚类 / 云端 speaker ID
- 🔇 **VRChat 联动**：跟随 VRChat `MuteSelf` 暂停麦克风；启动后台检查更新
- 🔐 **凭据安全**：API Key 与自定义请求头用当前 Windows 用户 DPAPI 加密保存
- 📋 **可观测性**：内置「日志」页排查翻译与引擎问题，日志同时写入磁盘

## 🚀 快速开始

从 [Releases](https://github.com/znc15/VoxLink/releases) 下载，二选一：

### 安装包（推荐日常使用）

- 文件：`Setup-VoxLink-x.x.x.exe`
- 按当前用户安装到 `%LOCALAPPDATA%\Programs\VoxLink`，无需管理员
- 未代码签名，首次运行可能触发 SmartScreen

### 便携包

- 文件：`VoxLink-win-x64.zip`
- 解压后运行包根目录的 `VoxLink.exe`

两种包均自带 .NET 运行时与 Windows App SDK。首次启动按引导选择模式、语言与设备即可开始。

> [!NOTE]
> 两个模式默认只启用麦克风并关闭系统回环（避免 TTS 被再次识别）。翻译其他玩家语音时，在「音频设备」页单独开启系统音频回环。

## 🎧 让 VRChat 听到译后语音

VRChat OSC 不能传输音频，语音模式需虚拟声卡：

1. 安装 [VB-CABLE](https://vb-audio.com/Cable/) 或 Voicemeeter
2. 在 VoxLink「VRChat」页把「语音输出」设为 `CABLE Input` 等播放端
3. 在 VRChat `Settings > Audio > Microphone` 选择配对录音端（如 `CABLE Output`）

## ⚙️ 配置

所有设置在应用内完成，改动即时生效（音频采集类改动需重新开始会话）。

| 页面 | 内容 |
| --- | --- |
| 实时翻译 | 模式、语言、第二目标语言、会话控制、会话记录、手动输入 |
| AI 与语音 | ASR / 翻译 / 文本生成 / 语音输出服务、API Key、自定义请求头 |
| 音频设备 | 采集来源与路由、本地 Whisper 模型、断句阈值 |
| VRChat | Chatbox、语音路由、MuteSelf 联动、字幕设置 |
| 高级设置 | 会话行为、出站语音内容、全局快捷键、版本与更新 |
| 日志 | 实时查看运行日志，排查翻译与引擎错误 |

**配置文件**

| 路径 | 说明 |
| --- | --- |
| `%APPDATA%\VoxLink\settings.json` | 普通设置 |
| `%APPDATA%\VoxLink\secrets.dat` | API Key 与请求头（DPAPI 加密） |
| `%APPDATA%\VoxLink\logs\` | 运行日志 |

默认快捷键：`Ctrl+Alt+Space` 开始/停止，`Ctrl+Alt+Enter` 翻译当前输入。

## 🔒 隐私与数据边界

- 默认本地 Whisper：原始音频不离开电脑；本地说话人模型也仅保存在本机
- 云端 ASR 必须先在设置中显式启用「允许上传原始音频」，并受所选服务约束
- 在线翻译只接收识别文本；在线 TTS 只接收实际朗读的译文文本
- 系统回环是输出设备的混合音轨：关闭背景音乐或单独路由游戏语音可提高识别效果

## 🛠️ 构建

**环境**：Windows 10 2004+ x64、.NET 10 SDK、NuGet 可达。

```bash
dotnet build VoxLink.slnx -c Release
dotnet test tests/VoxLink.Tests/VoxLink.Tests.csproj -c Release
```

涉及真实 Whisper / WASAPI / VRChat / SteamVR / 云服务的用例仅在 `VOXLINK_RUN_LIVE_TESTS=1` 时执行（默认 mock，不需要 Key）。

**发布**

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```

产物在 `artifacts/release/`：自包含目录、`VoxLink-win-x64.zip`、`Setup-VoxLink-<版本>.exe` 及校验文件（首次自动下载便携版 Inno Setup）。

## 🚀 贡献

Issues 与 Pull Request 欢迎提交。功能或较大改动建议先开 Issue 讨论。

**约定**

- 提交信息：中文约定式提交 `类型(范围): 描述`（feat / fix / docs / refactor / test / chore）
- 版本：功能或修复有实质变化时同步 `src/VoxLink.UI/VoxLink.UI.csproj` 的 `<Version>`
- 发布：打 `vX.Y.Z` 标签，CI 自动构建并发布 Release；应用启动后台查询 GitHub Releases 检查更新，高级设置页可手动检查

## ⚗️ 技术栈

WinUI 3 / Windows App SDK 2.3.1 工作台 + .NET 10 独立 Engine（WASAPI 采集、Whisper.net、sherpa-onnx 说话人、DashScope / Soniox / OpenAI / MiMo、Edge / Google / Windows TTS、VRChat OSC、OpenVR 字幕、DPAPI 存储）。详见 [docs/architecture.md](docs/architecture.md)。

## ⚠️ 已知边界

- 免密翻译与在线语音无商业 SLA，受网络、地区与配额影响
- 系统回环识别混合音轨，说话人标签不能映射到 VRChat 用户名
- VRChat OSC 仅提供 Chatbox 输入与本地参数广播；Chatbox 文本受长度与频率限制
- 语音模式必须借助第三方虚拟声卡；VoxLink 不安装虚拟驱动，也不向 OSC 发送音频
- 桌面字幕是普通顶层窗口，不注入游戏；头显字幕仅支持 SteamVR / OpenVR

## 📜 许可证

[MIT License](LICENSE)。参考项目（realtime-subtitle、VRCTTP）为 AGPL-3.0，本实现为独立清洁实现；第三方组件见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
