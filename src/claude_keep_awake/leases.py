"""Reading the leases the hook writes: one JSON file per Claude Code session."""

from __future__ import annotations

import json
import pathlib
import sys
import time
from dataclasses import dataclass

STALE_AFTER = 24 * 3600   # an expired lease file is deleted after a day; it's kept that long for `status`


@dataclass
class Lease:
    session: str
    until: float
    bg: bool
    pid: int | None
    cwd: str | None
    event: str | None
    path: pathlib.Path


def read(folder: pathlib.Path) -> list[Lease]:
    found = []
    for path in sorted(pathlib.Path(folder).glob("*.json")):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            found.append(Lease(path.stem, float(data.get("until", 0)), bool(data.get("bg")),
                               int(data["pid"]) if data.get("pid") else None, data.get("cwd"), data.get("event"), path))
        except (OSError, ValueError, TypeError, AttributeError):
            continue
    return found


def live(folder: pathlib.Path, now: float | None = None, check_pid: bool = True, alive=None) -> list[Lease]:
    """Leases that haven't expired, and (with check_pid) whose Claude Code process is still running."""
    now = time.time() if now is None else now
    alive = alive or pid_alive
    return [lease for lease in read(folder)
            if lease.until > now and (not check_pid or lease.pid is None or alive(lease.pid))]


def tidy(folder: pathlib.Path, now: float | None = None) -> int:
    """Delete lease files that expired over a day ago, and leftover temporary files. Returns how many."""
    now = time.time() if now is None else now
    gone = 0
    for lease in read(folder):
        if lease.until < now - STALE_AFTER:
            lease.path.unlink(missing_ok=True)
            gone += 1
    for temp in pathlib.Path(folder).glob("*.tmp"):
        try:
            if temp.stat().st_mtime < now - 3600:
                temp.unlink()
                gone += 1
        except OSError:
            pass
    return gone


def clear(folder: pathlib.Path) -> int:
    gone = 0
    for path in pathlib.Path(folder).glob("*.json"):
        path.unlink(missing_ok=True)
        gone += 1
    return gone


def pid_alive(pid: int) -> bool:
    """Whether a process is running. When in doubt (access denied, off Windows), True: a lease then simply
    runs to its expiry, which is the safe way to be wrong."""
    if sys.platform != "win32":
        return True
    import ctypes
    from ctypes import wintypes as wt
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.OpenProcess.restype = wt.HANDLE
    k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
    handle = k32.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return ctypes.get_last_error() != 87          # ERROR_INVALID_PARAMETER: no such process
    try:
        code = wt.DWORD()
        if not k32.GetExitCodeProcess(handle, ctypes.byref(code)):
            return True
        return code.value == 259                      # STILL_ACTIVE
    finally:
        k32.CloseHandle(handle)
