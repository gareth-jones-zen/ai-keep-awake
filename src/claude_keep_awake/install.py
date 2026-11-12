"""Putting the hooks into Claude Code's user settings, and taking them out again.

Only this tool's own hook entries are ever added or removed: each is a command hook whose arguments name
this package's hook.py, so they're found by that, and nothing else in the file is touched. Each gets a
matcher group of its own rather than joining someone else's. The file is backed up before every change,
written whole and replaced in one step, and left alone if it isn't valid JSON.

The hooks run hook.py directly (exec form, `args`, no shell), with pythonw.exe so no window can appear,
and with -I -S so Python starts as fast as it can: hook.py needs nothing but the standard library.
"""

from __future__ import annotations

import copy
import datetime as dt
import difflib
import json
import os
import pathlib
import sys

from . import hook as hook_module
from .config import Config

MARK = "claude_keep_awake"
TIMEOUT = 5   # seconds; the hook takes a few hundredths

# (event, matcher, action)
EVENTS = [
    ("UserPromptSubmit", None, "busy"),
    ("PreToolUse", None, "busy"),
    ("SubagentStart", None, "busy"),
    ("Stop", None, "stop"),
    ("StopFailure", None, "idle"),
    ("Notification", "idle_prompt", "idle"),
    ("SessionEnd", None, "end"),
]


class SettingsError(Exception):
    pass


def python_for_hooks() -> str:
    """pythonw.exe beside this Python, if there is one (no console window, even from a GUI parent)."""
    python = pathlib.Path(sys.executable)
    windowless = python.with_name("pythonw.exe")
    return str(windowless if windowless.exists() else python)


def hook_path() -> str:
    return str(pathlib.Path(hook_module.__file__).resolve())


def handler(config: Config, action: str, python: str | None = None, script: str | None = None) -> dict:
    args = ["-I", "-S", script or hook_path(), action]
    if pathlib.Path(config.leases_dir) != pathlib.Path(hook_module.default_dir()):
        args += ["--dir", str(config.leases_dir)]
    if config.lease_seconds != hook_module.LEASE:
        args += ["--lease", str(config.lease_seconds)]
    if config.background_lease_seconds != hook_module.BG_LEASE:
        args += ["--bg-lease", str(config.background_lease_seconds)]
    return {"type": "command", "command": python or python_for_hooks(), "args": args, "timeout": TIMEOUT}


def is_ours(item) -> bool:
    if not isinstance(item, dict):
        return False
    texts = [item.get("command", "")] + [a for a in item.get("args", []) or [] if isinstance(a, str)]
    return any(MARK in str(text) for text in texts)


def without_ours(settings: dict) -> tuple[dict, int]:
    """A copy with this tool's hook entries removed, and how many there were. Empty groups and events that
    only held ours go too; anything else stays exactly as it was."""
    result = copy.deepcopy(settings)
    hooks = result.get("hooks")
    if not isinstance(hooks, dict):
        return result, 0
    removed = 0
    for event in list(hooks):
        groups = hooks[event]
        if not isinstance(groups, list):
            continue
        kept_groups = []
        for group in groups:
            if isinstance(group, dict) and isinstance(group.get("hooks"), list):
                kept = [h for h in group["hooks"] if not is_ours(h)]
                taken = len(group["hooks"]) - len(kept)
                removed += taken
                if taken and not kept:
                    continue                     # the group was only ours
                if taken:
                    group = {**group, "hooks": kept}
            kept_groups.append(group)
        if kept_groups:
            hooks[event] = kept_groups
        elif groups:
            del hooks[event]
    if not hooks and removed:
        del result["hooks"]
    return result, removed


def with_ours(settings: dict, config: Config, python: str | None = None, script: str | None = None) -> dict:
    """A copy with this tool's hooks in place (any earlier copy of them replaced)."""
    result, _ = without_ours(settings)
    hooks = result.setdefault("hooks", {})
    if not isinstance(hooks, dict):
        raise SettingsError('"hooks" in the settings file isn\'t an object; not changing it.')
    for event, matcher, action in EVENTS:
        group = {"matcher": matcher} if matcher else {}
        group["hooks"] = [handler(config, action, python, script)]
        existing = hooks.setdefault(event, [])
        if not isinstance(existing, list):
            raise SettingsError(f'"hooks.{event}" in the settings file isn\'t a list; not changing it.')
        existing.append(group)
    return result


def read(path: pathlib.Path) -> tuple[dict, str]:
    """(settings, original text). A missing file is an empty one; a file that isn't a JSON object is refused."""
    if not path.exists():
        return {}, ""
    text = path.read_text(encoding="utf-8-sig")
    if not text.strip():
        return {}, text
    try:
        data = json.loads(text)
    except ValueError as error:
        raise SettingsError(f"{path} isn't valid JSON ({error}). Fix it first; nothing was changed.") from error
    if not isinstance(data, dict):
        raise SettingsError(f"{path} isn't a JSON object. Nothing was changed.")
    return data, text


def render(settings: dict) -> str:
    """As Claude Code writes it: 2-space indent, non-ASCII kept, a final newline."""
    return json.dumps(settings, indent=2, ensure_ascii=False) + "\n"


def diff(path: pathlib.Path, before: str, after: str) -> str:
    return "".join(difflib.unified_diff(before.splitlines(True), after.splitlines(True),
                                        f"{path} (now)", f"{path} (after)"))


def change(path: pathlib.Path, transform, dry_run: bool = False) -> dict:
    """Apply transform(settings) -> settings to the file. Returns {"changed", "diff", "backup"}."""
    settings, before = read(path)
    after_settings = transform(settings)
    if after_settings == settings and before:
        return {"changed": False, "diff": "", "backup": None}
    after = render(after_settings)
    report = {"changed": True, "diff": diff(path, before, after), "backup": None}
    if dry_run:
        return report
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        backup = path.with_name(f"{path.name}.claude-keep-awake-{dt.datetime.now():%Y%m%d-%H%M%S}.bak")
        backup.write_bytes(path.read_bytes())
        report["backup"] = backup
    temp = path.with_name(f"{path.name}.claude-keep-awake.tmp")
    temp.write_text(after, encoding="utf-8")
    os.replace(temp, path)
    return report


def installed(path: pathlib.Path) -> list[str]:
    """The events that have this tool's hook, from the settings file."""
    try:
        settings, _ = read(path)
    except (SettingsError, OSError):
        return []
    hooks = settings.get("hooks")
    if not isinstance(hooks, dict):
        return []
    return [event for event, groups in hooks.items() if isinstance(groups, list) and any(
        isinstance(g, dict) and any(is_ours(h) for h in g.get("hooks", []) or []) for g in groups)]
