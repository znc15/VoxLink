#!/usr/bin/env python3
"""Active readiness probe for app-managed Python runtimes."""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import os
import re
import sys
from pathlib import Path
from typing import Any

LOCK_LINE = re.compile(r"^([A-Za-z0-9][A-Za-z0-9._-]*)==([^\s;]+)(?:\s+.*)?$")
HASH_TOKEN = re.compile(r"(?:^|\s)--hash=sha256:([0-9a-fA-F]{64})(?=\s|$)")
SCHEMA_VERSION = 1


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(128 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _canonical_name(value: str) -> str:
    return re.sub(r"[-_.]+", "-", value).lower()


def _logical_lock_lines(text: str) -> list[str]:
    result: list[str] = []
    current = ""
    for raw_line in text.replace("\r", "").split("\n"):
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith(("--index-url", "--extra-index-url", "--find-links", "-r ",
                            "--requirement ", "-e ", "--editable ")):
            raise ValueError("unsafe_lock")
        continues = line.endswith("\\")
        fragment = line[:-1].rstrip() if continues else line
        current = f"{current} {fragment}".strip()
        if not continues:
            result.append(current)
            current = ""
    if current or not result:
        raise ValueError("invalid_lock")
    return result


def _requirements(lock_path: Path) -> dict[str, str]:
    requirements: dict[str, str] = {}
    for line in _logical_lock_lines(lock_path.read_text(encoding="utf-8")):
        match = LOCK_LINE.match(line)
        if match is None or HASH_TOKEN.search(line) is None or " @ " in line or "://" in line:
            raise ValueError("unlocked_requirement")
        name = _canonical_name(match.group(1))
        if name in requirements:
            raise ValueError("duplicate_requirement")
        requirements[name] = match.group(2)
    return requirements


def _installed_versions(requirements: dict[str, str]) -> dict[str, str]:
    versions: dict[str, str] = {}
    for name, expected in requirements.items():
        actual = importlib.metadata.version(name)
        if actual != expected:
            raise ValueError("package_version_mismatch")
        versions[name] = actual
    return versions


def _load_state(path: Path) -> dict[str, Any]:
    document = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(document, dict):
        raise ValueError("invalid_state")
    return document


def _write_state(path: Path, state: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(state, stream, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def _emit(ready: bool, status: str, python_version: str) -> None:
    json.dump(
        {"ready": ready, "status": status, "pythonVersion": python_version},
        sys.stdout,
        ensure_ascii=False,
        separators=(",", ":"),
    )
    sys.stdout.write("\n")
    sys.stdout.flush()


def main() -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--state", required=True)
    parser.add_argument("--lock", required=True)
    parser.add_argument("--host", required=True)
    parser.add_argument("--expected-python", required=True)
    parser.add_argument("--expected-lock-sha256", required=True)
    parser.add_argument("--expected-host-sha256", required=True)
    parser.add_argument("--write-state", action="store_true")
    arguments = parser.parse_args()
    python_version = f"{sys.version_info.major}.{sys.version_info.minor}"

    try:
        lock_path = Path(arguments.lock)
        host_path = Path(arguments.host)
        state_path = Path(arguments.state)
        if python_version != arguments.expected_python:
            raise ValueError("python_version_mismatch")
        lock_sha256 = _sha256(lock_path)
        host_sha256 = _sha256(host_path)
        if lock_sha256.lower() != arguments.expected_lock_sha256.lower():
            raise ValueError("lock_fingerprint_mismatch")
        if host_sha256.lower() != arguments.expected_host_sha256.lower():
            raise ValueError("host_fingerprint_mismatch")
        packages = _installed_versions(_requirements(lock_path))
        expected_state: dict[str, Any] = {
            "schemaVersion": SCHEMA_VERSION,
            "pythonVersion": python_version,
            "lockSha256": lock_sha256,
            "hostSha256": host_sha256,
            "packages": packages,
        }
        if arguments.write_state:
            _write_state(state_path, expected_state)
        elif _load_state(state_path) != expected_state:
            raise ValueError("state_mismatch")
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError, importlib.metadata.PackageNotFoundError):
        _emit(False, "托管运行时主动探测未通过。", python_version)
        return 0

    _emit(True, "托管运行时已通过主动探测。", python_version)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
