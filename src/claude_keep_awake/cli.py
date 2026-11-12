"""claude-keep-awake: keep a Windows laptop awake while Claude Code is working, and let it sleep otherwise.

    claude-keep-awake install            put the hooks in ~/.claude/settings.json and start the watcher at logon
        --dry-run                            show the change to settings.json and the task, change nothing
        --settings PATH                      another Claude Code settings file
        --no-autostart                       only the hooks
    claude-keep-awake uninstall          take out exactly this tool's hooks, and remove the watcher's task
    claude-keep-awake status             hooks, watcher, leases, the power request, and the lock point from Windows
    claude-keep-awake config             create the settings file (if missing) and say where it is
    claude-keep-awake watch              the watcher itself (what autostart runs)
    claude-keep-awake autostart on|off|status
    claude-keep-awake hook busy|stop|idle|end    what the hooks run (reads the hook's JSON on stdin)
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import sys
import time

from . import __version__, autostart, config as cfg, hook, install, leases, lockpoint


def main(argv: list | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if stream and hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(prog="claude-keep-awake", description=__doc__.split("\n")[0])
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--config", help="settings file (default %%APPDATA%%\\claude-keep-awake\\config.toml)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("config", help="create the settings file if missing, and say where it is")
    for name, text in (("install", "add the hooks and start the watcher at logon"),
                       ("uninstall", "remove the hooks and the watcher's task")):
        p = sub.add_parser(name, help=text)
        p.add_argument("--settings", help="Claude Code settings file (default ~/.claude/settings.json)")
        p.add_argument("--dry-run", action="store_true", help="show what would change; change nothing")
        p.add_argument("--no-autostart", action="store_true", help="leave the watcher's task alone")
    sub.add_parser("status", help="what's installed, running and held, and the lock point")
    sub.add_parser("watch", help="the watcher (what autostart runs)")
    p = sub.add_parser("autostart", help="the watcher at logon (Task Scheduler)")
    p.add_argument("action", choices=["on", "off", "status"])
    p = sub.add_parser("hook", help="what the Claude Code hooks run")
    p.add_argument("action", choices=["busy", "stop", "idle", "end"])
    args = parser.parse_args(argv)

    if args.command == "hook":   # never fails, never prints
        try:
            data = json.loads(sys.stdin.buffer.read().decode("utf-8", "replace") or "{}")
            config = cfg.load(args.config)
            hook.handle([args.action, "--dir", str(config.leases_dir), "--lease", str(config.lease_seconds),
                         "--bg-lease", str(config.background_lease_seconds)], data if isinstance(data, dict) else {})
        except Exception:
            pass
        return 0
    if args.command == "config":
        print(args.config or cfg.create_default())
        return 0
    try:
        config = cfg.load(args.config)
    except (ValueError, OSError) as error:
        print(error, file=sys.stderr)
        return 2

    from win_background import logger, notify
    if args.command == "watch":
        from . import watch
        log = logger(config.log_file, echo=sys.stdout is not None)
        return watch.run(config, log, notify)
    if args.command == "install":
        return _install(args, config)
    if args.command == "uninstall":
        return _uninstall(args, config)
    if args.command == "autostart":
        return _autostart(args.action, config)
    return _status(config)


def _settings_path(args, config) -> pathlib.Path:
    return pathlib.Path(args.settings).expanduser().resolve() if args.settings else config.claude_settings


def _install(args, config) -> int:
    path = _settings_path(args, config)
    try:
        report = install.change(path, lambda s: install.with_ours(s, config), args.dry_run)
    except (install.SettingsError, OSError) as error:
        print(error, file=sys.stderr)
        return 2
    exe, task_args = autostart.command(config.path)
    if args.dry_run:
        print(report["diff"] or f"{path}: the hooks are already in place; no change.")
        if not args.no_autostart:
            print(f"\nWould register the Task Scheduler task '{autostart.TASK}' (at every logon, kept running, and "
                  f"started now):\n  {exe} {' '.join(task_args)}")
        return 0
    if report["changed"]:
        print(f"Hooks added to {path} ({len(install.EVENTS)} events)."
              + (f" Backup: {report['backup']}" if report["backup"] else ""))
        print("Claude Code sessions already open pick the hooks up when they next read their settings; "
              "a new session certainly will.")
    else:
        print(f"The hooks were already in {path}; no change.")
    if not args.no_autostart:
        config.path.parent.mkdir(parents=True, exist_ok=True)
        try:
            command = autostart.enable(config.path)
        except RuntimeError as error:
            print(f"The watcher's task couldn't be registered: {error}", file=sys.stderr)
            return 1
        print(f"The watcher runs at every logon ('{autostart.TASK}'), and has started now.\n  {command}")
    return 0


def _uninstall(args, config) -> int:
    path = _settings_path(args, config)
    try:
        report = install.change(path, lambda s: install.without_ours(s)[0], args.dry_run)
    except (install.SettingsError, OSError) as error:
        print(error, file=sys.stderr)
        return 2
    if args.dry_run:
        print(report["diff"] or f"{path}: none of this tool's hooks are there; no change.")
        if not args.no_autostart:
            print(f"\nWould remove the Task Scheduler task '{autostart.TASK}', if it exists, and stop the watcher.")
        return 0
    print((f"Hooks removed from {path}." + (f" Backup: {report['backup']}" if report["backup"] else ""))
          if report["changed"] else f"None of this tool's hooks were in {path}.")
    if not args.no_autostart:
        print("The watcher's task is removed (and the watcher stopped)." if autostart.disable()
              else "There was no watcher task.")
    gone = leases.clear(config.leases_dir)
    if gone:
        print(f"{gone} lease file(s) cleared.")
    return 0


def _autostart(action: str, config) -> int:
    if action == "on":
        config.path.parent.mkdir(parents=True, exist_ok=True)
        print(f"Autostart is on: '{autostart.TASK}' runs at every logon, and has started now.\n"
              f"  {autostart.enable(config.path)}")
    elif action == "off":
        print("Autostart is off." if autostart.disable() else "Autostart was already off.")
    else:
        info = autostart.status()
        if not info:
            print("Autostart is off. `claude-keep-awake autostart on` turns it on.")
        else:
            print(f"Autostart is on ('{autostart.TASK}').")
            for name in ("state", "last_run", "last_result", "next_run", "command"):
                print(f"  {name.replace('_', ' '):<12} {info.get(name, '')}")
    return 0


def _clock(epoch: float | None) -> str:
    return dt.datetime.fromtimestamp(epoch).strftime("%H:%M:%S") if epoch else "-"


def _status(config) -> int:
    from win_background import key, power, single_instance
    now = time.time()
    events = install.installed(config.claude_settings)
    print(f"Settings:   {config.path}{'' if config.path.exists() else ' (not created; defaults apply)'}")
    print(f"Hooks:      {'installed, ' + str(len(events)) + ' events: ' + ', '.join(events) if events else 'not installed'}"
          f" ({config.claude_settings})")

    probe = single_instance(f"claude-keep-awake-watch-{key(config.leases_dir)}")
    running = probe is None
    if probe:
        probe.release()
    task = autostart.status()
    print(f"Watcher:    {'running' if running else 'not running'}; "
          + (f"task '{autostart.TASK}' {task.get('state', '?')}, last run {task.get('last_run', '?')}"
             f" ({task.get('last_result', '?')})" if task else "no logon task (claude-keep-awake install)"))

    state = {}
    try:
        state = json.loads(pathlib.Path(config.state_file).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        pass
    if running and state:
        held = "held" if state.get("held") else "not held"
        print(f"Request:    {held} since {_clock(state.get('since'))}: {state.get('why', '')} "
              f"(as of {now - state.get('updated', now):.0f} s ago)")
    else:
        print("Request:    not held (no watcher)")

    found = leases.read(config.leases_dir)
    if not found:
        print("Leases:     none")
    for lease in found:
        live = lease.until > now
        alive = leases.pid_alive(lease.pid) if lease.pid else None
        print(f"Lease:      {lease.session[:8]} {'live' if live else 'expired'} until {_clock(lease.until)}"
              f"{' (background)' if lease.bg else ''}, last {lease.event or '?'}, Claude Code pid {lease.pid}"
              f" {'running' if alive else 'gone' if alive is False else ''}, {lease.cwd or ''}")

    mains, idle, es = power.on_mains(), power.idle_seconds(), power.execution_state()
    asking = [text for bit, text in ((0x1, "system awake"), (0x2, "display on")) if es & bit]
    print(f"Power:      {'mains' if mains else 'battery' if mains is False else 'unknown'}; idle {idle / 60:.1f} min; "
          f"programs are asking for: {', '.join(asking) or 'nothing'}")

    settings = power.lock_settings()
    point = lockpoint.effective(config.lock, lambda: settings)
    print(f"Lock point: {lockpoint.minutes(point.seconds)} idle: {point.why}")
    for line in point.considered:
        print(f"              {line}")
    saver = settings.screensaver
    side = "mains" if settings.on_mains else "battery"
    sign_in = settings.sign_in_after_sleep
    print(f"Windows ({side}):")
    print(f"  sleep after            {lockpoint.minutes(settings.sleep_after) if settings.sleep_after else 'never' if settings.sleep_after == 0 else 'unreadable'} ({settings.sleep_source})")
    print(f"  display off after      {lockpoint.minutes(settings.display_off_after) if settings.display_off_after else 'never'}")
    print(f"  sign-in after sleep    {'never' if sign_in is None else 'every time' if sign_in == 0 else 'after ' + lockpoint.minutes(sign_in)} ({settings.sign_in_source})")
    if saver:
        print(f"  screensaver            {'on' if saver.active else 'off'}, {'secure' if saver.secure else 'not secure'}, "
              f"{lockpoint.minutes(saver.timeout)}, {'chosen' if saver.program else 'none chosen'}"
              f"{', set by policy' if saver.policy else ''}")
    print(f"  inactivity limit       {lockpoint.minutes(settings.inactivity_limit) if settings.inactivity_limit else 'not set'}")
    print(f"  Modern Standby         {'yes' if settings.modern_standby else 'no' if settings.modern_standby is False else 'unknown'}")
    print(f"Holds when: a lease is live{', on mains' if config.mains_only else ''}, and idle under "
          f"{config.unattended_hours:g} h. Log: {config.log_file}")
    return 0
