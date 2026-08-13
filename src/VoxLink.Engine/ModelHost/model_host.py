#!/usr/bin/env python3
"""VoxLink managed local-model JSON Lines host.

Runs the real translation adapters (T4) inside the app-managed Python runtime.
The runtime profile and model root are required; the adapter is selected by
model id and loaded on demand. All failures surface fixed messages without
paths, stack traces, or model output.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import adapter_translation
import adapter_wsl

PROTOCOL_VERSION = 1
MAX_REQUEST_BYTES = 1024 * 1024

INFERENCE_OPERATIONS = [
    "ping",
    "getCapabilities",
    "shutdown",
    "load",
    "infer",
    "unload",
    "cancel",
]


def _write(message: dict[str, Any]) -> None:
    payload = json.dumps(message, ensure_ascii=False, separators=(",", ":"))
    sys.stdout.write(payload + "\n")
    sys.stdout.flush()


def _error(request_id: int | None, code: str, message: str) -> None:
    _write({"id": request_id, "error": {"code": code, "message": message}})


def _parse_request(raw_line: bytes) -> tuple[int, str, dict[str, Any]]:
    if len(raw_line) > MAX_REQUEST_BYTES:
        raise ValueError("request_too_large")
    document = json.loads(raw_line.decode("utf-8", errors="strict"))
    if not isinstance(document, dict):
        raise ValueError("invalid_request")
    request_id = document.get("id")
    method = document.get("method")
    parameters = document.get("params", {})
    if not isinstance(request_id, int) or isinstance(request_id, bool) or request_id < 1:
        raise ValueError("invalid_request")
    if not isinstance(method, str) or not method or len(method) > 80:
        raise ValueError("invalid_request")
    if not isinstance(parameters, dict):
        raise ValueError("invalid_request")
    return request_id, method, parameters

def _handle(
    runtime_profile_id: str,
    request_id: int,
    method: str,
    parameters: dict[str, Any],
) -> bool:
    if method == "ping":
        _write(
            {
                "id": request_id,
                "result": {
                    "ready": True,
                    "protocolVersion": PROTOCOL_VERSION,
                    "runtimeProfileId": runtime_profile_id,
                },
            }
        )
        return True
    if method == "getCapabilities":
        _write(
            {
                "id": request_id,
                "result": {
                    "protocolVersion": PROTOCOL_VERSION,
                    "operations": INFERENCE_OPERATIONS,
                    "inferenceAvailable": True,
                },
            }
        )
        return True
    if method == "shutdown":
        _write({"id": request_id, "result": {"ok": True}})
        return False
    if method == "load":
        return _handle_load(request_id, parameters)
    if method == "infer":
        return _handle_infer(request_id, parameters)
    if method == "unload":
        return _handle_unload(request_id, parameters)
    if method == "cancel":
        return _handle_cancel(request_id)
    _error(request_id, "method_not_found", "未知的托管模型宿主方法。")
    return True


_adapter: Any | None = None
_adapter_model_id: str | None = None


def _create_adapter(model_id: str, model_root: str) -> Any:
    if model_id in (
        "moss-transcribe-diarize",
        "dots-tts",
        "qwen3-tts-1.7b",
        "cosyvoice2-0.5b",
    ):
        return adapter_wsl.create_adapter(model_id, model_root)
    return adapter_translation.create_adapter(model_id, model_root)


def _is_wsl_adapter(adapter: Any) -> bool:
    return isinstance(adapter, adapter_wsl.BaseAdapter)


def _adapter_error(request_id: int, error: BaseException) -> None:
    for error_type in (adapter_translation.AdapterError, adapter_wsl.AdapterError):
        if isinstance(error, error_type):
            _error(request_id, "adapter_error", str(error))
            return
    _error(request_id, "host_failure", "托管模型宿主执行失败。")


def _handle_load(request_id: int, parameters: dict[str, Any]) -> bool:
    global _adapter, _adapter_model_id
    model_id = parameters.get("modelId")
    if not isinstance(model_id, str) or not model_id:
        _error(request_id, "invalid_params", "模型 ID 无效。")
        return True
    try:
        adapter = _create_adapter(model_id, _model_root)
        adapter.load()
        _adapter = adapter
        _adapter_model_id = model_id
        _write({"id": request_id, "result": {"loaded": True, "modelId": model_id}})
    except (adapter_translation.AdapterError, adapter_wsl.AdapterError) as error:
        _error(request_id, "adapter_error", str(error))
    except Exception:
        _error(request_id, "host_failure", "托管模型宿主执行失败。")
    return True


def _handle_infer(request_id: int, parameters: dict[str, Any]) -> bool:
    if _adapter is None:
        _error(request_id, "adapter_error", "托管模型尚未加载。")
        return True
    try:
        if _is_wsl_adapter(_adapter):
            result = _adapter.infer(parameters)
            _write({"id": request_id, "result": result})
        else:
            text = parameters.get("text")
            source_lang = parameters.get("sourceLang")
            target_lang = parameters.get("targetLang")
            max_tokens = parameters.get("maxNewTokens")
            if (
                not isinstance(text, str)
                or not isinstance(source_lang, str)
                or not isinstance(target_lang, str)
            ):
                _error(request_id, "invalid_params", "翻译参数无效。")
                return True
            if max_tokens is not None and not isinstance(max_tokens, int):
                max_tokens = None
            result = _adapter.infer(
                text,
                source_lang,
                target_lang,
                max_tokens,
            )
            _write({"id": request_id, "result": {"text": result}})
    except (adapter_translation.AdapterError, adapter_wsl.AdapterError) as error:
        _error(request_id, "adapter_error", str(error))
    except Exception:
        _error(request_id, "host_failure", "托管模型宿主执行失败。")
    return True

def _handle_unload(request_id: int, parameters: dict[str, Any]) -> bool:
    del parameters
    global _adapter, _adapter_model_id
    if _adapter is not None:
        _adapter.unload()
    _adapter = None
    _adapter_model_id = None
    _write({"id": request_id, "result": {"unloaded": True}})
    return True


def _handle_cancel(request_id: int) -> bool:
    cancelled = _adapter.cancel() if _adapter is not None else False
    _write({"id": request_id, "result": {"cancelled": cancelled}})
    return True

_model_root: str = ""


def main() -> int:
    # `python -I` 隐含 -E，会忽略 PYTHONUTF8 等环境变量，stdout 默认用 Windows
    # locale 编码（GBK）；协议要求 UTF-8，这里用运行时 API 强制，不受 -E 影响。
    if sys.stdout is not None:
        sys.stdout.reconfigure(encoding="utf-8")
    if sys.stderr is not None:
        sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--runtime-profile", required=True)
    parser.add_argument("--model-root", required=True)
    arguments = parser.parse_args()
    if (
        not arguments.runtime_profile
        or len(arguments.runtime_profile) > 80
        or any(not (character.isascii() and (character.isalnum() or character in "-_"))
               for character in arguments.runtime_profile)
    ):
        return 2
    global _model_root
    _model_root = os.path.abspath(arguments.model_root)
    if not os.path.isdir(_model_root):
        return 2

    while True:
        raw_line = sys.stdin.buffer.readline(MAX_REQUEST_BYTES + 2)
        if not raw_line:
            return 0
        if len(raw_line) > MAX_REQUEST_BYTES + 1 or not raw_line.endswith(b"\n"):
            _error(None, "request_too_large", "托管模型宿主请求过大。")
            return 2
        try:
            request_id, method, parameters = _parse_request(raw_line[:-1])
        except (UnicodeDecodeError, json.JSONDecodeError, ValueError):
            _error(None, "invalid_request", "托管模型宿主请求格式无效。")
            continue
        try:
            if not _handle(arguments.runtime_profile, request_id, method, parameters):
                return 0
        except Exception:
            _error(request_id, "host_failure", "托管模型宿主执行失败。")


if __name__ == "__main__":
    os.environ.setdefault("PYTHONUTF8", "1")
    raise SystemExit(main())
