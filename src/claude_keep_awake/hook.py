"""The Claude Code hook: writes or clears this session's lease. Claude Code runs it on every prompt and tool call.

    python -I -S hook.py busy|stop|idle|end [--dir FOLDER] [--lease SECONDS] [--bg-lease SECONDS]

It reads the hook's JSON from stdin and keeps one small file per session, {"until": epoch, "bg": bool, ...},
in FOLDER (default %LOCALAPPDATA%\\claude-keep-awake\\leases). The watcher keeps the laptop awake while any
file's "until" is in the future.

    busy   UserPromptSubmit, PreToolUse, SubagentStart: a lease of --lease seconds (15 minutes). Skipped if
           renewed under a minute ago, so a burst of tool calls costs one write.
    stop   Stop: if background_tasks has anything still running, a background lease of --bg-lease seconds
           (45 minutes); otherwise the turn is over, so the lease goes.
    idle   Notification idle_prompt and StopFailure: the lease goes, unless it's a background one (idle_prompt
           fires while a background shell or subagent is still working).
    end    SessionEnd: the lease goes.

Rules for a hook, all kept here: it always exits 0, prints nothing (Claude Code reads a hook's stdout as
context or a decision), never waits on anything, and imports nothing but the standard library's json, os
and sys (run with -I -S, it starts in about the time Python itself takes). It's run as a file, never as part
of the package, so it must not import the package. CLAUDE_KEEP_AWAKE=0 turns it off.
"""

import json
import os
import sys
import time

LEASE, BG_LEASE = 15 * 60, 45 * 60
RENEW_AFTER = 60
DONE = {"completed", "complete", "done", "finished", "failed", "error", "killed", "stopped", "cancelled",
        "canceled", "exited", "succeeded"}


def default_dir(env=os.environ):
    base = env.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), ".local", "state")
    return os.path.join(base, "claude-keep-awake", "leases")


def parse(argv):
    """(action, folder, lease, bg_lease) from the arguments, with the defaults for anything missing."""
    action, folder, lease, bg_lease = (argv[0] if argv else ""), None, LEASE, BG_LEASE
    i = 1
    while i + 1 < len(argv):
        name, value = argv[i], argv[i + 1]
        if name == "--dir":
            folder = value
        elif name == "--lease":
            lease = int(value)
        elif name == "--bg-lease":
            bg_lease = int(value)
        i += 2
    return action, folder, lease, bg_lease


def in_flight(tasks):
    """Whether Stop's background_tasks still has anything working. The docs show only "running", so anything
    not plainly finished counts."""
    if not isinstance(tasks, list):
        return False
    return any(not isinstance(t, dict) or str(t.get("status", "running")).lower() not in DONE for t in tasks)


def safe_name(session):
    name = "".join(c for c in str(session) if c.isalnum() or c in "-_")[:100]
    return name or "unknown"


def handle(argv, data, env=os.environ, now=None, ppid=None):
    """Apply one hook event. Returns what it did ("wrote", "kept", "cleared", "skipped", "off"), for tests."""
    if env.get("CLAUDE_KEEP_AWAKE", "").strip().lower() in ("0", "off", "false", "no"):
        return "off"
    action, folder, lease, bg_lease = parse(argv)
    now = time.time() if now is None else now
    folder = folder or default_dir(env)
    path = os.path.join(folder, safe_name(data.get("session_id", "unknown")) + ".json")
    try:
        with open(path, encoding="utf-8") as handle_:
            old = json.load(handle_)
        if not isinstance(old, dict):
            old = {}
    except (OSError, ValueError):
        old = {}
    old_until = float(old.get("until", 0) or 0)
    old_bg = bool(old.get("bg")) and old_until > now

    if action == "busy":
        # Activity. A background lease stays a background lease (a background subagent's own tool calls land
        # here too), and never gets shorter.
        if old_until - now > lease - RENEW_AFTER:
            return "skipped"   # renewed under a minute ago, or a longer background lease is running
        lease_ = {"until": now + lease, "bg": old_bg}
    elif action == "stop" and in_flight(data.get("background_tasks")):
        lease_ = {"until": max(now + bg_lease, old_until if old_bg else 0), "bg": True}
    elif action == "idle" and old_bg:
        return "kept"
    elif action in ("stop", "idle", "end"):
        try:
            os.remove(path)
            return "cleared"
        except OSError:
            return "skipped"
    else:
        return "skipped"

    lease_.update(pid=ppid if ppid is not None else os.getppid(), cwd=data.get("cwd"),
                  event=data.get("hook_event_name"), at=now)
    os.makedirs(folder, exist_ok=True)
    temp = f"{path}.{os.getpid()}.tmp"
    with open(temp, "w", encoding="utf-8") as out:
        json.dump(lease_, out)
    os.replace(temp, path)
    return "wrote"


def main():
    try:
        raw = sys.stdin.buffer.read() if sys.stdin is not None else b""
        data = json.loads(raw.decode("utf-8", "replace") or "{}")
        if not isinstance(data, dict):
            data = {}
        handle(sys.argv[1:], data)
    except Exception:
        pass   # a hook must never fail the session it's watching
    return 0


if __name__ == "__main__":
    main()
    sys.exit(0)
