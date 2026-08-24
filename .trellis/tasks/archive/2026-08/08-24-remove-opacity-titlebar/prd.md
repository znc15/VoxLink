# 删除整体透明度与标题栏开关

## Goal

从 v1.5.0 引入的两项设置（软件整体透明度 WindowOpacity、标题栏模式 UseSystemTitleBar）整体移除，UI 与测试同步清理，无残留引用。

## Requirements

- 删除「软件整体透明度」滑块及其设置字段、应用逻辑（WS_EX_LAYERED/LWA_ALPHA 路径）、相关 P/Invoke 与测试断言
- 删除「标题栏模式」开关及其设置字段、ApplyWindowChrome 的分支逻辑；主窗口固定走自绘标题栏（现状默认值），AppTitleBar 元素与 PaneToggleRequested 保留
- 旧 settings.json 中残留的 windowOpacity/useSystemTitleBar 键按未知键自然丢弃，不做迁移
- 完整触点清单见 research/removal-checklist.md（行号为探索快照，实施时以实际代码为准）

## Acceptance Criteria

- [ ] 全仓 grep 无 `WindowOpacity`/`windowOpacity`/`UseSystemTitleBar`/`useSystemTitleBar`/`WindowOpacitySlider`/`ApplyWindowOpacity`/`OpacityValueText`/`RefreshOpacityLabel`/`SetLayeredWindowAttributes`/`系统标题栏`/`软件整体透明度` 残留
- [ ] AdvancedPage 无这两个控件；主窗口标题栏显示正常（自绘）
- [ ] `dotnet build VoxLink.slnx -c Release` 零警告；`dotnet test` 全绿
- [ ] 实际运行 VoxLink.exe：高级设置页无「软件整体透明度」与「标题栏模式」两项，标题栏/拖动窗口/最小化关闭按钮正常
