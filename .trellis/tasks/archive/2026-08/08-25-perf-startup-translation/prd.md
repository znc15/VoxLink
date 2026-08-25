# PRD：优化启动与转译速度

## 背景

用户反馈：希望优化软件启动速度和实时转译（语音→识别→翻译→输出）速度。VoxLink 是与 VRChat 同开的实时语音翻译工具，延迟直接决定可用性。

调研结论（2026-08-25，基于代码通读 + 本机实测）：

- **启动**：`listLocalModels` 每次启动对每个已装模型全量 SHA-256 重哈希（本机 1.74GB 已装 ≈ 数百 ms～秒级，机械盘更糟）；`AcquireUsage`（会话启动）与 Whisper `PrepareAsync` 又各哈希一遍。引擎发布未开 ReadyToRun。
- **转译**：TTS 播放 `await` 阻塞翻译工作队列（上一句朗读 4s → 下一句翻译排队等 4s，队列上限 8 会 DropOldest 丢句）；HY-MT/MiniCPM 每句重建 4096 context；Whisper 每句重建 processor；公共免密翻译 MyMemory→Google 串行 6s 服务级超时。

## 需求

1. 模型文件校验结果按「大小 + LastWriteTimeUtc」缓存（内存缓存即可，同进程生命周期），命中即跳过全量 SHA-256；缓存键必须含文件路径。落盘缓存不做（首次下载/安装时已做过全量校验，启动内缓存解决重复哈希）。
2. 引擎（VoxLink.Engine）发布开启 ReadyToRun，缩短引擎冷启动 JIT 时间。
3. TTS 播放不再阻塞翻译工作队列：朗读放后台，字幕/后续句照常处理；新句打断旧句语义保持（HybridTextToSpeechService 已有 `_activeSpeech.Cancel` 机制）。
4. HY-MT / MiniCPM 推理 context 4096 → 2048，并在会话启动 Prepare 阶段做一次预热推理，消除首句冷启动（权重加载/JIT/mmap 换页）。
5. Whisper processor 按语言缓存复用（当前每次 TranscribeAsync 重建）。
6. 公共免密翻译：MyMemory 失败后切 Google 的服务级超时 6s 偏长，压缩到合理值（结合实测），避免单句卡 12s。

## 非目标

- 不改 VAD 阈值/断句策略等用户可感知行为（除非实测证明有明确收益且用户同意）。
- 不引入 GPU/CUDA 路径（保持纯 CPU 兼容大众电脑）。
- 不重构 managed Python/WSL 运行时（已下线维护状态）。

## 验收标准

- 全量 `dotnet test` 绿（521 项基线）。
- 启动速度：用日志计时对比 optimize 前后，`listLocalModels` 往返耗时在本机（已装 1.74GB 模型）明显下降（哈希缓存命中路径 <50ms）；实际测试启动 App 到「软件已就绪」。
- 转译速度：实际运行 App 启动会话，实测一条语音到字幕出现的端到端延迟对比；朗读长句时下一句翻译不再被阻塞（实测验证）；本地 HY-MT 翻译延迟实测记录。
- R2R：发布产物含 AOT 编译代码（或以构建日志/文件对比佐证引擎启动加快）。
- 行为回归：切 TTS、字幕、Chatbox 均正常（人工实测）。
