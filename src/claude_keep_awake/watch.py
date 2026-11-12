"""The watcher: holds one named power request while any Claude Code session has a live lease.

It holds the request ("Claude Code is working") only while all of these are true:
- a session has a live lease (the hook renews it on every prompt and tool call);
- the laptop is on mains, unless mains_only = false;
- someone has touched the keyboard or mouse within unattended_hours (3 hours by default).

While it holds the request, it locks the session where Windows would have locked it (lockpoint.py), unless
another program is keeping the display on (a call, a video, a presentation), because then Windows wouldn't
have locked either. It never changes a setting and never simulates input.
"""

from __future__ import annotations

import json
import os
import pathlib
import time
from typing import Callable

from . import leases, lockpoint
from .config import Config

SETTINGS_EVERY = 60     # re-read Windows' settings this often, so a change in Settings applies within a minute
ALIVE_EVERY = 3600
STATE_EVERY = 60
ES_DISPLAY_REQUIRED = 0x2


class System:
    """The real Windows calls (win_background.power). Tests pass a fake with the same methods."""

    def __init__(self):
        from win_background import power
        self.power = power

    def on_mains(self):
        return self.power.on_mains()

    def idle_seconds(self):
        return self.power.idle_seconds()

    def execution_state(self):
        return self.power.execution_state()

    def lock_settings(self):
        return self.power.lock_settings()

    def lock(self):
        return self.power.lock_workstation()

    def request(self, reason):
        return self.power.SystemRequired(reason)


def decide(config: Config, live: int, mains, idle: float) -> tuple[bool, str]:
    """Whether to hold the request, and why, in words for the log."""
    if not live:
        return False, "no Claude Code session is working"
    if config.mains_only and mains is False:
        return False, "on battery"
    if idle >= config.unattended_seconds:
        return False, f"nobody at the laptop for {idle / 3600:.1f} hours (the cap is {config.unattended_hours:g})"
    return True, f"{live} session{'s' if live != 1 else ''} working"


class Watcher:
    def __init__(self, config: Config, system, log: Callable[[str], None], notify: Callable[[str, list], None],
                 alive=None):
        self.config, self.system, self.log, self.notify = config, system, log, notify
        self.alive = alive
        self.request = system.request(config.reason)
        self.held = False
        self.why = ""
        self.since = None
        self.locked = False           # locked during this idle stretch
        self.display_skip = False     # logged that another program keeps the display on
        self.capped = False           # notified that the cap let it sleep
        self.point = None
        self.point_at = 0.0
        self.alive_at = 0.0
        self.state_at = 0.0
        self.live: list = []
        self.last = None

    def lock_point(self, now: float) -> lockpoint.LockPoint:
        if self.point is None or now - self.point_at >= SETTINGS_EVERY:
            point = lockpoint.effective(self.config.lock, self.system.lock_settings)
            if self.point is not None and (point.seconds, point.why) != (self.point.seconds, self.point.why):
                self.log(f"lock point now {lockpoint.minutes(point.seconds)}: {point.why}")
            self.point, self.point_at = point, now
        return self.point

    def step(self, now: float) -> None:
        self.live = leases.live(self.config.leases_dir, now, self.config.check_pid, self.alive)
        mains, idle = self.system.on_mains(), self.system.idle_seconds()
        want, why = decide(self.config, len(self.live), mains, idle)
        changed = want != self.held
        if changed:
            self.request.hold(want)
            self.held, self.since = want, now
            mains_text = {True: "mains", False: "battery", None: "power source unknown"}[mains]
            self.log(f"{'holding' if want else 'released'}: {why}; idle {idle / 60:.0f} min, {mains_text}")
        self.why = why

        # The cap: say so once, if sessions are still working when it lets the laptop sleep.
        if self.live and idle >= self.config.unattended_seconds:
            if not self.capped:
                self.capped = True
                self.notify("Claude Code may now let the laptop sleep",
                            [f"{len(self.live)} session(s) still working, but nobody has used the laptop for "
                             f"{self.config.unattended_hours:g} hours, so it's no longer kept awake."])
        elif idle < self.config.unattended_seconds:
            self.capped = False

        point = self.lock_point(now)
        if point.seconds is None or idle < point.seconds:
            self.locked = self.display_skip = False
        elif self.held and not self.locked:
            if self.system.execution_state() & ES_DISPLAY_REQUIRED:
                if not self.display_skip:
                    self.display_skip = True
                    self.log("not locking: another program is keeping the display on, so Windows wouldn't lock either")
            else:
                self.locked = True
                done = self.system.lock()
                self.log(f"locked: idle {idle / 60:.0f} min while Claude Code works ({point.why})"
                         + ("" if done else "; Windows refused"))

        if now - self.alive_at >= ALIVE_EVERY:
            self.alive_at = now
            self.log(f"alive: {'holding' if self.held else 'not holding'}, {len(self.live)} live lease(s)")
            leases.tidy(self.config.leases_dir, now)
        self.last = (mains, idle, point)
        if changed or now - self.state_at >= STATE_EVERY:
            self.state_at = now
            self.write_state(now, mains, idle, point)

    def write_state(self, now, mains, idle, point) -> None:
        state = {"held": self.held, "why": self.why, "since": self.since, "updated": now, "pid": os.getpid(),
                 "sessions": [lease.session for lease in self.live], "mains": mains, "idle": idle,
                 "lock_after": point.seconds, "lock_why": point.why}
        path = pathlib.Path(self.config.state_file)
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            temp = path.with_suffix(".tmp")
            temp.write_text(json.dumps(state), encoding="utf-8")
            os.replace(temp, path)
        except OSError:
            pass

    def close(self) -> None:
        try:
            self.request.close()
        finally:
            was_held, self.held, self.why = self.held, False, "the watcher stopped"
            if was_held:
                self.log("released: the watcher stopped")
            if self.last:
                self.write_state(time.time(), *self.last)


def run(config: Config, log, notify, system=None, sleep=time.sleep, steps: int | None = None) -> int:
    """The watcher loop. 3 if another watcher is already running."""
    from win_background import key, single_instance
    lock = single_instance(f"claude-keep-awake-watch-{key(config.leases_dir)}")
    if lock is None:
        log("another watcher is already running; not starting a second")
        return 3
    watcher = Watcher(config, system or System(), log, notify)
    point = watcher.lock_point(time.time())
    log(f"started: keeps the laptop awake while Claude Code works; lock point {lockpoint.minutes(point.seconds)} "
        f"({point.why})")
    failures = 0
    try:
        while steps is None or steps > 0:
            try:
                watcher.step(time.time())
                failures = 0
            except Exception as error:   # keep watching; say so if it keeps failing
                failures += 1
                log(f"error: {type(error).__name__}: {error}")
                if failures == 3:
                    notify("claude-keep-awake is failing", [f"{type(error).__name__}: {error}"[:300],
                                                             f"Log: {config.log_file}"])
            if steps is not None:
                steps -= 1
                if not steps:
                    break
            sleep(config.check_seconds)
    finally:
        watcher.close()
        lock.release()
    return 0
