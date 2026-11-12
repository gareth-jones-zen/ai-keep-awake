"""The settings file. Every setting has a default, so no file is needed.

    reason = "Claude Code is working"      # what `powercfg /requests` shows while the laptop is kept awake
    lease_minutes = 15                      # how long one prompt or tool call keeps it awake
    background_lease_minutes = 45           # after a turn ends with background work still running
    unattended_hours = 3                    # stop keeping it awake after this long with no keyboard or mouse
    mains_only = true                       # only on mains power (Windows ends the request on battery anyway)
    lock = "windows"                        # "windows": lock where Windows would have locked (see the README);
                                            #   "never"; or a number of minutes
    check_seconds = 15                      # how often the watcher looks
    check_pid = true                        # ignore a lease whose Claude Code process has gone
    leases_dir = "%LOCALAPPDATA%/claude-keep-awake/leases"
    log_file = "%LOCALAPPDATA%/claude-keep-awake/claude-keep-awake.log"
    state_file = "%LOCALAPPDATA%/claude-keep-awake/state.json"
    claude_settings = "~/.claude/settings.json"   # where `install` puts the hooks

Paths may use ~ and %VARIABLES%; relative paths are relative to the settings file. After changing the lease
lengths or leases_dir, run `claude-keep-awake install` again: the hooks carry them.
"""

from __future__ import annotations

import os
import pathlib
import tomllib
from dataclasses import dataclass

APPDATA = pathlib.Path(os.environ.get("APPDATA", pathlib.Path.home()))
LOCAL = pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "claude-keep-awake"
DEFAULT_CONFIG = APPDATA / "claude-keep-awake" / "config.toml"

TEMPLATE = """\
# claude-keep-awake settings. Every line is optional; these are the defaults. See the README.
# reason = "Claude Code is working"
# lease_minutes = 15
# background_lease_minutes = 45
# unattended_hours = 3
# mains_only = true
# lock = "windows"            # or "never", or a number of minutes
# check_seconds = 15
# check_pid = true
# leases_dir = "%LOCALAPPDATA%/claude-keep-awake/leases"
# log_file = "%LOCALAPPDATA%/claude-keep-awake/claude-keep-awake.log"
# state_file = "%LOCALAPPDATA%/claude-keep-awake/state.json"
# claude_settings = "~/.claude/settings.json"
"""


@dataclass
class Config:
    path: pathlib.Path
    reason: str = "Claude Code is working"
    lease_minutes: float = 15
    background_lease_minutes: float = 45
    unattended_hours: float = 3
    mains_only: bool = True
    lock: str | float = "windows"
    check_seconds: float = 15
    check_pid: bool = True
    leases_dir: pathlib.Path = LOCAL / "leases"
    log_file: pathlib.Path = LOCAL / "claude-keep-awake.log"
    state_file: pathlib.Path = LOCAL / "state.json"
    claude_settings: pathlib.Path = pathlib.Path.home() / ".claude" / "settings.json"

    @property
    def lease_seconds(self) -> int:
        return int(self.lease_minutes * 60)

    @property
    def background_lease_seconds(self) -> int:
        return int(self.background_lease_minutes * 60)

    @property
    def unattended_seconds(self) -> float:
        return self.unattended_hours * 3600


def _path(value: str, base: pathlib.Path) -> pathlib.Path:
    path = pathlib.Path(os.path.expandvars(os.path.expanduser(str(value))))
    return path if path.is_absolute() else (base / path).resolve()


def load(path: pathlib.Path | str | None = None) -> Config:
    """The settings, from the file if there is one, else the defaults."""
    path = pathlib.Path(path or os.environ.get("CLAUDE_KEEP_AWAKE_CONFIG") or DEFAULT_CONFIG).resolve()
    data = tomllib.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    config = Config(path)
    known = set(Config.__dataclass_fields__) - {"path"}
    unknown = set(data) - known
    if unknown:
        raise ValueError(f"{path}: unknown setting(s) {', '.join(sorted(unknown))}.")
    for name, value in data.items():
        if name in ("leases_dir", "log_file", "state_file", "claude_settings"):
            value = _path(value, path.parent)
        setattr(config, name, value)
    lock = config.lock
    if isinstance(lock, str) and lock.strip().lower() not in ("windows", "never"):
        raise ValueError(f'{path}: lock must be "windows", "never" or a number of minutes, not {lock!r}.')
    if isinstance(lock, bool) or (not isinstance(lock, str) and not (isinstance(lock, (int, float)) and lock > 0)):
        raise ValueError(f'{path}: lock must be "windows", "never" or a number of minutes above 0.')
    for name in ("lease_minutes", "background_lease_minutes", "unattended_hours", "check_seconds"):
        if not isinstance(getattr(config, name), (int, float)) or getattr(config, name) <= 0:
            raise ValueError(f"{path}: {name} must be a number above 0.")
    return config


def create_default() -> pathlib.Path:
    if not DEFAULT_CONFIG.exists():
        DEFAULT_CONFIG.parent.mkdir(parents=True, exist_ok=True)
        DEFAULT_CONFIG.write_text(TEMPLATE, encoding="utf-8")
    return DEFAULT_CONFIG
