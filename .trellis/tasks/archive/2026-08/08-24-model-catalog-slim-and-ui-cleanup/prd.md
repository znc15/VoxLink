# 本地模型精简与UI清理（1.5.1）

## Goal

面向大众电脑（含无独显/低显存用户，且需与 VRChat 等 3D 游戏同开）精简本地模型目录，同时清理两项 v1.5.0 引入但不再需要的设置，修复模型服务下拉框布局问题，并为桌面字幕悬浮窗补充尺寸/位置设置。版本 1.5.1。

## 需求来源（用户原话整理）

1. 联网调研并抽选最适合大众电脑的本地模型，考虑 CPU/GPU 占用与运行性能（要和 VRChat 同开）。
2. 删除「软件整体透明度」设置。
3. 删除「标题栏」开关。
4. 删除「更多模型」入口。
5. 模型服务选择（下拉框）不应出现在下拉框下面（布局异常，见截图）。
6. 桌面字幕悬浮窗支持设置大小、宽度、位置等。
7. （用户确认）所有改动都要实际测试——构建、单测、真实运行 UI 验证。

## 子任务地图

| 子任务 | 交付物 | 独立可验收 |
|---|---|---|
| 08-24-slim-local-models | ASR 目录精简为 tiny/base/small/large-v3-turbo；本地翻译精简为混元 HY-MT（去掉 MiniCPM/M2M/SMaLL）；移除「更多模型」入口 | 下拉列表与下载链路只剩所选模型，构建+测试通过，UI 实测 |
| 08-24-remove-opacity-titlebar | 删除整体透明度滑块及其全部触点；删除标题栏开关及其全部触点 | 设置页无这两项，残留引用清理干净，UI 实测 |
| 08-24-fix-providers-combobox | 模型服务选择下拉框布局修复（不再渲染到下拉列表下方/错位） | UI 实测截图对比 |
| 08-24-overlay-size-position | 桌面字幕悬浮窗支持宽度/位置/大小设置（视现有实现定：宽度+字体大小+位置，尽量含记住位置） | UI 实测：改设置→悬浮窗即时生效 |

## 约束

- 遵循 CLAUDE.md 架构不变量：设置分两处存储（settings.json / secrets.dat DPAPI）、引擎侧能力驱动降级、UI 字符串中文。
- 不破坏两进程协议；悬浮窗设置如需引擎参与，走 configure/事件通道，禁止跨进程直接改 UI 状态。
- 版本号 bump 到 1.5.1（VoxLink.UI.csproj 的 Version/FileVersion/AssemblyVersion）。
- 每个 UI 改动需实际运行 VoxLink.exe 验证（用户明确要求），不只是单测。

## 验收标准（父任务，跨子任务集成）

- [ ] `dotnet build VoxLink.slnx -c Release` 零警告（TreatWarningsAsErrors）
- [ ] `dotnet test VoxLink.slnx -c Release --no-build` 全绿
- [ ] 发布冒烟：publish.ps1 可跑通（或至少 Release 构建产物可启动）
- [ ] UI 实测四项：模型列表精简后、透明度/标题栏开关已删、下拉框正常、悬浮窗设置生效
- [ ] 提交信息遵循 `类型(范围): 描述` 中文约定；版本 1.5.1
