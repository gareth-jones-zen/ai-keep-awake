"""The watcher at logon, through Windows Task Scheduler (win-background).

It runs `pythonw -m claude_keep_awake --config <settings> watch` directly, so there's no window, and stopping
the task stops the watcher itself (and Windows drops its power request with it). Restarted if it crashes;
never stopped for time.
"""

from __future__ import annotations

import pathlib

from win_background import Task, python_command

TASK = "claude-keep-awake watch"
_task = Task(TASK, "Keeps the laptop awake while Claude Code is working (claude-keep-awake watch).",
             results={"3": "another watcher was already running"})


def command(config_path: pathlib.Path) -> tuple[str, list]:
    return python_command("claude_keep_awake", "--config", str(pathlib.Path(config_path).resolve()), "watch")


def enable(config_path: pathlib.Path) -> str:
    return _task.enable(*command(config_path), working_dir=pathlib.Path(config_path).resolve().parent)


def disable() -> bool:
    return _task.disable()


def status() -> dict | None:
    return _task.status()
