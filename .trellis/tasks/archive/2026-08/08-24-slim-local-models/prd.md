# 精简本地模型目录（ASR+翻译）

## Goal

面向大众电脑（纯 Windows、CPU、与 VRChat 同开）精简本地模型目录：全部模型零 Python/WSL 依赖，修复 large-v3-turbo「能装不能选」Bug，用 GGUF 版混元翻译替换 ManagedPython 版。

## 目录方案（14 → 6）

| 保留/变更 | Id | 运行时 | 说明 |
|---|---|---|---|
| 保留 | whisper-tiny / whisper-base / whisper-small | whisper.net CPU | 现状即有 |
| 保留+修复 | whisper-large-v3-turbo | whisper.net CPU | 补 UI 侧 ID 镜像，使其真正可选 |
| 保留 | minicpm5-1b-gguf | LLamaSharp CPU | 本地翻译轻量选项 |
| **新增** | hy-mt15-18b-gguf（新 Id） | LLamaSharp CPU | tencent/HY-MT1.5-1.8B-GGUF Q4_K_M（1,133,080,512 B, sha256 4383ac0c…），替代 ManagedPython 版 |
| 保留 | kokoro-82m | sherpa-onnx | 本地 TTS |
| **删除** | m2m100-418m、small-100、hy-mt1.5-1.8b（Python 版）、sensevoice-small、moss-transcribe-diarize、cosyvoice2-0.5b、dots-tts、qwen3-tts-1.7b | — | 与大众电脑目标不符；sensevoice 与 Whisper 重复（探索者建议删，见 Notes） |

## Requirements

1. 引擎目录 LocalModelCatalog：删 8 个旧条目与对应 SHA-256 清单；新增 hy-mt15-18b-gguf（SingleFile，tencent/HY-MT1.5-1.8B-GGUF，文件 HY-MT1.5-1.8B-Q4_K_M.gguf，Requirements 标注 8GB 内存起步、llama.cpp 兼容推理）。旧 Id hy-mt1.5-1.8b 已装用户无迁移（保留磁盘文件即可，不可再选）。
2. 本地翻译服务：新建 LocalHyMtTextService（LLamaSharp，参照 LocalMiniCpmTextService：GpuLayerCount=0、线程数上限、加载/超时/错误处理风格一致），提示词按官方模板（目标语言全名、只输出译文）；翻译后过 ChineseTextNormalizer（架构不变量）。TranslationService 枚举/工厂/AppSettings.SelectTranslationBackend 增加 LocalHyMtGguf 选项；WhisperId 类似的 Id 镜像补全。
3. UI（ModelProvidersPage / LocalModelsPage / 托盘 BuildTrayMenu / AppController）：翻译下拉=公共免密/本地混元 HY-MT1.5-1.8B（GGUF）/本地 MiniCPM5-1B/DeepSeek/OpenAI兼容/自定义；ASR 下拉加 Whisper large-v3-turbo 项；删「更多模型」Expander 与 ExperimentalLocalModelIds 机制；推荐模型三件套不变；LocalModelCatalogTests 更新为 6 模型集合。
4. 用户已装被删模型：设置里 selectTranslationBackend=LocalManagedHyMt 等旧值 → 启动安全回退（公共免密或 MiniCPM，按探索现状设计），不得崩溃。引擎 listLocalModels 只返回新 6 个；已装旧模型目录不主动删除（不碰用户磁盘）。
5. 旧 ManagedPython/WSL 运行时代码（ManagedRuntimeCatalog、ModelHost adapter_*.py、LocalModelManager 的 Managed 相关分支）保留代码但无目录引用——本期不深删（改动面控制），只断开 UI/目录入口。
6. （优先级低，可不做）删除 sensevoice-small 的理由是与 Whisper 功能重复——但它是 Stable sherpa-onnx 方案。**默认保留 sensevoice-small**，除非检查后发现其 UI 入口已死。实施时最终确认。

## Acceptance Criteria

- [ ] LocalModelCatalogTests 锁定 6 模型（tiny/base/small/large-v3-turbo/minicpm/hy-mt-gguf + kokoro —— 共 7 个若含 Kokoro）全绿
- [ ] 模型服务页下拉与托盘菜单均无 M2M/SMaLL/MOSS/CosyVoice/dots/Qwen3-TTS；翻译下拉出现「本地混元翻译 HY-MT1.5-1.8B（GGUF）」
- [ ] large-v3-turbo 可在 ASR 下拉选中并激活（修 ID 镜像）
- [ ] 本地模型页无「更多模型」折叠区
- [ ] 构建零警告 + 测试全绿；实际运行 VoxLink.exe 验证列表/安装/激活
- [ ] 下载实测：hy-mt15-18b-gguf 可完整下载（校验通过）并完成一次真实翻译（用户要求的实际测试）
