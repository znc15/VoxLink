# 桌面字幕悬浮窗现状（2026-08-24 代码探索）

## OverlayWindow（src/VoxLink/OverlayWindow.xaml + .xaml.cs，引擎进程 STA 线程）

- 固定 `Width="760"` 默认、`SizeToContent="Height"`（xaml:4-5）；手动拉伸后 SizeToContent 翻转为 Manual（.xaml.cs:183-187），**高度不持久化，只有 left/top/width 保存**。MinWidth=240 / MinHeight=88（xaml:13-14）。
- 无边框透明置顶窗：WindowStyle=None、AllowsTransparency、Topmost、ShowInTaskbar=False、ShowActivated=False、Focusable=False。
- 内容 `SubtitleSurface` Border（Margin=18, Padding=24,17, Background=#E6192220, CornerRadius=7）内 4 个 TextBlock：header(FontSize 12)、主译文(24 SemiBold)、次译文(17)、原文(14)——后三个**硬编码 `MaxWidth="680"`** + Wrap（xaml:31-60）。**字体全部写死，无字体设置**。
- `ResizeThumb`（18×18 右下角，SizeNWSE 光标，默认 Collapsed；解锁时显示）。
- 定位：无 WindowStartupLocation；首次 Show 且无保存位置时 `PositionAtBottom()`（.xaml.cs:149-154）在**主显示器工作区**底部居中（Top = workArea.Bottom - ActualHeight - 54）。无多显示器/多 DPI 逻辑。
- `Configure(double? left, double? top, double? width, bool topmost, bool lockPosition)`（.xaml.cs:35-62）：`_hasSavedPlacement = left is not null && top is not null`；切换 ResizeThumb 可见性。**收到的 null 不会触发重新居中**——重置位置需要显式处理。
- 自动隐藏：DispatcherTimer 9 秒后 Hide()。锁定时 `WS_EX_TRANSPARENT` 点击穿透；解锁可 DragMove 拖动 + ResizeThumb 拉伸，`PlacementChanged` 事件 `Action<double,double,double>`（left, top, width）上报。

## 设置流（完整链路）

UI 模型 `src/VoxLink.UI.Core/Models/AppSettings.cs`：ShowOverlay(:325, true)、ShowVrOverlay(:326)、VrOverlayWidthMeters(:327, 1.6)、VrOverlayDistanceMeters(:328, 1.8)、VrOverlayVerticalOffsetMeters(:329, -0.35)、DesktopOverlayLeft/Top/Width(double?, :350-356)、DesktopOverlayTopmost(:357, true)、DesktopOverlayLockPosition(:358, true)。**没有字体大小/高度/不透明度设置**。

`ToEngineJson()`(:607-701) 序列化 showOverlay/showVrOverlay/vrOverlay 三项(:680-684) + desktopOverlayLeft/Top/Width/Topmost/LockPosition(:694-698)。经 AppController.SaveAndConfigureAsync → 「configure」请求（任何 NotifySettingsChanged 后 650ms 防抖，AppController.cs:226-232,1402-1452）；启动时随「initialize」(:1349-1352)；testDesktopOverlay(:539-547)。

引擎模型 `src/VoxLink/Models/AppSettings.cs`：ShowOverlay:169、ShowVrOverlay:171、VR:173-177、DesktopOverlayLeft/Top/Width:197-201、Topmost:203、LockPosition:205。EngineHost.ReadSettings(EngineHost.cs:738-750)、ApplySettings → `_uiHost?.Configure(_settings)`(:671)。字幕门控 :802-805 / :944-947。

UiHost.Configure(UiHost.cs:46-74) 调 STA：`_overlay.SetEnabled`、`_overlay.Configure(left, top, width, topmost, lockPosition)`(:52-58)、SteamVR Configure(:59-63)、热键(:64-72)。`overlayPlacement` 事件(:161-165) → `AppController.HandleEngineEvent`(:1601-1621) 写回 Settings.DesktopOverlayLeft/Top/Width 并 SaveNowAsync() → `%APPDATA%\VoxLink\settings.json`。

UI 页 `src/VoxLink.UI/Pages/OverlayPage.xaml`：桌面区 ShowOverlay(:39-43)、Topmost(:45-49)、LockPosition(:51-55)、测试按钮(:62-69)、说明(:36)。VR 区 :86-154（三个 slider，code-behind :48-84）。

测试：`tests/VoxLink.Tests/UI/AppControllerTests.cs:68-72,124-140`（overlay 字段 ToEngineJson 往返）、`SettingsRepositoryTests.cs:71-76,129-134`。

## 设计缺口（本次要补）

1. 高度不持久化（拉伸后重启丢失）
2. 正文 MaxWidth=680 硬编码，窗口拉宽文本也不变宽
3. 字体大小不可调
4. 位置只能拖动获得，无「重置位置」；Configure 收 null 不会重新居中
5. 无多显示器初始定位/DPI 处理（拖到副屏可工作，坐标照存——可接受）
