# Third-Party Notices

VoxLink includes redistributable Windows App SDK and self-contained .NET runtime components. The release package includes the complete upstream license files at:

- `WINDOWS-APP-SDK-LICENSE.txt`
- `WINDOWS-APP-SDK-NOTICES.txt`
- `DOTNET-LICENSE.txt`
- `DOTNET-THIRD-PARTY-NOTICES.txt`
- `engine/DOTNET-LICENSE.txt`
- `engine/DOTNET-THIRD-PARTY-NOTICES.txt`
- `engine/OPENVR-LICENSE.txt`
- `engine/SHERPA-ONNX-LICENSE.txt`
- `engine/ONNXRUNTIME-LICENSE.txt`
- `engine/ONNXRUNTIME-THIRD-PARTY-NOTICES.txt`

## Windows UI Runtime

- Microsoft Windows App SDK and WinUI 3, distributed under the Microsoft Windows App SDK license included with the package
- Microsoft WebView2 and other transitive Windows App SDK components, with notices included in `WINDOWS-APP-SDK-NOTICES.txt`
- .NET runtime and Windows Desktop runtime, MIT License, Copyright .NET Foundation and Contributors
- `System.Security.Cryptography.ProtectedData`, part of the .NET runtime libraries

The retired Flutter frontend under `src/voxlink_app` is not built into or distributed with the WinUI release package.

## Audio Engine

- NAudio, Microsoft Public License (Ms-PL): <https://github.com/naudio/NAudio>
- EdgeTTS.DotNet, MIT License: <https://github.com/twn39/EdgeTTS.DotNet>
- Whisper.net, MIT License: <https://github.com/sandrohanea/whisper.net>
- Whisper.net.Runtime / whisper.cpp runtime, MIT License: <https://github.com/ggml-org/whisper.cpp>
- LLamaSharp `0.27.0` managed API and CPU backend (llama.cpp), MIT License: <https://github.com/SciSharp/LLamaSharp>
- SharpCompress `0.50.3`, MIT License: <https://github.com/adamhathcock/sharpcompress>
- System.Speech, MIT License: <https://github.com/dotnet/runtime>
- sherpa-onnx `1.13.4` managed API and Windows x64 runtime, Apache License 2.0: <https://github.com/k2-fsa/sherpa-onnx/tree/v1.13.4>
- Microsoft ONNX Runtime `1.27.0`, MIT License: <https://github.com/microsoft/onnxruntime/tree/v1.27.0>
- SoundFlow.Extensions.WebRtc.Apm `1.4.0`, MIT License: <https://github.com/LSXPrime/SoundFlow>; the bundled native `webrtc-apm.dll` is derived from Google's WebRTC Audio Processing Module (BSD 3-Clause) and carries `SOUNDFLOW-THIRD-PARTY-NOTICES.txt`
- YellowDogMan.RRNoise.NET `0.1.9`, MIT License: <https://github.com/Yellow-Dog-Man/RNNoise.Net>; the bundled native `rnnoise.dll` is derived from xiph/rnnoise (BSD 3-Clause): <https://github.com/xiph/rnnoise>

Source versions, upstream commits, and imported-file SHA-256 values are recorded in `src/VoxLink/ThirdParty/SherpaOnnx/README.md` and distributed as `engine/SHERPA-ONNX-NOTICES.md`. Windows App SDK self-contained ML assets in the release root remain covered by `WINDOWS-APP-SDK-LICENSE.txt` and `WINDOWS-APP-SDK-NOTICES.txt`; the separate files under `engine/` cover the sherpa runtime's ONNX Runtime dependency.

## VR Runtime

- Valve OpenVR `v2.15.6` C# bindings and Windows x64 runtime, BSD 3-Clause License: <https://github.com/ValveSoftware/openvr/tree/v2.15.6>

The OpenVR runtime is loaded only when SteamVR subtitles are enabled or tested. It does not provide VRChat voice or chat input; VoxLink continues to capture other-player audio through local WASAPI loopback. The complete Valve license is distributed as `engine/OPENVR-LICENSE.txt`.
## Models and Online Services

Whisper tiny/base/small model files are downloaded on demand from the `ggerganov/whisper.cpp` Hugging Face repository or a mirror. They are not bundled in the release package; see the upstream model card and provenance.

MiniCPM5-1B GGUF is downloaded on demand from `openbmb/MiniCPM5-1B-GGUF` at the revision pinned in source. The repository and weights are Apache License 2.0. VoxLink uses the Q4_K_M file through LLamaSharp/llama.cpp as a general instruction model prompted for translation and refinement; it is not represented as a translation-specialized checkpoint.

Kokoro-82M model files are downloaded on demand from the sherpa-onnx `tts-models` release, with the archive and critical extracted artifacts pinned by byte length and SHA-256. Kokoro-82M and sherpa-onnx are Apache License 2.0; the archive includes linguistic data used by the upstream Kokoro configuration. Model weights are not bundled in the VoxLink release.

SenseVoice-Small and FireRedASR2-CTC model files are downloaded on demand from the sherpa-onnx `asr-models` release, with the archive and critical extracted artifacts pinned by byte length and SHA-256. SenseVoice-Small is MIT-licensed (FunAudioLLM/SenseVoice); FireRedASR2-CTC is converted from FireRedTeam/FireRedASR2-AED (Apache License 2.0). Model weights are not bundled in the VoxLink release.

Catalog-only entries such as dots.tts, HY-MT1.5-1.8B, and MOSS-Transcribe-Diarize are not downloaded or distributed by VoxLink. The UI links to their upstream terms and runtime requirements. In particular, HY-MT1.5-1.8B uses the Tencent HY Community License, which excludes the European Union, United Kingdom, and South Korea; it must not be treated as Apache-2.0 or as an unrestricted open-source dependency.
The optional CAMPPlus Chinese-English 16 kHz speaker-embedding model is not bundled. When local anonymous speaker labels are first enabled, VoxLink downloads `3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx` from the sherpa-onnx speaker-model release and accepts it only when its size and SHA-256 match the values pinned in the source. The sherpa-onnx release states that each model has its own license and refers users to the corresponding model repository; the 3D-Speaker code repository is Apache-2.0, but users remain responsible for the model-weight terms that apply in their jurisdiction.

Default no-key translation and online text-to-speech rely on third-party public services, including MyMemory, Google Translate, and Microsoft Edge Read Aloud. Optional DashScope, DeepSeek, MiMo, OpenAI-compatible, SiliconFlow, Soniox, and custom services are not bundled with VoxLink and may apply their own terms, quotas, data-retention policies, and regional restrictions. Cloud ASR is disabled until the user explicitly enables raw-audio upload.

## EdgeTTS.DotNet License

MIT License

Copyright (c) 2025 Curry

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
