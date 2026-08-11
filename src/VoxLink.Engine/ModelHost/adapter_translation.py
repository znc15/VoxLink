#!/usr/bin/env python3
"""VoxLink managed translation adapter (T4).

Real transformers-based inference for the three managed translation models:

- ``m2m100-418m``  -> M2M100ForConditionalGeneration + M2M100Tokenizer
- ``small-100``    -> M2M100ForConditionalGeneration + Small100Tokenizer
- ``hy-mt1.5-1.8b`` -> HunYuanDenseV1ForCausalLM + AutoTokenizer + chat template

Model weights are verified and leased by the model manager before the host is
launched; this adapter never downloads anything. All failures surface fixed
messages without paths, stack traces, or model output.
"""

from __future__ import annotations

import os
import sys


# HY-MT official language table (README): code -> Chinese name / English name.
_HY_ZH_NAMES: dict[str, str] = {
    "zh": "中文",
    "en": "英语",
    "fr": "法语",
    "pt": "葡萄牙语",
    "es": "西班牙语",
    "ja": "日语",
    "tr": "土耳其语",
    "ru": "俄语",
    "ar": "阿拉伯语",
    "ko": "韩语",
    "th": "泰语",
    "it": "意大利语",
    "de": "德语",
    "vi": "越南语",
    "ms": "马来语",
    "id": "印尼语",
    "tl": "菲律宾语",
    "hi": "印地语",
    "zh-Hant": "繁体中文",
    "pl": "波兰语",
    "cs": "捷克语",
    "nl": "荷兰语",
    "km": "高棉语",
    "my": "缅甸语",
    "fa": "波斯语",
    "gu": "古吉拉特语",
    "ur": "乌尔都语",
    "te": "泰卢固语",
    "mr": "马拉地语",
    "he": "希伯来语",
    "bn": "孟加拉语",
    "ta": "泰米尔语",
    "uk": "乌克兰语",
    "bo": "藏语",
    "kk": "哈萨克语",
    "mn": "蒙古语",
    "ug": "维吾尔语",
    "yue": "粤语",
}
_HY_EN_NAMES: dict[str, str] = {
    "zh": "Chinese",
    "en": "English",
    "fr": "French",
    "pt": "Portuguese",
    "es": "Spanish",
    "ja": "Japanese",
    "tr": "Turkish",
    "ru": "Russian",
    "ar": "Arabic",
    "ko": "Korean",
    "th": "Thai",
    "it": "Italian",
    "de": "German",
    "vi": "Vietnamese",
    "ms": "Malay",
    "id": "Indonesian",
    "tl": "Filipino",
    "hi": "Hindi",
    "zh-Hant": "Traditional Chinese",
    "pl": "Polish",
    "cs": "Czech",
    "nl": "Dutch",
    "km": "Khmer",
    "my": "Burmese",
    "fa": "Persian",
    "gu": "Gujarati",
    "ur": "Urdu",
    "te": "Telugu",
    "mr": "Marathi",
    "he": "Hebrew",
    "bn": "Bengali",
    "ta": "Tamil",
    "uk": "Ukrainian",
    "bo": "Tibetan",
    "kk": "Kazakh",
    "mn": "Mongolian",
    "ug": "Uyghur",
    "yue": "Cantonese",
}

# M2M-100 / SMaLL-100 language codes accepted by the tokenizers.
_M2M_LANGUAGES: frozenset[str] = frozenset(
    {
        "af",
        "am",
        "ar",
        "ast",
        "az",
        "ba",
        "be",
        "bg",
        "bn",
        "br",
        "bs",
        "ca",
        "ceb",
        "cs",
        "cy",
        "da",
        "de",
        "el",
        "en",
        "es",
        "et",
        "fa",
        "ff",
        "fi",
        "fr",
        "fy",
        "ga",
        "gd",
        "gl",
        "gu",
        "ha",
        "he",
        "hi",
        "hr",
        "ht",
        "hu",
        "hy",
        "id",
        "ig",
        "ilo",
        "is",
        "it",
        "ja",
        "jv",
        "ka",
        "kk",
        "km",
        "kn",
        "ko",
        "lb",
        "lg",
        "ln",
        "lo",
        "lt",
        "lv",
        "mg",
        "mk",
        "ml",
        "mn",
        "mr",
        "ms",
        "my",
        "ne",
        "nl",
        "no",
        "ns",
        "oc",
        "or",
        "pa",
        "pl",
        "ps",
        "pt",
        "ro",
        "ru",
        "sd",
        "si",
        "sk",
        "sl",
        "so",
        "sq",
        "sr",
        "ss",
        "su",
        "sv",
        "sw",
        "ta",
        "th",
        "tl",
        "tn",
        "tr",
        "uk",
        "ur",
        "uz",
        "vi",
        "wo",
        "xh",
        "yi",
        "yo",
        "zh",
        "zu",
    }
)


class AdapterError(Exception):
    """Fixed safe adapter failure (no paths/output in the message)."""


class BaseAdapter:
    """Lazy-loading transformers adapter shared by the three models."""

    model_id: str = ""
    max_new_tokens_default = 512

    def __init__(self, model_root: str) -> None:
        if not model_root or not os.path.isdir(model_root):
            raise AdapterError("托管模型目录不可用。")
        self._model_root = os.path.abspath(model_root)
        self._model = None
        self._tokenizer = None
        sys.path.insert(0, self._model_root)

    @property
    def loaded(self) -> bool:
        return self._model is not None

    def load(self) -> None:
        if self.loaded:
            return
        try:
            self._load_impl()
        except AdapterError:
            raise
        except BaseException:
            raise AdapterError("托管模型加载失败，请修复模型文件后重试。") from None
        if self._model is None or self._tokenizer is None:
            raise AdapterError("托管模型加载失败，请修复模型文件后重试。")

    def infer(
        self,
        text: str,
        source_lang: str,
        target_lang: str,
        max_new_tokens: int | None = None,
    ) -> str:
        if not text or not text.strip():
            raise AdapterError("待翻译文本不能为空。")
        if not self.loaded:
            raise AdapterError("托管模型尚未加载。")
        try:
            return self._infer_impl(text, source_lang, target_lang, max_new_tokens)
        except AdapterError:
            raise
        except BaseException:
            raise AdapterError("托管模型推理失败，请重试或修复运行时。") from None

    def unload(self) -> None:
        self._model = None
        self._tokenizer = None

    def cancel(self) -> bool:
        # transformers generation is atomic from the host's perspective: the
        # host serializes requests, so a cancel discards the in-flight result.
        return True

    # -- subclasses -------------------------------------------------------

    def _load_impl(self) -> None:
        raise NotImplementedError

    def _infer_impl(
        self,
        text: str,
        source_lang: str,
        target_lang: str,
        max_new_tokens: int | None,
    ) -> str:
        raise NotImplementedError

    @staticmethod
    def _require_lang(code: str, allowed: frozenset[str], label: str) -> str:
        if not code or code not in allowed:
            raise AdapterError(f"不支持的{label}语言代码。")
        return code


class M2MAdapter(BaseAdapter):
    """M2M-100 418M (and SMaLL-100 distilled variant)."""

    def __init__(self, model_root: str, model_id: str) -> None:
        super().__init__(model_root)
        self.model_id = model_id

    def _load_impl(self) -> None:
        import torch  # noqa: F401  (ensures torch is importable)
        from transformers import (
            AutoTokenizer,
            M2M100ForConditionalGeneration,
            M2M100Tokenizer,
        )

        if self.model_id == "m2m100-418m":
            tokenizer_cls = M2M100Tokenizer
        else:
            tokenizer_cls = AutoTokenizer  # Small100Tokenizer via tokenizer_class
        self._tokenizer = tokenizer_cls.from_pretrained(
            self._model_root, src_lang="en", tgt_lang="en"
        )
        self._model = M2M100ForConditionalGeneration.from_pretrained(self._model_root)
        self._model.eval()

    def _infer_impl(
        self,
        text: str,
        source_lang: str,
        target_lang: str,
        max_new_tokens: int | None,
    ) -> str:
        src = self._require_lang(source_lang, _M2M_LANGUAGES, "源")
        tgt = self._require_lang(target_lang, _M2M_LANGUAGES, "目标")
        import torch

        tokenizer_cls = type(self._tokenizer)
        # Reconfigure the tokenizer for this direction; M2M tokenizers are
        # direction-aware, so a fresh tokenizer per direction is safest.
        self._tokenizer = tokenizer_cls.from_pretrained(
            self._model_root, src_lang=src, tgt_lang=tgt
        )
        encoded = self._tokenizer(text, return_tensors="pt")
        with torch.inference_mode():
            generated = self._model.generate(
                **encoded,
                forced_bos_token_id=self._tokenizer.get_lang_id(tgt),
                max_new_tokens=max_new_tokens or self.max_new_tokens_default,
            )
        return self._tokenizer.decode(generated[0], skip_special_tokens=True).strip()


class HyMtAdapter(BaseAdapter):
    """Tencent HY-MT1.5-1.8B (HunYuanDenseV1ForCausalLM + chat template)."""

    model_id = "hy-mt1.5-1.8b"

    def _load_impl(self) -> None:
        import torch  # noqa: F401
        from transformers import AutoModelForCausalLM, AutoTokenizer

        self._tokenizer = AutoTokenizer.from_pretrained(self._model_root)
        self._model = AutoModelForCausalLM.from_pretrained(self._model_root)
        self._model.eval()

    def _infer_impl(
        self,
        text: str,
        source_lang: str,
        target_lang: str,
        max_new_tokens: int | None,
    ) -> str:
        src = self._require_lang(source_lang, frozenset(_HY_ZH_NAMES), "源")
        tgt = self._require_lang(target_lang, frozenset(_HY_ZH_NAMES), "目标")
        import torch

        if src == "zh" or tgt == "zh":
            target_name = _HY_ZH_NAMES[tgt]
            prompt = (
                f"将以下文本翻译为{target_name}，注意只需要输出翻译后的结果，"
                "不要额外解释：\n\n"
                f"{text}"
            )
        else:
            target_name = _HY_EN_NAMES[tgt]
            prompt = (
                f"Translate the following segment into {target_name}, "
                f"without additional explanation.\n\n{text}"
            )
        messages = [{"role": "user", "content": prompt}]
        tokenized = self._tokenizer.apply_chat_template(
            messages,
            tokenize=True,
            add_generation_prompt=False,
            return_tensors="pt",
        )
        with torch.inference_mode():
            outputs = self._model.generate(
                tokenized.to(self._model.device),
                max_new_tokens=max_new_tokens or self.max_new_tokens_default,
                top_k=20,
                top_p=0.6,
                repetition_penalty=1.05,
                temperature=0.7,
                do_sample=True,
            )
        return self._tokenizer.decode(
            outputs[0][tokenized.shape[1] :], skip_special_tokens=True
        ).strip()


def create_adapter(model_id: str, model_root: str) -> BaseAdapter:
    if model_id in ("m2m100-418m", "small-100"):
        return M2MAdapter(model_root, model_id)
    if model_id == "hy-mt1.5-1.8b":
        return HyMtAdapter(model_root)
    raise AdapterError("不支持的托管翻译模型。")
