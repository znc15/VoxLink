# 模型服务下拉框错位：根因与修复方向（2026-08-24）

## 根因

src/VoxLink.UI/Pages/ModelProvidersPage.xaml.cs:223-259：
- `DropDownOpened` 事件 → `AlignDropdownBelowSelectionBar()`(:232-239) → `FindTemplatePopup()`(:241-259) 遍历可视化树找 ComboBox 模板里的 Popup，然后手动 `popup.VerticalOffset = comboBox.ActualHeight + 2`。
- WinUI ComboBox 默认弹层本来就定位在控件正下方（Popup 默认 offset 语义），此 hack 在默认行为之上再加一个控件高度的偏移 → 弹层整体比预期低约一行，即用户截图「列表不在下拉框正下方」。
- 接线处 :223-230 对三个 ComboBox（Asr/Translation/Speech）都挂了。

## 修复方向

直接删除 AlignDropdownBelowSelectionBar + FindTemplatePopup + DropDownOpened 接线，恢复 WinUI 原生下拉定位。保留 UpdateLocalOption 的安装态后缀逻辑（与定位无关）。

注意：`MaxDropDownHeight="320"`（XAML :103 等）保留，与错位无关。

## 修复后验证

运行 VoxLink.exe → 模型服务页 → 逐个展开三个下拉框：弹层第一行应紧贴控件下边缘（间隙仅 ComboBox 内边距），无额外一截空白。与设置页/其他页 ComboBox 对照一致。
