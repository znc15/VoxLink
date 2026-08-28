# 本地语音识别模型选型说明

## 目标

在纯 Windows CPU（无独显）环境下提供离线、低延迟、高质量的中英混合语音识别。
所有模型经 sherpa-onnx / whisper.cpp 在应用内一键安装、校验（大小 + SHA-256）并可切换，
不会把原始音频上传到云端。

## 候选模型调研（2026-08）

| 模型 | 语言 | 中文 CER（越低越好） | 英文 WER | 大小 | CPU 速度 | 许可证 | Windows 可安装 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **FireRedASR2-CTC (int8)** ⭐ | 中英混合 + 20+ 中文方言（粤/川/沪/闽等） | 官方 4 集平均 ~2.9–3.1%；CTC 单遍前向 | 优秀 | 约 740 MB 模型 / 520 MB 下载 | 快（CTC 单遍） | Apache-2.0 | ✅ sherpa-onnx 1.13.4 C# API |
| FireRedASR2-AED (int8) | 中英混合 + 20+ 方言 | 官方 2.89% | 优秀 | 约 1.2 GB | 中等（AED） | Apache-2.0 | ✅ sherpa-onnx C# API |
| SenseVoice-Small (int8) | 中/英/日/韩/粤 + 情感/事件 | ~8%（Q8） | 一般 | 约 449 MB | ~20× 实时 | MIT | ✅ sherpa-onnx C# API |
| FunASR Paraformer | 普通话为主 | ~10% | — | 401 MB | ~21× 实时 | MIT | ✅ sherpa-onnx |
| Whisper small | 99 语种 | 22.12% | 较好 | 466 MB | 4.6× | MIT | ✅ whisper.cpp |
| Whisper large-v3-turbo | 99 语种 | 23.15% | 最强（LibriSpeech ~1.7%） | 1.6 GB | 3.2× | MIT | ✅ whisper.cpp |

数据来源：
- [FunAudioLLM/SenseVoice llama.cpp CPU benchmark](https://github.com/FunAudioLLM/SenseVoice/blob/main/runtime/llama.cpp/BENCHMARKS.md)：SenseVoice 8.17% vs whisper.cpp small 22.12% / large-v3-turbo 23.15%（184 条中文集，Q8 CPU）；该文档明确说明 Whisper 是通用多语种模型，中文是其弱项，英文/其他语言才是强项。
- [FireRedASR2S 官方评测](https://github.com/FireRedTeam/FireRedASR2S)：普通话 4 集平均 CER 2.89%（LLM）/ 3.05%（AED），优于 Doubao-ASR 3.69%、Qwen3-ASR-1.7B 3.76%、FunASR 4.16%；19 个方言集 11.55% 同样领先。
- [FireRedASR v1 官方评测](https://github.com/FireRedTeam/FireRedASR)：中文平均 3.05%，LibriSpeech test-clean 1.73%（优于 Whisper-large-v3）。
- [sherpa-onnx FireRedAsr 文档（含 C# API、Windows x64）](https://github.com/k2-fsa/sherpa/blob/master/docs/source/onnx/FireRedAsr/index.rst)
- [Open-LLM-VTuber ASR 对比](https://docs.llmvtuber.com/docs/user-guide/backend/asr/)：默认推荐 sherpa-onnx + SenseVoiceSmall（中文/CPU 快），并指出 FireRedASR 在中英混合场景下更好。
- Windows 无独显实测（2026-02）：SenseVoice Small 中英混合明显优于 Whisper，CPU 低延迟：https://jhuang.netlify.app/blog/2026-02-13-windows-local-voice-input/

## 结论

- **中英混合 + 高准确率**：FireRedASR2-CTC（当前 VoxLink 内置，`fire-red-asr2-ctc`）。它支持普通话和 20+ 中文方言，CTC 单遍前向，CPU 上仍快，是「准确率 + 性能 + 可安装」的最优解。
- **日常中英实时 + 占用最低**：SenseVoice-Small（`sensevoice-small`），约 449 MB、~20× 实时。
- **纯英文 / 多语种**：Whisper large-v3-turbo（`whisper-large-v3-turbo`）。

## 实现

- 运行时：`org.k2fsa.sherpa.onnx 1.13.4`（C# API）与 `org.k2fsa.sherpa.onnx.runtime.win-x64 1.13.4`（原生库）。
- 模型目录条目：`src/VoxLink/Models/LocalModelCatalog.cs` 中 `fire-red-asr2-ctc`：
  - 压缩包：520,516,278 字节，SHA-256 `1da8b737…83274`
  - `model.int8.onnx`：775,861,420 字节，SHA-256 `ca3dbabd…bb99`
  - `tokens.txt`：79,172 字节，SHA-256 `1bc613de…ea07b`
- 识别器：`src/VoxLink/Services/LocalFireRedAsr2CtcRecognizer.cs`，使用 `OfflineFireRedAsrCtcModelConfig` + `greedy_search`，非流式，会话内并发串行化。
- 设置：`AsrProtocol.LocalFireRedAsr2Ctc`（UI.Core 与 Engine 双端枚举），选择后 `ToEngineJson` 显式下发 `localFireRedAsr2Ctc`，引擎 `AsrRecognizerFactory` 路由到原生识别器。
- UI：模型服务页「语音识别」下拉新增「FireRedASR2-CTC（最准确）」；系统托盘菜单同步。
- 测试：目录/工厂/归一化单元测试；`VOXLINK_RUN_LIVE_TESTS=1` 时用官方中文与天津方言样本跑真实原生解码（加载 775MB ONNX，验证输出含「星期」「法律」）。