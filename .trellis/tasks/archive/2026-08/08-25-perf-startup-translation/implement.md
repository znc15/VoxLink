# 实施计划：优化启动与转译速度

## 顺序与检查点

### 第 1 步：模型校验缓存（启动优化核心）
- [ ] `LocalModelManager`：内部 `ConcurrentDictionary` 缓存路径→(length, mtime, verified)；`IsFileVerified`/`VerifyFileAsync` 命中缓存跳过哈希；失败结果也缓存
- [ ] `WhisperSpeechRecognizer`：`VerifyModelAsync`（`IsModelFileUsableAsync` 路径）同样加缓存（静态或实例，注意 `WhisperModelInstallerAdapter` 每次新建 recognizer 实例——缓存必须可跨实例共享，用静态 ConcurrentDictionary 或注入；选静态，键含路径与期望哈希）
- [ ] 单测：首次校验算哈希、同 mtime 二次调用不算（可注入探针/小文件）、篡改文件后（mtime 变）重新校验
- 验证：`dotnet test tests/VoxLink.Tests/VoxLink.Tests.csproj --filter "FullyQualifiedName~LocalModelManagerTests"`

### 第 2 步：引擎 R2R
- [ ] `src/VoxLink.Engine/VoxLink.Engine.csproj` 加 `PublishReadyToRun=true`（仅 Release publish 生效，本地 build 不受影响）
- 验证：本地 `dotnet publish` 引擎项目确认产物变化无警告；CI 全绿

### 第 3 步：TTS 后台化（转译流水线核心）
- [ ] `TranslationSession.ProcessWorkItemAsync`：`SpeakAsync` 改为记录后台 task 后立即 `RaiseReadyStatus()`；字段 `_speechPlaybackTask`，Stop 时 await（带 2s 上限）
- [ ] 保持 `TranslateTypedTextAsync` await 不变
- [ ] 单测：模拟慢 TTS，验证下一 workItem 在 TTS 完成前开始处理
- 验证：`dotnet test --filter "FullyQualifiedName~TranslationSession"`

### 第 4 步：本地 LLM context 缩减 + 预热
- [ ] `LocalHyMtRuntimePool.ContextSize` 4096→2048；`LocalMiniCpmRuntimePool.ContextSize` 4096→2048
- [ ] pool 内 `_warmupStarted` 标志 + 后台 1-token 预热推理（加载完成后触发；`CreateClient()` 不必触发——首次 CompleteAsync 的 EnsureLoadedAsync 后跑）
- [ ] 单测：ContextSize 常量断言；预热不破坏 Dispose 流程
- 验证：`dotnet test --filter "FullyQualifiedName~LocalHyMt|FullyQualifiedName~LocalMiniCpm"`（如有）

### 第 5 步：Whisper processor 复用
- [ ] `WhisperSpeechRecognizer`：`ConcurrentDictionary<string, WhisperProcessor>`（键=语言码），`TranscribeAsync` 复用；`DisposeAsync` 统一释放；换模型文件时清空缓存
- 验证：现有 whisper 测试绿（大部分为 mock，行为不变）

### 第 6 步：Failover 超时
- [ ] `FailoverTranslationService` 默认 serviceTimeout 6s→4s，operationTimeout 12s→10s
- [ ] 单测更新（TranslationServiceTests 中 failover 相关断言）

### 第 7 步：实测验证（全部真机）
- [ ] 构建 Release，启动 App，日志计时：引擎 ready、initialize、listLocalModels 往返；二次启动（缓存命中）对比
- [ ] 启动会话（本地 Whisper base + HY-MT），实测一句到字幕端到端延迟，记录 optimize 前后数值（optimize 前基线先测）
- [ ] 长朗读 + 连续说话：验证朗读不阻塞下一句翻译（日志时间戳流水线重叠）
- [ ] Chatbox / 悬浮字幕回归
- [ ] 移除临时计时或降级为 Debug 级日志

### 第 8 步：收尾
- [ ] 全量 `dotnet test`
- [ ] 版本 1.5.1 → 1.5.2（Directory.Build.props）
- [ ] 提交（中文 conventional commit：`perf(engine): ...`）
- [ ] 更新 spec / 任务归档

## 回滚点

每步独立提交粒度（至少逻辑独立），任一步出问题单独 revert。

## 测量基线（实施前先测，留给第 7 步对比）

实施前先在当前 HEAD 测：
- App 冷启动到「软件已就绪」日志时间差
- listLocalModels 往返
- HY-MT 单句翻译延迟（≈420ms warm，首句冷启动待测）
