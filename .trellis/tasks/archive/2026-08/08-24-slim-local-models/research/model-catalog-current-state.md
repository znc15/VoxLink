# 本地模型目录与模型服务页现状（2026-08-24 代码探索）

## 引擎目录（权威）：src/VoxLink/Models/LocalModelCatalog.cs（All，:48-295）+ LocalModelCatalog.Manifests.cs（SHA-256 清单）；ID 常量 LocalModelIds(:5-22)；定义 LocalModels.cs

全部 14 个：whisper-tiny/base/small/large-v3-turbo（Stable, WhisperCpp/WhisperGgml，whisper.net 1.9.1）；sensevoice-small（Stable, sherpa-onnx int8, 165MB tar.bz2）；minicpm5-1b-gguf（translation, Stable, LLamaSharp GGUF Q4_K_M 688MB SingleFile）；m2m100-418m、small-100、hy-mt1.5-1.8b（translation, ManagedPython=Windows Python 运行时, hy-mt 需 10GB 空间, license tencent-hy-community-license-d7d9db858500ac90）；moss-transcribe-diarize（asr, ManagedWslCuda）；kokoro-82m（tts, Stable sherpa-onnx）；cosyvoice2-0.5b、dots-tts、qwen3-tts-1.7b（tts, ManagedWslCuda）。

测试锁定：tests/VoxLink.Tests/Services/LocalModelCatalogTests.cs:9-38（RequiredModelIds 共 14、全部可安装、NumericParameterBillions<=2.0）。

## 关键 Bug：UI 侧 ID 镜像缺失

src/VoxLink.UI.Core/Models/LocalModelIds.cs(:4-30) 只有 tiny/base/small（+Kokoro？实施时确认）。后果（AppController.cs）：ActivateLocalModelAsync(:1936-1973) 对 large-v3-turbo/sensevoice 抛「未知的可运行本地模型」；IsLocalModelActive(:1917-1926) 永不标记；WhisperId()(:16-21 UI.Core) 未知名映射回 WhisperTiny；EnsureSelectedLocalModelsInstalledAsync(:1874) 同理。即 large-v3-turbo 目前「能装不能选」。

## UI 列表与「更多模型」

模型服务页 ModelProvidersPage.xaml 的三个 ComboBox 是**静态 XAML ComboBoxItem + Tag**（ASR :99-115：Whisper tiny/base/small + 本地MOSS + Soniox/硅基流动/MiMo/OpenAI兼容/自定义；翻译 :38-76：公共免密/本地MiniCPM5-1B/本地混元HY-MT1.5-1.8B/本地M2M-100 418M/本地SMaLL-100/DeepSeek/OpenAI兼容/自定义；TTS :119-152）。安装态后缀 UpdateLocalOption(:126-131)：`{name} · 已安装/请先安装`，未安装禁用。选择↔设置映射 code-behind LoadSelections/CurrentAsrTag(:42-75, Settings.WhisperModel 小写映射，未知→tiny)。

「更多模型」= LocalModelsPage.xaml:194-207 的 Expander（Header「更多模型（实验性）」），数据源 Controller.ExperimentalLocalModels；折叠判定 AppController.cs:66-72 ExperimentalLocalModelIds={MossTranscribeDiarize, HyMt1518B, Small100}；RefreshLocalModelsCoreAsync(:974-1051) 来自引擎 listLocalModels。「推荐模型」三件套 AppController.cs:59-64 = WhisperBase+MiniCpm51BGguf+Kokoro82M。

## 托盘菜单硬编码（MainWindow.xaml.cs BuildTrayMenu :421-600）

翻译服务子菜单：公共免密/本地MiniCPM5-1B/HY-MT1.5-1.8B/M2M-100 418M/SMaLL-100/DeepSeek/OpenAI兼容/自定义（带已装态+Checked）。语音识别子菜单：Whisper tiny/base/small、本地MOSS、Soniox、硅基流动、MiMo、OpenAI兼容、自定义。选择后 Settings 变更 + CommitSettingsChange()。

## 下载与运行时

- Whisper GGML：WhisperSpeechRecognizer(:153-220)，ggml-{name}.bin，hf-mirror.com pinned revision 主源 + whisper.net 官方 fallback，大小+SHA256 校验，{path}.download 临时+原子 Move。
- 目录模型：LocalModelManager(:485-660)，HfManifest URL 模式 https://huggingface.co/{repo}/resolve/{rev}/{path} + hf-mirror 镜像；host 白名单、HTTPS-only、断点续传(.resume.json)、tar.bz2 原子解包；存储 %LOCALAPPDATA%\VoxLink\models\local\{id}\（可被 --model-dir 覆盖）。
- Managed 运行时（Python/WSL）：%LOCALAPPDATA%\VoxLink\runtimes，ManagedRuntimeCatalog.cs（WindowsPython 3.12 给翻译；WSL VoxLink-Models+CUDA 给其余）；引擎 ModelHost/adapter_*.py。这些在精简后全部无引用。
- LLamaSharp：LocalMiniCpmTextService.cs（GpuLayerCount=0 纯 CPU），minicpm5-1b-gguf 为 SingleFile 安装（models/local/{id}/MiniCPM5-1B-Q4_K_M.gguf）。
- whisper.net 1.9.1（VoxLink.csproj:26-27），无 CUDA runtime 包，无任何 GPU 选项（唯一 CUDA 在 WSL adapter 内）。

## 下拉框错位元凶（combobox 任务用）

ModelProvidersPage.xaml.cs:223-259：DropDownOpened → AlignDropdownBelowSelectionBar → FindTemplatePopup 找模板 Popup 手动 popup.VerticalOffset = comboBox.ActualHeight + 2。WinUI 默认弹层本就在控件正下方，这一偏移让弹层额外下移一格 → 用户截图所见「列表不在下拉框正下方」。

## 新模型物料（HY-MT1.5-1.8B-GGUF 官方仓库 tencent/HY-MT1.5-1.8B-GGUF）

- 文件：HY-MT1.5-1.8B-Q4_K_M.gguf，size=1,133,080,512，sha256=4383ac0c3c8e476de98ff979c2a3f069f8c4fb385e7860cf2d28da896cc477c7（LFS oid 即 SHA-256）。Q6_K=1,474,785,216、Q8_0=1,908,528,288 备选，只取 Q4_K_M。
- 提示词（官方模板，语言用全名）：「Translate the following text into {target_lang}. Note that you should only output the translated result without any additional explanation: {source_text}」（中文提示同义变体亦可）。无默认 system prompt。推荐参数 temperature 0.7 / top_p 0.6 / top_k 20 / repetition_penalty 1.05（Hy-MT2 卡参数，1.5 相近，实施实测为准）。
- license：与 Python 版同源 tencent 社区许可（沿用 tencent-hy-community-license-d7d9db858500ac90）。
- 注意：Hy-MT2 GGUF 依赖 llama.cpp PR#22836 STQ 内核，LLamaSharp 跑不了 → 明确不用。

## 官方提示词与采样参数（HY-MT1.5-1.8B-GGUF README 实录）

- ZH<=>XX：「将以下文本翻译为{target_language}，注意只需要输出翻译后的结果，不要额外解释：\n\n{source_text}」（target_language 用中文名）
- XX<=>XX（非中文参与）：「Translate the following segment into {target_language}, without additional explanation.\n\n{source_text}」（语言用英文名）
- 采样：temperature 0.7 / top_k 20 / top_p 0.6 / repetition_penalty 1.05；无默认 system prompt
- chat 模板（ollama TEMPLATE 实录，DeepSeek 风格，GGUF 已内嵌）：`<｜hy_begin▁of▁sentence｜>[System<｜hy_place▁holder▁no▁3｜>]<｜hy_User｜>{prompt}<｜hy_Assistant｜>`——LLamaSharp 用 ChatSession/ApplyChatTemplate 即可，不需手拼 token
- llama.cpp 实测命令用 Q8_0；我们选 Q4_K_M（1.13GB）省空间，质量损失极小
