# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

VoxLink is a bidirectional real-time speech-translation desktop app for Windows, built for VRChat and similar multiplayer games. It captures the user's microphone (outbound) and system audio loopback (inbound), recognizes speech locally or in the cloud, translates it, and sends results to the VRChat Chatbox, desktop/SteamVR subtitles, or a virtual sound card as translated TTS.

For the full design document, read `docs/architecture.md` — it is the authoritative source for the pipeline, output policies, and key decisions.

## Build & test

Requires Windows 10 2004+ x64, the .NET 10 SDK, and a reachable NuGet feed. All projects target `net10.0-windows`, `x64` only. `Directory.Build.props` sets `TreatWarningsAsErrors=true`, so build failures are often warnings.

```bash
dotnet build VoxLink.slnx -c Release
dotnet test VoxLink.slnx -c Release --no-build
```

Run a single test (xUnit filter on fully-qualified name):

```bash
dotnet test tests/VoxLink.Tests/VoxLink.Tests.csproj \
  --filter "FullyQualifiedName~TranslationServiceTests"
```

Tests are mocked by default — no API keys or hardware needed. Tests touching real Whisper / WASAPI / VRChat / SteamVR / cloud services only run when `VOXLINK_RUN_LIVE_TESTS=1`. This gate is checked with `Environment.GetEnvironmentVariable(...)` inside the tests (e.g. `tests/VoxLink.Tests/Integration/`), so a plain `dotnet test` skips them.

Publish a self-contained portable ZIP + Inno Setup installer (compat: Windows PowerShell 5.1):

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1
```

Artifacts land in `artifacts/release/`. `publish.ps1` reads `<Version>` from `src/VoxLink.UI/VoxLink.UI.csproj`, builds the installer via `scripts/installer.iss`, and writes LF-terminated `.sha256` sidecars. `scripts/fetch-inno.ps1` downloads the Inno Setup compiler on first use.

## Architecture: two-process model

The app is split across a WinUI 3 frontend and a .NET engine sidecar that talk over **stdin/stdout JSON Lines** (UTF-8, one JSON object per line):

```
VoxLink.exe (WinUI 3, src/VoxLink.UI)
  │  request:  { id, method, params }
  │  response: { id, result | error }
  │  event:    { event, data }
  ▼
engine/VoxLink.Engine.exe (.NET sidecar, src/VoxLink.Engine + src/VoxLink)
```

- The **WinUI process** owns the visible workspace, settings persistence, validation, and app lifecycle. It spawns one engine, correlates requests by numeric id, consumes async events, and sends `shutdown` on exit.
- The **engine process** owns everything that needs raw Windows access: WASAPI capture, ASR, translation orchestration, TTS, global hotkeys, VRChat OSC, and both subtitle hosts (a WPF topmost desktop overlay and a SteamVR/OpenVR overlay).

All stdin writes are serialized by `VoxLink.UI.Core/Services/EngineClient.cs`. If the process closes, crashes, times out on start, or the stdin write fails, pending requests resolve immediately.

### Project layout (in `src/`)

- **`VoxLink.UI`** — WinUI 3 app (`VoxLink.exe`). `App.xaml.cs` (single-instance + DI), `MainWindow.xaml` (Mica title bar, NavigationView shell), `Pages/` (Live, Providers, Audio, VRChat, Advanced, Logs), `Controls/` (OnboardingDialog, HeaderEditor).
- **`VoxLink.UI.Core`** — frontend logic, no WinUI dependency. `ViewModels/AppController.cs` is the central state/validation/command-dispatch brain. `Services/` holds `EngineClient` (sidecar + protocol), `SettingsRepository` (settings + DPAPI secrets + migration), `ReleaseChecker` (GitHub Releases update checks), `LogService` (singleton + disk logs).
- **`VoxLink.Engine`** — sidecar entry. `Program.cs` (JSON Lines loop), `EngineHost.cs` (command dispatch, event payloads, Chatbox gating, secret redaction), `UiHost.cs` (STA WPF thread: desktop `OverlayWindow`, SteamVR overlay, global hotkeys), `SecretRedactor.cs`.
- **`VoxLink`** — the engine's core library: `Audio/` (WASAPI capture, VAD segmenter, PCM encoding), `Services/` (ASR factory + recognizers, translation services with failover, TTS, speaker labeling, VRChat OSC, SteamVR). It is `UseWPF` so the desktop subtitle `OverlayWindow.xaml` can live here and be driven by the engine's `UiHost`. Note: `App.xaml`/`MainWindow.xaml` in this project are legacy WPF-app-shell files that are **no longer launched** (production runs `VoxLink.UI.exe` + `VoxLink.Engine.exe`); do not wire new UI work into them.
- **`voxlink_app/`** — the old Flutter frontend, retained only as a migration reference. Do not build or edit it; the CI and publish path ignore it. (A nested `build/windows/.../VoxLink.slnx` exists but is unrelated to the root solution.)

## Key architectural invariants

When changing engine/UI behavior, preserve these (see `docs/architecture.md` §关键决策 for the full list):

- **Local-first, explicit upload.** Default is local Whisper; raw audio never leaves the machine. Any cloud ASR requires the user to enable `AllowCloudAudioUpload`, and the engine validates this defensively. Online translation receives text only; online TTS receives only the actually-spoken outbound/primary translation.
- **Protocol drives transport shape.** DashScope/Soniox → persistent WebSocket streaming. OpenAI/SiliconFlow → segmented WAV multipart. MiMo → `input_audio`. Custom providers must declare a protocol.
- **Two-level bounded queues.** Per-source streaming audio queue (cap 40, drop oldest) keeps WASAPI callbacks non-blocking; a single-reader serial final-queue (cap 8) processes translation/output so stale utterances can't pile up. WASAPI callbacks only do non-blocking `TryWrite`.
- **Capability-driven degradation.** Local speaker labeling needs complete segmented audio (safe-degrades on streaming). Cloud speaker IDs only honored from Soniox. SteamVR, speaker labels, and MuteSelf failures are isolated as optional-feature errors — they must not break core translation.
- **Settings split.** Plain settings → `%APPDATA%\VoxLink\settings.json`. API keys + custom headers → `%APPDATA%\VoxLink\secrets.dat` (DPAPI-encrypted for the current Windows user). Don't put secrets in XAML keys or plain bindable objects; use `HeaderEditor` / `PasswordBox` patterns.
- **Simplified-Chinese normalization.** Public translation providers target `zh-CN`; ASR/translation/refinement output for `zh-CN` is passed through `ChineseTextNormalizer` (Windows `LCMapStringEx`) for final glyph normalization. DashScope/Soniox/MiMo ASR protocol language codes stay `zh`.
- **Capture-source changes need a session restart.** `configure` does not rebuild the capture graph — changing `CaptureMicrophone`/`CaptureSystemAudio`/device IDs sets `NeedsSessionRestart`, surfaced as a banner.

## Conventions

- **Commits** use Chinese conventional commits: `类型(范围): 描述` with types `feat / fix / docs / refactor / test / chore` (see `b9fb40d` style).
- **Versioning**: bump `<Version>` (and File/AssemblyVersion) in `src/VoxLink.UI/VoxLink.UI.csproj` whenever there's a meaningful change. Release = tag `vX.Y.Z`; CI builds and publishes the Release, and the app checks GitHub Releases on startup.
- **CI** (`.github/workflows/ci.yml`) on `main` pushes/PRs: build → test → publish → upload artifacts; `v*` tags also call `gh release create`.
- UI strings and commit messages are in Chinese; match surrounding files' language and comment density.
