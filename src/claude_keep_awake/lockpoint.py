"""Where Windows would have locked an idle session, from its own settings.

Keeping the laptop awake must not keep it unlocked. Without the power request, an idle laptop locks when
one of these happens first:

- **it sleeps, and waking needs a sign-in**: at the sleep timeout for the current power source, plus the
  "Require sign-in" grace (0 for "every time"; never, if sign-in on wake is off; no sleep, if sleep is
  "never");
- **a secure screensaver starts**: active, "on resume, display logon screen", a screensaver chosen, and a
  timeout (the screensaver still runs while the request is held, so this lock happens anyway);
- **the machine inactivity limit** (a security policy) is reached.

The watcher locks at the earliest of these, while it's holding the request. If none applies, Windows would
never have locked, and neither does the watcher. Nothing here changes a setting.
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass
class LockPoint:
    seconds: float | None          # None: Windows wouldn't lock, so neither do we
    why: str                       # which setting gave it
    considered: list               # each candidate, as text, for `status`


def minutes(seconds: float | None) -> str:
    if seconds is None:
        return "never"
    if seconds % 60:
        return f"{seconds:.0f} s"
    return f"{seconds / 60:.0f} min"


def windows(settings) -> LockPoint:
    """The lock point from win_background.power.LockSettings (or anything with the same fields)."""
    candidates, considered = [], []

    sleep, guessed = settings.sleep_after, ""
    if sleep is None:   # unreadable: assume it sleeps when the display turns off, or at 5 minutes
        sleep = settings.display_off_after if settings.display_off_after else 300
        guessed = " (sleep timeout unreadable; assumed)"
    grace = settings.sign_in_after_sleep
    if not sleep:
        considered.append("sleep: never, so no lock from sleeping")
    elif grace is None:
        considered.append(f"sleep after {minutes(sleep)}{guessed}, but sign-in on wake is off, so no lock")
    else:
        when = "every time" if grace == 0 else f"after {minutes(grace)} asleep"
        text = f"sleep after {minutes(sleep)}{guessed}, sign-in required {when}"
        candidates.append((sleep + grace, text))
        considered.append(f"{text}: {minutes(sleep + grace)}")

    saver = settings.screensaver
    if saver is None:
        considered.append("screensaver: unreadable")
    elif saver.locks:
        text = f"secure screensaver after {minutes(saver.timeout)}{' (policy)' if saver.policy else ''}"
        candidates.append((saver.timeout, text))
        considered.append(text)
    else:
        missing = [name for name, ok in (("active", saver.active), ("secure", saver.secure),
                                         ("chosen", bool(saver.program)), ("timed", saver.timeout > 0)) if not ok]
        considered.append(f"screensaver: doesn't lock (not {', '.join(missing)})")

    if settings.inactivity_limit:
        text = f"machine inactivity limit {minutes(settings.inactivity_limit)}"
        candidates.append((settings.inactivity_limit, text))
        considered.append(text)

    if not candidates:
        return LockPoint(None, "Windows wouldn't lock this session when idle", considered)
    seconds, why = min(candidates, key=lambda c: c[0])
    return LockPoint(seconds, why, considered)


def effective(config_lock, read_settings) -> LockPoint:
    """The lock point the watcher uses: the setting "windows" (read_settings() is called), "never", or minutes."""
    if isinstance(config_lock, str):
        if config_lock.strip().lower() == "never":
            return LockPoint(None, 'lock = "never" in the settings', [])
        return windows(read_settings())
    return LockPoint(float(config_lock) * 60, f"lock = {config_lock} minutes in the settings", [])
