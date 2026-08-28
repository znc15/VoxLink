# 麦克风语音增强（降噪 + 自动增益）选型说明

## 目标

对用户输入语音（麦克风）在进入 VAD / ASR 之前做预处理，提升识别准确率：

- 降噪：抑制环境底噪、风扇/空调等平稳噪声；
- 自动增益（AGC）：把过轻/过响的说话音量统一到稳定响度，避免 Whisper 等 ASR 在小音量下的漏识。

系统音频回环（他人语音）保持原始听感，不套用本处理。

## 开源方案调研

| 方案 | 提供能力 | 许可证 | 集成难度 | 说明 |
| --- | --- | --- | --- | --- |
| [RNNoise（xiph/rnnoise）](https://github.com/xiph/rnnoise) | 神经网络降噪 | BSD-3-Clause | 中 | 体积小、CPU 占用低、降噪效果好；只做降噪，不含 AGC；以 480 样本帧为单位，需打包 rnn_data.c 模型，P/Invoke 封装简单 |
| [WebRTC AudioProcessing（webrtc-audio-processing，PulseAudio 镜像）](https://cgit.freedesktop.org/pulseaudio/webrtc-audio-processing/) | 降噪 + AGC + 高通 + 回声消除 | BSD-3-Clause | 高 | 实时语音场景最成熟，语音识别前处理的事实标准；原生库较大、构建链复杂，P/Invoke 面也大 |
| [SpeexDSP（Xiph/liunix61 镜像）](https://github.com/liunix61/speexdsp) | 降噪 + AGC + 回声消除 | LGPL-2.1 | 中 | C API 简单，适合嵌入式/桌面；降噪质量弱于 RNNoise / WebRTC，且 LGPL 对打包有通知义务 |
| [OpenVoiceSharp](https://github.com/realcoloride/OpenVoiceSharp) | 托管语音库（含处理） | 需按仓库确认 | 低 | .NET 直接可用，但以 VoIP 场景为主，许可证与维护状态需评估 |

## 选型建议

- **追求最佳识别效果**：优先集成 **WebRTC AudioProcessing** 的 NoiseSuppression + GainControl（或 GainController2），它是语音识别前处理的事实标准，但需要接受较大的原生库体积和封装成本。
- **追求轻量**：用 **RNNoise** 做降噪，再叠加一个简单 AGC（本仓库当前 RNNoise 模式使用固定增益链，若追求更稳定响度可后续接入 WebRTC 的 AGC）。
- **当前实现（设置可切换）**：同时集成两个真实引擎，可在「音频设备 → 麦克风语音增强」中切换：
  - **WebRTC APM**：WebRtcVoicePreprocessor 使用 SoundFlow.Extensions.WebRtc.Apm（webrtc-apm.dll），启用高降噪 + AGC1 AdaptiveDigital + 80 Hz 高通，按 10 ms 帧处理。
  - **RNNoise**：RnnNoiseVoicePreprocessor 使用 YellowDogMan.RRNoise.NET（rnnoise.dll），按 480 样本帧做神经网络降噪，并叠加轻量 RMS 自动增益稳定音量。
  - **Off**：关闭后直接使用原始麦克风 PCM。

两个封装实现 IVoicePreprocessor，在 WasapiSpeechCapture 的同一位置替换；调用方（TranslationSession、UI 开关）无需改动。

## 相关设置

- 设置项：VoicePreprocessingMode（Off / WebRtc / RNNoise，默认 WebRtc），位于「音频设备 → 麦克风语音增强」。
- 仅作用于麦克风采集；系统回环采集不经过处理。
- 重新开始会话后生效（预处理节点在每次会话启动时创建）。
