# 技术设计：优化启动与转译速度

## 边界

改动集中在：
- `src/VoxLink/Services/LocalModelManager.cs`（校验缓存）
- `src/VoxLink/Services/WhisperSpeechRecognizer.cs`（校验缓存 + processor 复用）
- `src/VoxLink.Engine/VoxLink.Engine.csproj` + `scripts/publish.ps1`（R2R）
- `src/VoxLink/Services/TranslationSession.cs`（TTS 不阻塞队列）
- `src/VoxLink/Services/LocalHyMtTextService.cs` / `LocalMiniCpmTextService.cs`（context 缩减 + 预热）
- `src/VoxLink/Services/FailoverTranslationService.cs`（超时调整）

不动：协议格式、设置持久化、UI 页面结构。

## 1. 模型校验缓存

`LocalModelManager.IsFileVerified` 与 `WhisperModelInstallerAdapter.IsFileVerified`、`WhisperSpeechRecognizer.VerifyModelAsync` 是三处独立的全量哈希。

方案：在各类型内部加 `ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc, bool Verified)>`，键 = 文件全路径。校验时先比 Length + LastWriteTimeUtc，命中直接返回上次结论；未命中才读文件算 SHA-256，结果写缓存。文件删除/替换时 LastWriteTimeUtc 变化自动失效。

- 安装流程（`InstallAsync`）内部走同一 `VerifyFileAsync`，下载完成后首次校验结果也进缓存——同进程内后续 `AcquireUsage`/`GetStatus` 直接命中。
- 跨进程不共享（不做落盘缓存；安装时已全量校验过一次，本进程内缓存已消除重复哈希）。
- 失败结果同样缓存（避免坏文件每次启动都全量扫），但安装覆盖写后 mtime 变化会重新校验。

## 2. 引擎 ReadyToRun

`VoxLink.Engine.csproj` 加 `<PublishReadyToRun>true</PublishReadyToRun>`（与 UI 一致）。发布脚本无需改（publish 命令读取 csproj）。注意 `TreatWarningsAsErrors` 下 R2R 警告（如 IL2007/IL3050 风格的 AOT 分析警告在纯 R2R 不触发，R2R 不做 trim，预期无警告）。CI 上验证。

## 3. TTS 不阻塞翻译队列

`TranslationSession.ProcessWorkItemAsync` 当前 `await _textToSpeech.SpeakAsync(...)`——改为 fire-and-forget 后台任务：
- 翻译完成 → `MessageReceived`（字幕/UI/Chatbox 即时）→ TTS 启动后台任务 → `RaiseReadyStatus()` 立即返回，工作队列继续处理下一句。
- 并发控制：HybridTextToSpeechService 内部 `_speechGate(1,1)` + `_activeSpeech.Cancel()` 已保证「新句打断旧句」，无需在 session 层再加闸。
- 会话停止语义：`StopCoreAsync` 已调 `_textToSpeech.Stop()`；需要保存后台 TTS 任务并在 Stop 时等待（或至多小超时）避免跨会话串音/进程退出竞态。
- `TranslateTypedTextAsync`（手动输入路径）保持 await（用户在等结果，且无排队压力）。

## 4. 本地 LLM：context 缩减 + 预热

- `LocalHyMtRuntimePool.ContextSize` 4096 → 2048；`LocalMiniCpmRuntimePool.ContextSize` 同步 4096 → 2048。提示词 + 512 输出 token 远小于 2048。StatelessExecutor 每请求新 context，分配量减半。
- 预热：`EnsureLoadedAsync` 权重加载完成后（或首次会话 Prepare），后台跑一次 1 token 的空推理丢弃结果，把 JIT/mmap/线程池热起来。实现放在 pool 内部：`EnsureLoadedAsync` 成功后 `_ = Task.Run(WarmUpAsync)` 一次（标志位防重入）。会话 `Prepare` 阶段（startSession 已有 `_recognizer.PrepareAsync`）对翻译服务没有对应钩子——用 pool 的懒加载后首个 TranslateAsync 前触发：`TranslationServiceFactory.Create` 创建 client 时通知 pool.EnsureWarmupStarted（仅本地 provider）。为避免每次 Create 重复触发，pool 内部 `_warmupStarted` 标志即可。

## 5. Whisper processor 复用

`WhisperSpeechRecognizer.TranscribeAsync` 每次调 `factory.CreateBuilder().WithLanguage(...).Build()`。改为 `ConcurrentDictionary<string, WhisperProcessor>`（键 = language.Code）缓存，复用 processor；Dispose 时统一释放。注意 processor 不是线程安全，但 `_recognitionGate(1,1)` 已串行化所有识别调用，安全。

## 6. Failover 超时

MyMemory（primary）单服务超时 6s → 4s；operation 12s → 10s。Google 挂在 secondary，MyMemory 慢时总上限控制在 10s 内。若实测 MyMemory 国内可用性差，再评估调整顺序（本次仅调超时，不改顺序）。

## 权衡与回滚

- 校验缓存：安全性依赖「mtime+size 未变 ⇒ 内容未变」；对手工篡改但保留 mtime 的攻击者不设防——威胁模型内可接受（本机磁盘文件，非安全边界）。
- TTS 后台化：极端情况下会话停止瞬间仍在播放的最后一句可能被截断——与现有 Stop() 行为一致，无回退需求。
- context 2048：若超长语句 + 长输出溢出，LLamaSharp 会抛异常，测试覆盖边界（MaxTranslateTokens=512 + 提示词模板实测 <1k token）。
- 全部改动可独立 revert，无数据迁移。

## 验证链

1. `dotnet build`（warnings as errors）+ `dotnet test` 全量。
2. 启动计时：加临时计时日志（测量后移除或降为 debug 级），对比 `listLocalModels` 往返、引擎 ready 时间。
3. 实机：启动 App → 会话启动 → 实测 HY-MT 翻译一句的延迟（日志时间戳）；朗读中下一句不被阻塞（日志验证两个 utterance 处理流水线重叠）。
4. 发布一次验证 R2R 生效 + 安装包完整性。
