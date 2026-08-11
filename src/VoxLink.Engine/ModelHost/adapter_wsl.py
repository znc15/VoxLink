#!/usr/bin/env python3
"""VoxLink managed WSL2 + NVIDIA model adapters (T5).

Real inference for the four private-distribution models:

- ``moss-transcribe-diarize`` -> transformers remote-code ASR + diarization
- ``dots-tts``                -> dots_tts.runtime.DotsTtsRuntime (pip wheel)
- ``qwen3-tts-1.7b``          -> qwen_tts.Qwen3TTSModel voice clone (pip wheel)
- ``cosyvoice2-0.5b``         -> blocked: binary-only supply chain cannot
  satisfy omegaconf/antlr4 + openai-whisper (sdist-only); a fixed dependency
  error is surfaced until the adapter moves to the ONNX exports.

Audio results are written as WAV files under the app-managed model root and
returned as relative paths; the client resolves them inside the leased model
directory. All failures surface fixed messages without paths, stack traces,
or model output.
"""

from __future__ import annotations

import os
import sys
import uuid
from typing import Any

CUDA_AVAILABLE = False


class AdapterError(Exception):
    """Fixed safe adapter failure (no paths/output in the message)."""


def _require_cuda() -> None:
    global CUDA_AVAILABLE
    if not CUDA_AVAILABLE:
        try:
            import torch

            CUDA_AVAILABLE = bool(torch.cuda.is_available())
        except BaseException:
            CUDA_AVAILABLE = False
    if not CUDA_AVAILABLE:
        raise AdapterError("需要 NVIDIA CUDA GPU 才能运行该模型。")




class BaseAdapter:
    model_id: str = ""

    def __init__(self, model_root: str) -> None:
        if not model_root or not os.path.isdir(model_root):
            raise AdapterError("托管模型目录不可用。")
        self._model_root = os.path.abspath(model_root)
        self._output_dir = os.path.join(self._model_root, "outputs")
        os.makedirs(self._output_dir, exist_ok=True)
        sys.path.insert(0, self._model_root)

    @property
    def loaded(self) -> bool:
        raise NotImplementedError

    def load(self) -> None:
        raise NotImplementedError

    def infer(self, parameters: dict[str, Any]) -> dict[str, Any]:
        raise NotImplementedError

    def unload(self) -> None:
        raise NotImplementedError

    def cancel(self) -> bool:
        return True

    def _new_output_path(self, suffix: str) -> str:
        return os.path.join(self._output_dir, f"{uuid.uuid4().hex}{suffix}")


class MossAdapter(BaseAdapter):
    """MOSS-Transcribe-Diarize: transformers remote-code ASR + speaker labels."""

    model_id = "moss-transcribe-diarize"

    def __init__(self, model_root: str) -> None:
        super().__init__(model_root)
        self._model = None
        self._processor = None

    @property
    def loaded(self) -> bool:
        return self._model is not None

    def load(self) -> None:
        if self.loaded:
            return
        _require_cuda()
        try:
            import torch
            from transformers import AutoModelForCausalLM, AutoProcessor

            self._processor = AutoProcessor.from_pretrained(
                self._model_root, trust_remote_code=True
            )
            self._model = AutoModelForCausalLM.from_pretrained(
                self._model_root, trust_remote_code=True
            )
            self._model.eval()
            self._model.to("cuda")
        except AdapterError:
            raise
        except BaseException:
            raise AdapterError("托管模型加载失败，请修复模型文件后重试。") from None
        if self._model is None or self._processor is None:
            raise AdapterError("托管模型加载失败，请修复模型文件后重试。")

    def infer(self, parameters: dict[str, Any]) -> dict[str, Any]:
        if not self.loaded:
            raise AdapterError("托管模型尚未加载。")
        audio_path = parameters.get("audioPath")
        if not isinstance(audio_path, str) or not audio_path:
            raise AdapterError("音频路径无效。")
        abs_audio = os.path.abspath(audio_path)
        if not os.path.isabs(audio_path):
            abs_audio = os.path.join(self._model_root, audio_path)
        if not os.path.isfile(abs_audio):
            raise AdapterError("音频文件不可用。")
        try:
            import torch

            inputs = self._processor(
                audio=abs_audio,
                return_tensors="pt",
                sampling_rate=self._processor.feature_extractor.sampling_rate,
            ).to("cuda")
            with torch.inference_mode():
                generated = self._model.generate(**inputs)
            text = self._processor.decode(
                generated[0], skip_special_tokens=True
            ).strip()
            return {"text": text}
        except AdapterError:
            raise
        except BaseException:
            raise AdapterError("托管模型推理失败，请重试或修复运行时。") from None

    def unload(self) -> None:
        self._model = None
        self._processor = None


class DotsTtsAdapter(BaseAdapter):
    """dots.tts: DotsTtsRuntime text-to-speech with optional voice cloning."""

    model_id = "dots-tts"

    def __init__(self, model_root: str) -> None:
        super().__init__(model_root)
        self._runtime = None

    @property
    def loaded(self) -> bool:
        return self._runtime is not None

    def load(self) -> None:
        if self.loaded:
            return
        _require_cuda()
        try:
            from dots_tts.runtime import DotsTtsRuntime

            self._runtime = DotsTtsRuntime.from_pretrained(
                self._model_root, precision="bfloat16"
            )
        except AdapterError:
            raise
        except BaseException:
            raise AdapterError("托管模型加载失败，请修复模型文件后重试。") from None

    def infer(self, parameters: dict[str, Any]) -> dict[str, Any]:
        if not self.loaded:
            raise AdapterError("托管模型尚未加载。")
        text = parameters.get("text")
        if not isinstance(text, str) or not text.strip():
            raise AdapterError("待合成文本不能为空。")
        prompt_audio = parameters.get("referenceAudioPath")
        prompt_text = parameters.get("referenceText")
        if prompt_audio is not None and not isinstance(prompt_audio, str):
            raise AdapterError("参考音频参数无效。")
        if prompt_text is not None and not isinstance(prompt_text, str):
            raise AdapterError("参考文本参数无效。")
        try:
            result = self._runtime.generate(
                text=text,
                prompt_audio_path=prompt_audio,
                prompt_text=prompt_text,
                num_steps=10,
                guidance_scale=1.2,
            )
            audio = result["audio"].float().cpu().squeeze()
            sample_rate = int(result["sample_rate"])
            wav_path = self._new_output_path(".wav")
            import soundfile as sf

            sf.write(wav_path, audio.numpy(), sample_rate)
            rel = os.path.relpath(wav_path, self._model_root).replace("\\", "/")
            return {"audioPath": rel, "sampleRate": sample_rate}
        except AdapterError:
            raise
        except BaseException:
            raise AdapterError("托管模型推理失败，请重试或修复运行时。") from None

    def unload(self) -> None:
        self._runtime = None


class Qwen3TtsAdapter(BaseAdapter):
    """Qwen3-TTS-12Hz-1.7B-Base: voice-clone TTS (reference audio + text)."""

    model_id = "qwen3-tts-1.7b"

    def __init__(self, model_root: str) -> None:
        super().__init__(model_root)
        self._model = None

    @property
    def loaded(self) -> bool:
        return self._model is not None

    def load(self) -> None:
        if self.loaded:
            return
        _require_cuda()
        try:
            import torch
            from qwen_tts import Qwen3TTSModel

            self._model = Qwen3TTSModel.from_pretrained(
                self._model_root,
                device_map="cuda:0",
                dtype=torch.bfloat16,
            )
        except AdapterError:
            raise
        except BaseException:
            raise AdapterError("托管模型加载失败，请修复模型文件后重试。") from None

    def infer(self, parameters: dict[str, Any]) -> dict[str, Any]:
        if not self.loaded:
            raise AdapterError("托管模型尚未加载。")
        text = parameters.get("text")
        if not isinstance(text, str) or not text.strip():
            raise AdapterError("待合成文本不能为空。")
        language = parameters.get("language") or "Auto"
        reference_audio = parameters.get("referenceAudioPath")
        reference_text = parameters.get("referenceText")
        if not isinstance(reference_audio, str) or not os.path.isfile(
            os.path.abspath(reference_audio)
        ):
            raise AdapterError("声音克隆需要有效的参考音频。")
        if not isinstance(reference_text, str) or not reference_text.strip():
            raise AdapterError("声音克隆需要参考音频的准确文本。")
        try:
            wavs, sample_rate = self._model.generate_voice_clone(
                text=text,
                language=language,
                reference_audio=os.path.abspath(reference_audio),
                reference_text=reference_text,
            )
            wav_path = self._new_output_path(".wav")
            import soundfile as sf

            sf.write(wav_path, wavs[0], sample_rate)
            rel = os.path.relpath(wav_path, self._model_root).replace("\\", "/")
            return {"audioPath": rel, "sampleRate": int(sample_rate)}
        except AdapterError:
            raise
        except BaseException:
            raise AdapterError("托管模型推理失败，请重试或修复运行时。") from None

    def unload(self) -> None:
        self._model = None


class BlockedAdapter(BaseAdapter):
    """CosyVoice2: binary-only supply chain cannot install its dependencies."""

    model_id = "cosyvoice2-0.5b"

    def load(self) -> None:
        raise AdapterError(
            "CosyVoice2 的依赖暂无法以二进制锁定方式安装，请等待上游发布 wheel。"
        )

    def infer(self, parameters: dict[str, Any]) -> dict[str, Any]:
        del parameters
        raise AdapterError("CosyVoice2 推理暂不可用。")

    def unload(self) -> None:
        pass


def create_adapter(model_id: str, model_root: str) -> BaseAdapter:
    if model_id == "moss-transcribe-diarize":
        return MossAdapter(model_root)
    if model_id == "dots-tts":
        return DotsTtsAdapter(model_root)
    if model_id == "qwen3-tts-1.7b":
        return Qwen3TtsAdapter(model_root)
    if model_id == "cosyvoice2-0.5b":
        return BlockedAdapter(model_root)
    raise AdapterError("不支持的托管 WSL 模型。")
