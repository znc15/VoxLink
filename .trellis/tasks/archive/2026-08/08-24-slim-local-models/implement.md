# 实施计划：本地模型目录精简

前置：combobox 修复任务先完成（同文件 ModelProvidersPage.xaml.cs 避免冲突）。

## 步骤

1. [ ] 引擎目录：LocalModelCatalog.cs 删 8 条目 + LocalModelCatalog.Manifests.cs 对应清单 + LocalModelIds 常量清理；新增 hy-mt15-18b-gguf 条目与清单（物料见 research）
2. [ ] 翻译服务：src/VoxLink/Services/LocalHyMtTextService.cs（克隆 MiniCPM 结构+官方采样参数+双模板提示词+ChineseTextNormalizer）；TranslationService 枚举 + 工厂注册
3. [ ] 旧值回退：engine + UI 两侧 SelectTranslationBackend 对 LocalManagedHyMt/LocalM2M/Small100 的 normalize
4. [ ] UI 镜像：LocalModelIds.cs(UI.Core) 补 turbo/hy-mt-gguf；AppController 四处补 case；WhisperId() 映射修正
5. [ ] 页面：ModelProvidersPage 两个 ComboBox 增删项 + tag/映射；LocalModelsPage 删 Expander；AppController 删 ExperimentalLocalModelIds；托盘 BuildTrayMenu 同步
6. [ ] 测试更新：LocalModelCatalogTests（新集合）+ 相关 AppController/TranslationService 测试修复
7. [ ] 验证：build 零警告 + test 全绿 + 全仓 grep（M2M|SMaLL|Small100|Moss|MOSS|CosyVoice|dots-tts|qwen3-tts 仅允许出现在 Managed 运行时代码本体与 docs）
8. [ ] 实测：跑 VoxLink.exe → 本地模型页发起 hy-mt15-18b-gguf 安装（真实下载 1.13GB+sha256 校验）→ 模型服务页选它 → 验证一次真实翻译输出

## 验证命令

dotnet build VoxLink.slnx -c Release
dotnet test VoxLink.slnx -c Release --no-build

## 回滚点

每步独立可编译；步骤 1-2 引擎侧、3-6 UI 侧，分两段各自可回滚。
