# 模型服务下拉框布局修复

## Goal

模型服务页三个 ComboBox 的下拉列表错位（比正常位置低一格）。根因是代码侧 hack：DropDownOpened 时手动给模板 Popup 加 VerticalOffset = 控件高度+2。删除该 hack，恢复 WinUI 原生定位。

## Requirements

- 删除 ModelProvidersPage.xaml.cs 中 AlignDropdownBelowSelectionBar、FindTemplatePopup 及三个 DropDownOpened 接线（:223-259 快照行号，以实际代码为准）
- 保留 MaxDropDownHeight、安装态后缀（UpdateLocalOption）等无关逻辑
- 不改任何 ComboBox 模板（本就没有自定义模板）

## Acceptance Criteria

- [ ] 构建零警告、测试全绿
- [ ] 实际运行 VoxLink.exe：三个下拉框展开时列表第一行紧贴控件下边缘，与其他页 ComboBox 一致（截图对比）
