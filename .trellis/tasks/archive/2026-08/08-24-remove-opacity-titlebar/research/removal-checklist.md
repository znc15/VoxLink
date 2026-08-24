# 删除清单：WindowOpacity（软件整体透明度）+ UseSystemTitleBar（标题栏模式）

来自 2026-08-24 代码探索，行号为当时快照，实施时以实际文件为准。

## WindowOpacity（引擎侧零涉及，纯 UI 进程）

1. `src/VoxLink.UI.Core/Models/AppSettings.cs` — `_windowOpacity = 1.0`(:147)；属性 `WindowOpacity` 含 `Math.Clamp(…, 0.2, 1.0)`(:343-349)
2. `src/VoxLink.UI/Pages/AdvancedPage.xaml:71-87` — 「软件整体透明度」StackPanel：`OpacityValueText`(:78)、`WindowOpacitySlider`(Min 0.2, Max 1, step 0.05, :80-86)
3. `src/VoxLink.UI/Pages/AdvancedPage.xaml.cs` — slider 初始化 :28、:50；`RefreshOpacityLabel()` 调用 :35、:57；handler `WindowOpacitySlider_ValueChanged` :61-70；`RefreshOpacityLabel` :72-73
4. `src/VoxLink.UI/MainWindow.xaml.cs` — `ApplyWindowOpacity()` :240-262（SetWindowLongPtr 加 WS_EX_LAYERED + SetLayeredWindowAttributes alpha）；调用点 `RootLayout_Loaded` :236、`Settings_PropertyChanged` case `WindowOpacity` :165-168。随之可删（仅被它使用）：常量 `GwlExstyle`/`WsExLayered`/`LwaAlpha`(:27-29)、P/Invoke `GetWindowLongPtr`/`SetWindowLongPtr`(:46-50)、`SetLayeredWindowAttributes`(:52-58)。注意 `ApplyWindowIcon` 用的是 SendMessageW/LoadImageW/GetSystemMetrics，不受影响——删除前 grep 确认。
5. `tests/VoxLink.Tests/UI/SettingsRepositoryTests.cs:71`（`WindowOpacity = 0.65`）与 :129（断言）
6. 用户已有 settings.json 里的 `windowOpacity` 旧键：反序列化大小写不敏感、未知键下次保存即丢弃，无需迁移代码（确认 SettingsRepository 无严格反序列化报错）。

## UseSystemTitleBar（引擎侧零涉及）

1. `src/VoxLink.UI.Core/Models/AppSettings.cs` — `_useSystemTitleBar`(:144, 默认 false=自绘)；属性 :340
2. `src/VoxLink.UI/Pages/AdvancedPage.xaml:66-70` — `ToggleSwitch Header="标题栏模式"` OffContent=「自绘标题栏」OnContent=「系统标题栏」
3. `src/VoxLink.UI/MainWindow.xaml.cs` `ApplyWindowChrome()`(:123-145) 中标题栏块 :137-144：`ExtendsContentIntoTitleBar = !useSystemTitleBar`、`AppTitleBar.Visibility`、`NavView.IsPaneToggleButtonVisible = useSystemTitleBar`、`SetTitleBar(AppTitleBar)`。响应式：`Settings_PropertyChanged` :149-155（`nameof(AppSettings.UseSystemTitleBar)` 触发 + 日志）。删除后固定走自绘分支（ExtendsContentIntoTitleBar=true、AppTitleBar 可见、PaneToggleButtonVisible=false、SetTitleBar）。
4. `MainWindow.xaml:19-29` 的 `AppTitleBar` 元素与 `AppTitleBar_PaneToggleRequested`(:171-172) 保留（自绘标题栏仍需要）。
5. 无测试引用（grep 已确认）。

## 删除后必须全仓 grep 验证的关键词

`WindowOpacity` `windowOpacity` `UseSystemTitleBar` `useSystemTitleBar` `WindowOpacitySlider` `ApplyWindowOpacity` `OpacityValueText` `RefreshOpacityLabel` `SetLayeredWindowAttributes` `系统标题栏` `软件整体透明度`
