# 桌面字幕悬浮窗尺寸位置设置

## Goal

桌面字幕悬浮窗在现有「拖动/拉伸自动保存」基础上，补齐显式设置：宽度、字体大小、高度持久化、重置位置，保持两进程架构不变。

## Requirements

1. **宽度**：OverlayPage 桌面字幕区加宽度滑块/数字框（范围 240–1920，默认 760），即时生效；与现有拉伸交互互相同步（拉伸后滑块跟随）。
2. **字体大小**：主译文/次译文/原文按比例可调——一个「字幕字号」设置（范围 14–40，默认 24，作用于主译文，次译文≈主*0.7、原文≈主*0.58 取整），正文 MaxWidth 改为绑定窗口宽度自适应（去掉硬编码 680）。
3. **高度持久化**：OverlayWindow 拉伸产生的高度保存（DesktopOverlayHeight），重启恢复；未拉伸时保持 SizeToContent=Height 自动高度。
4. **重置位置**：OverlayPage 加「重置位置与大小」按钮——清空 left/top/width/height 持久化值并通知悬浮窗回到主屏底部居中（engine→configure 需支持显式重置语义，null 不会自动重置的现状要处理）。
5. 设置链路：AppSettings(UI) → ToEngineJson → engine AppSettings → UiHost.Configure → OverlayWindow，PlacementChanged 事件回传 height；保存到 settings.json。字符串中文。
6. 测试：AppControllerTests 的 ToEngineJson 往返、SettingsRepositoryTests 增补新字段。

## 现状与缺口

见 research/overlay-current-state.md（现有字段、链路文件:行号、四个设计缺口）。

## Acceptance Criteria

- [ ] OverlayPage 可设置宽度与字号，改动即时反映到悬浮窗（解锁状态下）
- [ ] 拉伸悬浮窗后重启应用，位置与高度均恢复
- [ ] 「重置位置与大小」后悬浮窗回主屏底部居中、宽 760、自动高度
- [ ] 构建+测试全绿；实际运行 VoxLink.exe 验证以上全部（含测试字幕按钮）
