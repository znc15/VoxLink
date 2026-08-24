# 实施计划：悬浮窗尺寸位置设置

前置：slim-local-models 完成（AppSettings.cs 两处都改，避免冲突）。

## 步骤

1. [ ] 引擎窗口：OverlayWindow.xaml.cs Configure 扩参 (left, top, width, height, fontSize, topmost, lockPosition)；全 null 时重置 _hasSavedPlacement+立即 PositionAtBottom（可见时）；PlacementChanged 加 height；Show 前按虚拟屏幕 clamp 位置；xaml：删三处 MaxWidth=680，字号绑定
2. [ ] 引擎链：src/VoxLink/Models/AppSettings.cs 加 DesktopOverlayHeight(double?)/DesktopOverlayFontSize(int=24, clamp 14-40)；UiHost.Configure 扩参透传；overlayPlacement 事件 payload 加 height；EngineHost 透传
3. [ ] UI 链：UI.Core AppSettings 加两属性（含 clamp）；ToEngineJson 序列化 desktopOverlayHeight/desktopOverlayFontSize；AppController.HandleEngineEvent overlayPlacement 写 Height
4. [ ] 页面：OverlayPage 桌面区加 宽度 Slider(240-1920,step20) + 字号 Slider(14-40) + 「重置位置与大小」按钮（Controller.ResetDesktopOverlayPlacement()：四字段 null + FontSize 24 + NotifySettingsChanged）；_updating 防回环
5. [ ] 测试：AppControllerTests ToEngineJson 往返补两字段；SettingsRepositoryTests 序列化断言；（若可行）OverlayWindow 逻辑单测

## 验证命令

dotnet build VoxLink.slnx -c Release && dotnet test VoxLink.slnx -c Release --no-build

## 实测清单（真实跑 app）

- [ ] 滑宽度→悬浮窗即时变宽；拖窗口→滑块跟随
- [ ] 滑字号→三行文字等比缩放
- [ ] 拉伸高度→重启恢复
- [ ] 重置按钮→回主屏底部居中 760 宽自动高
- [ ] 拖到副屏/右缘→重启后仍在副屏；改分辨率后不丢出屏（clamp）
