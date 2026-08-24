# 设计：桌面字幕悬浮窗尺寸位置设置

## 边界

改 5 层（现状链路见 research/overlay-current-state.md）：
UI 设置模型（UI.Core AppSettings）→ ToEngineJson → 引擎设置模型+UiHost.Configure → OverlayWindow（xaml/xaml.cs）→ overlayPlacement 事件回写。UI 页面 OverlayPage.xaml(+.cs)。

## 新增字段

| 字段 | UI 属性 | 引擎字段 | JSON | 默认 | 范围 |
|---|---|---|---|---|---|
| 高度 | DesktopOverlayHeight (double?) | DesktopOverlayHeight | desktopOverlayHeight | null=自动 | 88–2000 |
| 字号 | DesktopOverlayFontSize (int) | DesktopOverlayFontSize | desktopOverlayFontSize | 24 | 14–40 |

（宽度/位置已有：DesktopOverlayLeft/Top/Width。）

## 数据流

1. **设置→窗口**：UiHost.Configure 签名扩为 (left, top, width, height, fontSize, topmost, lockPosition)；OverlayWindow.Configure 同步扩参——height 有值时设 Height 并 SizeToContent=Manual，null 时 SizeToContent=Height；fontSize 写到字段，主 TextBlock FontSize 绑定应用（次=round(主×0.7)、原文=round(主×0.58)，header 不变）。
2. **正文宽度**：三个正文 TextBlock 的 MaxWidth=680 删除，改 SizeToContent 下由窗口 Width（默认 760−2×(Margin18+Padding24)≈676）自然约束；窗口拉宽文本跟随。
3. **回写**：ResizeThumb DragCompleted 已发 PlacementChanged(left, top, width) → 增加 height（仅当 Manual 时）；UiHost 事件 payload 加 height；AppController.HandleEngineEvent overlayPlacement case 写 DesktopOverlayHeight。字号不在窗口侧改，无需回写。
4. **重置**：OverlayPage「重置位置与大小」按钮 → Controller 方法：Settings.DesktopOverlayLeft/Top/Width/Height=null、FontSize=24（字段本身含回退），NotifySettingsChanged()；configure 载 null 语义=重置 → OverlayWindow.Configure 收到全 null 时 _hasSavedPlacement=false 并立即 PositionAtBottom()（若可见）或下次 Show 时定位。加 engine testDesktopOverlay 已有，重置后点「测试桌面字幕」可见效果。
5. **UI 页**：桌面字幕区加「字幕宽度」Slider（240–1920，步进 20，绑 DesktopOverlayWidth??760）与「字幕字号」Slider（14–40，绑 FontSize）+「重置位置与大小」按钮。Slider 值写入 Settings 后走既有 650ms 防抖 configure。宽度 Slider 与窗口拉伸的双向同步：overlayPlacement 回写已覆盖（回写触发 UI 属性变更→Slider 刷新，注意 Slider 拖动时暂停回写刷新避免跳动——用 _updating 标志）。

## 权衡

- 字号只给「主译文」一个滑块按比例联动，不做四个独立滑块（设置爆炸，PRD 同意比例方案）。
- 重置不做「恢复窗口高度自适应」单独开关——重置按钮一并处理。
- 多显示器不做出屏选择器；保存的绝对坐标现状可跨屏工作（WPF 多屏坐标一致），超出可见区的兜底：Show 前 clamp 到虚拟屏幕范围（VirtualScreenLeft/Top/Right/Bottom）。

## 回滚

单 commit；settings.json 新键未知时自然丢弃，无迁移。
