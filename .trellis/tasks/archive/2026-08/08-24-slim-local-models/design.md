# 设计：本地模型目录精简

## 边界

改 4 层：引擎目录→ 翻译服务→ UI/托盘（ModelProvidersPage、LocalModelsPage、MainWindow 托盘、AppController、LocalModelIds 镜像）→ 测试。不动 ManagedPython/WSL 运行时代码本体（保留但无入口）；不删用户磁盘上已装旧模型。

## 数据流

1. **目录**：LocalModelCatalog.All 删 8 条 + Manifests 对应清单；新增 `hy-mt15-18b-gguf`：Category=translation、InstallKind=SingleFile、Runtime=LlamaCppGguf、HfManifest(tencent/HY-MT1.5-1.8B-GGUF, main, HY-MT1.5-1.8B-Q4_K_M.gguf, size 1133080512, sha256 4383ac0c3c8e476de98ff979c2a3f069f8c4fb385e7860cf2d28da896cc477c7)、约 1.8B、Requirements「8GB 内存起步，llama.cpp 兼容推理（GGUF Q4_K_M）」、License 沿用 tencent 社区许可。
2. **服务**：新 `LocalHyMtTextService : ITextTranslationService`，克隆 LocalMiniCpmTextService 结构（LLamaSharp 参数 GpuLayerCount=0、ContextSize、线程上限、懒加载、单飞加载锁、超时、dispose），差异点：
   - SamplingParams: temp 0.7 / TopK 20 / TopP 0.6 / RepeatPenalty 1.05（官方推荐）
   - Prompt 按目标语言选模板：target=zh 系用中文模板，否则英文模板；语言全名映射表（复用/新建 LanguageNames，目标 zh-CN → 「中文」）
   - zh-CN 输出过 ChineseTextNormalizer（架构不变量，MiniCPM 同款路径）
3. **枚举/工厂**：TranslationService 增加 `LocalHyMtGguf`；TranslationServiceFactory 注册新服务；engine AppSettings.SelectTranslationBackend 反序列化兼容旧值 `LocalManagedHyMt` → 启动回退 PublicFree（或若 hy-mt-gguf 已装则映射过去——按实现简洁度选，倾向安全回退公共免密并日志说明）。同理 LocalM2M/Small100 旧值回退。
4. **UI 镜像**：VoxLink.UI.Core/Models/LocalModelIds.cs 补 `WhisperLargeV3Turbo = "whisper-large-v3-turbo"`、`HyMt15Gguf = "hy-mt15-18b-gguf"`（并保留现有）；AppController 的 ActivateLocalModel/IsLocalModelActive/WhisperId/EnsureSelectedLocalModelsInstalled 四处补 case；WhisperId() 把 Settings.WhisperModel "large-v3-turbo" 正确映射。
5. **UI 页面/托盘**：
   - ModelProvidersPage 翻译 ComboBox：公共免密 / 本地混元翻译 HY-MT1.5-1.8B(GGUF) / 本地 MiniCPM5-1B / DeepSeek / OpenAI 兼容 / 自定义（删 M2M、SMaLL、旧混元 Python 项）
   - ASR ComboBox：加 `Whisper large-v3-turbo（最准确）` 项，删「本地 MOSS 转写+说话人」
   - 托盘 BuildTrayMenu 两个子菜单同步（删 MOSS/M2M/SMaLL，加 turbo 与 GGUF 混元）
   - LocalModelsPage 删「更多模型」Expander；AppController 删 ExperimentalLocalModelIds + ExperimentalLocalModels 属性（grep 确认无他处绑定）
6. **测试**：LocalModelCatalogTests 锁 7 个 Id（tiny/base/small/large-v3-turbo/sensevoice-small/minicpm5-1b-gguf/hy-mt15-18b-gguf/kokoro-82m —— 若 sensevoice 保留即 8 个，实施时定）；AppController ToEngineJson 往返若涉及 backend 枚举则补 LocalHyMtGguf 用例；TranslationService 单测补新服务（mock LLamaSharp 不可行则按 MiniCPM 现有测试模式）。

## 兼容性

- 旧 settings：SelectTranslationBackend 旧值回退在引擎侧 normalize（ReadSettings 后 clamp），UI 侧 SelectTranslationBackend setter 同步接受旧值映射，避免两进程不一致。
- 已装模型：LocalModelManager 只认新目录；旧目录残留忽略（不删不报错）。
- LocalModelsPage/引擎 listLocalModels 协议不变，仅内容变化。

## 回滚

单 commit 粒度，revert 即回滚；无数据迁移、无协议变更。
