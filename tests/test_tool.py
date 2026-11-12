"""Run with: python -m unittest discover -s tests"""

import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
from types import SimpleNamespace

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "src"))

from claude_keep_awake import config as cfg, hook, install, leases, lockpoint, watch  # noqa: E402

NOW = 1_800_000_000.0


class Temp(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.folder = pathlib.Path(self.temp.name)
        self.leases = self.folder / "leases"

    def tearDown(self):
        self.temp.cleanup()

    def lease(self, session="s1"):
        path = self.leases / f"{session}.json"
        return json.loads(path.read_text()) if path.exists() else None


class Hook(Temp):
    def run_hook(self, action, data=None, now=NOW, env=None, extra=()):
        data = {"session_id": "s1", "hook_event_name": "X", **(data or {})}
        return hook.handle([action, "--dir", str(self.leases), *extra], data, env or {}, now, ppid=42)

    def test_busy_writes_a_fifteen_minute_lease(self):
        self.assertEqual(self.run_hook("busy"), "wrote")
        lease = self.lease()
        self.assertEqual(lease["until"], NOW + 900)
        self.assertFalse(lease["bg"])
        self.assertEqual(lease["pid"], 42)

    def test_busy_is_skipped_if_renewed_under_a_minute_ago(self):
        self.run_hook("busy")
        self.assertEqual(self.run_hook("busy", now=NOW + 30), "skipped")
        self.assertEqual(self.lease()["until"], NOW + 900)
        self.assertEqual(self.run_hook("busy", now=NOW + 61), "wrote")
        self.assertEqual(self.lease()["until"], NOW + 61 + 900)

    def test_stop_with_nothing_in_flight_clears(self):
        self.run_hook("busy")
        self.assertEqual(self.run_hook("stop", {"background_tasks": []}), "cleared")
        self.assertIsNone(self.lease())

    def test_stop_with_only_finished_tasks_clears(self):
        self.run_hook("busy")
        self.run_hook("stop", {"background_tasks": [{"id": "1", "status": "completed"}]})
        self.assertIsNone(self.lease())

    def test_stop_with_background_work_sets_a_background_lease(self):
        self.run_hook("busy")
        self.run_hook("stop", {"background_tasks": [{"id": "t", "type": "shell", "status": "running"}]}, now=NOW + 5)
        lease = self.lease()
        self.assertTrue(lease["bg"])
        self.assertEqual(lease["until"], NOW + 5 + 2700)

    def test_idle_prompt_does_not_clear_a_background_lease(self):
        self.run_hook("stop", {"background_tasks": [{"status": "running"}]})
        self.assertEqual(self.run_hook("idle", now=NOW + 60), "kept")
        self.assertTrue(self.lease()["bg"])

    def test_idle_clears_a_foreground_lease(self):
        self.run_hook("busy")
        self.assertEqual(self.run_hook("idle"), "cleared")
        self.assertIsNone(self.lease())

    def test_end_clears_even_a_background_lease(self):
        self.run_hook("stop", {"background_tasks": [{"status": "running"}]})
        self.assertEqual(self.run_hook("end"), "cleared")
        self.assertIsNone(self.lease())

    def test_activity_during_background_work_keeps_it_a_background_lease(self):
        self.run_hook("stop", {"background_tasks": [{"status": "running"}]})
        self.assertEqual(self.run_hook("busy", now=NOW + 60), "skipped")            # 45 minutes is longer anyway
        self.assertEqual(self.run_hook("busy", now=NOW + 2000), "wrote")             # under 15 minutes left
        lease = self.lease()
        self.assertTrue(lease["bg"])
        self.assertEqual(lease["until"], NOW + 2000 + 900)

    def test_an_expired_background_lease_is_not_carried_forward(self):
        self.run_hook("stop", {"background_tasks": [{"status": "running"}]})
        self.run_hook("busy", now=NOW + 3000)
        self.assertFalse(self.lease()["bg"])

    def test_opt_out(self):
        self.assertEqual(self.run_hook("busy", env={"CLAUDE_KEEP_AWAKE": "0"}), "off")
        self.assertIsNone(self.lease())

    def test_lease_lengths_from_arguments(self):
        self.run_hook("busy", extra=("--lease", "60"))
        self.assertEqual(self.lease()["until"], NOW + 60)
        self.run_hook("stop", {"background_tasks": [{}]}, extra=("--bg-lease", "120"))
        self.assertEqual(self.lease()["until"], NOW + 120)

    def test_session_ids_cannot_escape_the_folder(self):
        hook.handle(["busy", "--dir", str(self.leases)], {"session_id": "..\\..\\evil"}, {}, NOW, ppid=1)
        self.assertEqual([p.name for p in self.leases.iterdir()], ["evil.json"])

    def test_the_script_always_exits_0_and_prints_nothing(self):
        script = pathlib.Path(hook.__file__)
        for stdin in (b"not json", b"", b"[1, 2]", json.dumps({"session_id": "x"}).encode()):
            done = subprocess.run([sys.executable, "-I", "-S", str(script), "busy", "--dir", str(self.leases)],
                                  input=stdin, capture_output=True)
            self.assertEqual((done.returncode, done.stdout, done.stderr), (0, b"", b""), stdin)
        done = subprocess.run([sys.executable, "-I", "-S", str(script)], input=b"{}", capture_output=True)
        self.assertEqual((done.returncode, done.stdout), (0, b""))

    def test_the_script_imports_nothing_heavy(self):
        text = pathlib.Path(hook.__file__).read_text()
        imports = {line.split()[1] for line in text.splitlines() if line.startswith(("import ", "from "))}
        self.assertEqual(imports, {"json", "os", "sys", "time"})


class Leases(Temp):
    def write(self, session, until, pid=None, bg=False):
        self.leases.mkdir(exist_ok=True)
        (self.leases / f"{session}.json").write_text(json.dumps({"until": until, "bg": bg, "pid": pid}))

    def test_only_unexpired_leases_are_live(self):
        self.write("a", NOW + 10)
        self.write("b", NOW - 10)
        self.assertEqual([lease.session for lease in leases.live(self.leases, NOW)], ["a"])

    def test_a_lease_whose_process_has_gone_is_not_live(self):
        self.write("a", NOW + 10, pid=1)
        self.write("b", NOW + 10, pid=2)
        found = leases.live(self.leases, NOW, check_pid=True, alive=lambda pid: pid == 2)
        self.assertEqual([lease.session for lease in found], ["b"])
        self.assertEqual(len(leases.live(self.leases, NOW, check_pid=False, alive=lambda pid: False)), 2)

    def test_tidy_removes_day_old_leases_only(self):
        self.write("old", NOW - 2 * 86400)
        self.write("recent", NOW - 60)
        self.assertEqual(leases.tidy(self.leases, NOW), 1)
        self.assertEqual([lease.session for lease in leases.read(self.leases)], ["recent"])

    def test_a_missing_folder_or_bad_file_is_no_lease(self):
        self.assertEqual(leases.live(self.folder / "nowhere", NOW), [])
        self.leases.mkdir()
        (self.leases / "bad.json").write_text("{")
        self.assertEqual(leases.read(self.leases), [])


def saver(active=True, secure=True, timeout=1800, program="scrnsave.scr", policy=False):
    return SimpleNamespace(active=active, secure=secure, timeout=timeout, program=program, policy=policy,
                           locks=active and secure and bool(program) and timeout > 0)


def windows(sleep=300, display=300, sign_in=0, screensaver=None, inactivity=None):
    return SimpleNamespace(sleep_after=sleep, display_off_after=display, sign_in_after_sleep=sign_in,
                           screensaver=screensaver if screensaver is not None else saver(), inactivity_limit=inactivity)


class LockPoint(unittest.TestCase):
    def check(self, settings, seconds, why_has):
        point = lockpoint.windows(settings)
        self.assertEqual(point.seconds, seconds)
        self.assertIn(why_has, point.why)

    def test_sleep_with_sign_in_every_time_locks_at_the_sleep_timeout(self):
        self.check(windows(sleep=300, sign_in=0), 300, "sleep after 5 min")

    def test_a_sign_in_grace_is_added_to_the_sleep_timeout(self):
        self.check(windows(sleep=300, sign_in=60), 360, "after 1 min asleep")

    def test_sleep_never_leaves_the_screensaver(self):
        self.check(windows(sleep=0), 1800, "screensaver")

    def test_sign_in_on_wake_off_leaves_the_screensaver(self):
        self.check(windows(sleep=300, sign_in=None), 1800, "screensaver")

    def test_an_earlier_secure_screensaver_wins(self):
        self.check(windows(sleep=1800, screensaver=saver(timeout=600)), 600, "screensaver after 10 min")

    def test_a_screensaver_that_does_not_lock_is_ignored(self):
        for kind in (saver(secure=False), saver(active=False), saver(program=None), saver(timeout=0)):
            self.check(windows(sleep=0, screensaver=kind), None, "wouldn't lock")

    def test_the_inactivity_limit_counts(self):
        self.check(windows(sleep=0, screensaver=saver(active=False), inactivity=900), 900, "inactivity limit")
        self.check(windows(sleep=600, inactivity=120), 120, "inactivity limit")

    def test_nothing_locks_means_no_lock(self):
        self.check(windows(sleep=0, screensaver=saver(active=False)), None, "wouldn't lock")

    def test_an_unreadable_sleep_timeout_falls_back_to_the_display_then_five_minutes(self):
        self.check(windows(sleep=None, display=180), 180, "assumed")
        self.check(windows(sleep=None, display=None), 300, "assumed")

    def test_the_setting_can_override_windows(self):
        self.assertIsNone(lockpoint.effective("never", lambda: self.fail("not read")).seconds)
        self.assertEqual(lockpoint.effective(10, lambda: self.fail("not read")).seconds, 600)
        self.assertEqual(lockpoint.effective("windows", lambda: windows()).seconds, 300)


class FakeRequest:
    def __init__(self):
        self.held = False
        self.history = []

    def hold(self, on=True):
        if on != self.held:
            self.held = on
            self.history.append(on)

    def close(self):
        self.hold(False)


class FakeSystem:
    def __init__(self):
        self.mains, self.idle, self.state = True, 0.0, 0
        self.settings = windows()
        self.locks = 0
        self.req = FakeRequest()

    def on_mains(self):
        return self.mains

    def idle_seconds(self):
        return self.idle

    def execution_state(self):
        return self.state | (1 if self.req.held else 0)

    def lock_settings(self):
        return self.settings

    def lock(self):
        self.locks += 1
        return True

    def request(self, reason):
        return self.req


class Watcher(Temp):
    def setUp(self):
        super().setUp()
        self.config = cfg.Config(self.folder / "config.toml", leases_dir=self.leases, state_file=self.folder / "state.json")
        self.system = FakeSystem()
        self.logged, self.notified = [], []
        self.watcher = watch.Watcher(self.config, self.system, self.logged.append,
                                     lambda title, lines: self.notified.append(title), alive=lambda pid: True)

    def session(self, until=NOW + 900, name="s1"):
        self.leases.mkdir(exist_ok=True)
        (self.leases / f"{name}.json").write_text(json.dumps({"until": until, "bg": False, "pid": 1}))

    def step(self, now=NOW, idle=None):
        if idle is not None:
            self.system.idle = idle
        self.watcher.step(now)

    def test_holds_while_a_session_works_and_releases_after(self):
        self.step()
        self.assertFalse(self.system.req.held)
        self.session()
        self.step()
        self.assertTrue(self.system.req.held)
        self.assertTrue(any(line.startswith("holding") for line in self.logged))
        self.step(now=NOW + 901)
        self.assertFalse(self.system.req.held)
        self.assertTrue(any(line.startswith("released") for line in self.logged))
        self.assertEqual(json.loads(self.config.state_file.read_text())["held"], False)

    def test_not_on_battery_unless_allowed(self):
        self.session()
        self.system.mains = False
        self.step()
        self.assertFalse(self.system.req.held)
        self.config.mains_only = False
        self.step()
        self.assertTrue(self.system.req.held)

    def test_unknown_power_source_counts_as_mains(self):
        self.session()
        self.system.mains = None
        self.step()
        self.assertTrue(self.system.req.held)

    def test_the_unattended_cap_releases_and_notifies_once(self):
        self.session(until=NOW + 99999)
        self.step(idle=60)
        self.assertTrue(self.system.req.held)
        self.step(idle=3 * 3600)
        self.assertFalse(self.system.req.held)
        self.step(now=NOW + 15, idle=3 * 3600 + 15)
        self.assertEqual(len(self.notified), 1)
        self.step(now=NOW + 30, idle=0)            # someone's back
        self.assertTrue(self.system.req.held)
        self.step(now=NOW + 45, idle=3 * 3600)
        self.assertEqual(len(self.notified), 2)

    def test_the_cap_does_not_notify_with_no_session_working(self):
        self.step(idle=4 * 3600)
        self.assertEqual(self.notified, [])

    def test_locks_once_at_the_lock_point_while_holding(self):
        self.session()
        self.step(idle=299)
        self.assertEqual(self.system.locks, 0)
        self.step(now=NOW + 1, idle=300)
        self.assertEqual(self.system.locks, 1)
        self.step(now=NOW + 15, idle=315)
        self.assertEqual(self.system.locks, 1)      # once per idle stretch
        self.step(now=NOW + 30, idle=5)             # back at the keyboard
        self.step(now=NOW + 330, idle=305)
        self.assertEqual(self.system.locks, 2)

    def test_does_not_lock_when_not_holding(self):
        self.step(idle=600)
        self.assertEqual(self.system.locks, 0)

    def test_does_not_lock_while_another_program_keeps_the_display_on(self):
        self.session()
        self.system.state = 0x2
        self.step(idle=600)
        self.assertEqual(self.system.locks, 0)
        self.assertTrue(any("display on" in line for line in self.logged))
        self.system.state = 0
        self.step(now=NOW + 15, idle=615)
        self.assertEqual(self.system.locks, 1)

    def test_no_lock_when_windows_would_not_lock(self):
        self.session()
        self.system.settings = windows(sleep=0, screensaver=saver(active=False))
        self.step(idle=3000)
        self.assertEqual(self.system.locks, 0)

    def test_lock_never_and_fixed_minutes(self):
        self.session()
        self.config.lock = "never"
        self.step(idle=3000)
        self.assertEqual(self.system.locks, 0)
        self.config.lock = 1
        self.watcher.point = None
        self.step(now=NOW + 1, idle=61)
        self.assertEqual(self.system.locks, 1)

    def test_a_change_in_windows_settings_applies_within_a_minute(self):
        self.session(until=NOW + 9999)
        self.step(idle=0)
        self.system.settings = windows(sleep=120)
        self.step(now=NOW + 30, idle=130)
        self.assertEqual(self.system.locks, 0)       # still the old 5 minutes
        self.step(now=NOW + 61, idle=130)
        self.assertEqual(self.system.locks, 1)
        self.assertTrue(any(line.startswith("lock point now 2 min") for line in self.logged))

    def test_logs_alive_hourly(self):
        self.step()
        self.step(now=NOW + 1800)
        self.step(now=NOW + 3600)
        self.assertEqual(sum(line.startswith("alive") for line in self.logged), 2)

    def test_closing_releases(self):
        self.session()
        self.step()
        self.watcher.close()
        self.assertFalse(self.system.req.held)


class Loop(Temp):
    def test_run_steps_then_releases_and_refuses_a_second_watcher(self):
        from win_background import key
        config = cfg.Config(self.folder / "c.toml", leases_dir=self.leases, state_file=self.folder / "state.json",
                            check_seconds=0.01)
        system, logged = FakeSystem(), []
        self.leases.mkdir()
        (self.leases / "s.json").write_text(json.dumps({"until": 9e12, "pid": None}))
        self.assertEqual(watch.run(config, logged.append, lambda *a: None, system, sleep=lambda s: None, steps=2), 0)
        self.assertFalse(system.req.held)
        self.assertEqual(system.req.history, [True, False])
        self.assertTrue(logged[0].startswith("started"))
        name = f"claude-keep-awake-watch-{key(self.leases)}"
        code = "; ".join(["import time", "from win_background import single_instance",
                          f"held = single_instance({name!r})", "print('held', flush=True)", "time.sleep(30)"])
        other = subprocess.Popen([sys.executable, "-c", code], stdout=subprocess.PIPE,
                                 text=True)   # a mutex is re-entrant in one thread, so hold it from another process
        try:
            self.assertEqual(other.stdout.readline().strip(), "held")
            self.assertEqual(watch.run(config, logged.append, lambda *a: None, FakeSystem(), steps=1), 3)
        finally:
            other.kill()
            other.wait()

    def test_errors_are_logged_and_notified_on_the_third(self):
        config = cfg.Config(self.folder / "c.toml", leases_dir=self.leases, state_file=self.folder / "s.json")
        system, logged, notified = FakeSystem(), [], []
        system.idle_seconds = lambda: 1 / 0
        watch.run(config, logged.append, lambda title, lines: notified.append(title), system,
                  sleep=lambda s: None, steps=4)
        self.assertEqual(sum(line.startswith("error") for line in logged), 4)
        self.assertEqual(len(notified), 1)


class Cli(Temp):
    def test_install_and_uninstall_from_the_command_line(self):
        from contextlib import redirect_stdout
        from io import StringIO
        from claude_keep_awake import cli
        settings = self.folder / "settings.json"
        settings.write_text('{"model": "opus"}' + chr(10))
        conf = ["--config", str(self.folder / "none.toml")]
        out = StringIO()
        with redirect_stdout(out):
            self.assertEqual(cli.main(conf + ["install", "--settings", str(settings), "--dry-run"]), 0)
        self.assertIn("+  \"hooks\": {", out.getvalue())
        self.assertIn("Would register", out.getvalue())
        self.assertEqual(settings.read_text(), '{"model": "opus"}' + chr(10))
        with redirect_stdout(StringIO()):
            self.assertEqual(cli.main(conf + ["install", "--settings", str(settings), "--no-autostart"]), 0)
            self.assertEqual(len(install.installed(settings)), len(install.EVENTS))
            self.assertEqual(cli.main(conf + ["uninstall", "--settings", str(settings), "--no-autostart"]), 0)
        self.assertEqual(json.loads(settings.read_text()), {"model": "opus"})
        settings.write_text("{oops")
        with redirect_stdout(StringIO()), __import__("contextlib").redirect_stderr(StringIO()) as err:
            self.assertEqual(cli.main(conf + ["install", "--settings", str(settings), "--no-autostart"]), 2)
        self.assertIn("isn't valid JSON", err.getvalue())


class Install(Temp):
    def setUp(self):
        super().setUp()
        self.settings = self.folder / "settings.json"
        self.config = cfg.Config(self.folder / "config.toml")
        self.original = {
            "env": {"X": "1"}, "model": "opus",
            "hooks": {"PreToolUse": [{"matcher": "Bash", "hooks": [{"type": "command", "command": "lint.sh"}]}],
                      "Stop": [{"hooks": [{"type": "command", "command": "notify.sh"}]}]},
            "theme": "dark",
        }
        self.settings.write_text(install.render(self.original), encoding="utf-8")

    def install(self, dry_run=False):
        return install.change(self.settings, lambda s: install.with_ours(s, self.config), dry_run)

    def uninstall(self):
        return install.change(self.settings, lambda s: install.without_ours(s)[0])

    def read(self):
        return json.loads(self.settings.read_text(encoding="utf-8"))

    def backups(self):
        return list(self.folder.glob("settings.json.claude-keep-awake-*.bak"))

    def test_install_adds_every_event_and_keeps_everything_else(self):
        report = self.install()
        self.assertTrue(report["changed"])
        data = self.read()
        self.assertEqual(set(install.installed(self.settings)), {event for event, _, _ in install.EVENTS})
        for key in ("env", "model", "theme"):
            self.assertEqual(data[key], self.original[key])
        self.assertEqual(data["hooks"]["PreToolUse"][0], self.original["hooks"]["PreToolUse"][0])
        self.assertEqual(data["hooks"]["Stop"][0], self.original["hooks"]["Stop"][0])
        notification = data["hooks"]["Notification"][0]
        self.assertEqual(notification["matcher"], "idle_prompt")
        ours = data["hooks"]["Stop"][1]["hooks"][0]
        self.assertEqual(ours["args"][:2], ["-I", "-S"])
        self.assertTrue(ours["args"][2].endswith("hook.py"))
        self.assertEqual(ours["args"][3], "stop")
        self.assertEqual(ours["timeout"], 5)

    def test_install_backs_up_first(self):
        before = self.settings.read_bytes()
        report = self.install()
        self.assertEqual(report["backup"].read_bytes(), before)
        self.assertEqual(len(self.backups()), 1)

    def test_install_twice_changes_nothing(self):
        self.install()
        after_first = self.settings.read_text()
        report = self.install()
        self.assertFalse(report["changed"])
        self.assertEqual(self.settings.read_text(), after_first)
        self.assertEqual(len(self.backups()), 1)

    def test_reinstalling_with_new_settings_replaces_ours_without_duplicating(self):
        self.install()
        self.config.lease_minutes = 20
        self.install()
        stops = self.read()["hooks"]["Stop"]
        self.assertEqual(len(stops), 2)
        self.assertIn("1200", stops[1]["hooks"][0]["args"])

    def test_uninstall_removes_exactly_ours(self):
        self.install()
        self.uninstall()
        self.assertEqual(self.read(), self.original)
        self.assertEqual(install.installed(self.settings), [])

    def test_uninstall_keeps_a_group_shared_with_another_hook(self):
        data = dict(self.original)
        mine = install.handler(self.config, "busy")
        data["hooks"] = {"PreToolUse": [{"hooks": [{"type": "command", "command": "other.sh"}, mine]}]}
        self.settings.write_text(install.render(data))
        self.uninstall()
        self.assertEqual(self.read()["hooks"], {"PreToolUse": [{"hooks": [{"type": "command", "command": "other.sh"}]}]})

    def test_uninstall_with_nothing_of_ours_writes_nothing(self):
        report = self.uninstall()
        self.assertFalse(report["changed"])
        self.assertEqual(self.backups(), [])

    def test_a_dry_run_shows_the_diff_and_writes_nothing(self):
        before = self.settings.read_text()
        report = self.install(dry_run=True)
        self.assertEqual(self.settings.read_text(), before)
        self.assertEqual(self.backups(), [])
        added = [line for line in report["diff"].splitlines() if line.startswith("+") and not line.startswith("+++")]
        removed = [line for line in report["diff"].splitlines() if line.startswith("-") and not line.startswith("---")]
        self.assertTrue(any("hook.py" in line for line in added))
        self.assertEqual([line for line in removed], [])    # only additions

    def test_invalid_json_is_refused_and_left_alone(self):
        self.settings.write_text('{"model": "opus",}')
        with self.assertRaises(install.SettingsError):
            self.install()
        self.assertEqual(self.settings.read_text(), '{"model": "opus",}')
        self.assertEqual(self.backups(), [])

    def test_a_non_object_is_refused(self):
        self.settings.write_text("[]")
        with self.assertRaises(install.SettingsError):
            self.install()

    def test_a_missing_file_is_created(self):
        self.settings.unlink()
        report = self.install()
        self.assertIsNone(report["backup"])
        self.assertEqual(set(self.read()), {"hooks"})

    def test_custom_leases_dir_and_lengths_reach_the_hook(self):
        self.config.leases_dir = self.folder / "elsewhere"
        self.config.background_lease_minutes = 60
        args = install.handler(self.config, "stop")["args"]
        self.assertEqual(args[args.index("--dir") + 1], str(self.folder / "elsewhere"))
        self.assertEqual(args[args.index("--bg-lease") + 1], "3600")
        self.assertNotIn("--lease", args)


class Config(unittest.TestCase):
    def test_defaults_without_a_file(self):
        with tempfile.TemporaryDirectory() as temp:
            config = cfg.load(pathlib.Path(temp) / "missing.toml")
        self.assertEqual((config.lease_seconds, config.background_lease_seconds, config.unattended_seconds),
                         (900, 2700, 3 * 3600))
        self.assertEqual(config.lock, "windows")
        self.assertTrue(config.mains_only)

    def test_values_and_refusals(self):
        with tempfile.TemporaryDirectory() as temp:
            path = pathlib.Path(temp) / "c.toml"
            path.write_text('lock = 7\nunattended_hours = 1.5\nleases_dir = "leases"\n')
            config = cfg.load(path)
            self.assertEqual(config.lock, 7)
            self.assertEqual(config.unattended_seconds, 5400)
            self.assertEqual(config.leases_dir, (pathlib.Path(temp) / "leases").resolve())
            for bad in ('lock = "sometimes"', "lock = 0", "lock = true", "unattended_hours = 0", "nonsense = 1"):
                path.write_text(bad)
                with self.assertRaises(ValueError, msg=bad):
                    cfg.load(path)


if __name__ == "__main__":
    unittest.main()
