# claude-keep-awake

Keep a Windows laptop awake while Claude Code is working, and let it sleep, and lock, as normal otherwise.

A long Claude Code task can run for half an hour with nobody touching the keyboard. Windows counts only
keyboard and mouse input as activity, so on a laptop that sleeps after a few minutes the session is
suspended mid-task. Claude Code keeps a Mac awake while it works (`caffeinate`), but does nothing on Windows.

This tool uses Claude Code's own hooks to know when a session is working, and holds a named Windows power
request ("Claude Code is working") only for that time. It needs no admin rights, never changes a Windows
setting, and never fakes input.

```
pip install git+https://github.com/gareth-jones-zen/win-background
pip install git+https://github.com/gareth-jones-zen/claude-keep-awake
claude-keep-awake install --dry-run     # see exactly what it would change
claude-keep-awake install               # the hooks, and the watcher at every logon
claude-keep-awake status
```

Or, from a checkout, `pip install -e .` so changes apply at once. Python 3.11+ on Windows.
`python -m claude_keep_awake` works wherever `claude-keep-awake` isn't on the PATH.

## How it works

**Hooks write a lease per session.** `install` adds command hooks to your Claude Code user settings
(`~/.claude/settings.json`), so every project is covered. Each runs a small script, `hook.py`, which keeps one
file per session in `%LOCALAPPDATA%\claude-keep-awake\leases\`:

| Hook | What it does |
|---|---|
| UserPromptSubmit, PreToolUse, SubagentStart | A 15-minute lease. Skipped if it was renewed under a minute ago, so a burst of tool calls costs one write |
| Stop | If `background_tasks` has anything still running (a background shell or subagent), a 45-minute background lease; otherwise the lease goes, because the turn is over |
| Notification `idle_prompt`, StopFailure | The lease goes, unless it's a background lease (`idle_prompt` fires while background work is still running) |
| SessionEnd | The lease goes |

A crash, or a terminal closed without SessionEnd, leaves a lease that simply expires. The lease records
Claude Code's process id, and the watcher ignores a lease whose process has gone.

**The watcher holds one power request** while any lease is live, the laptop is on mains, and someone has
used the keyboard or mouse in the last 3 hours. It runs at logon (Task Scheduler, through
[win-background](https://github.com/gareth-jones-zen/win-background)), one copy at a time, and checks every
15 seconds. If the 3-hour cap lets the laptop sleep while sessions are still working, it says so once, in a
notification. Otherwise it's silent; the log is `%LOCALAPPDATA%\claude-keep-awake\claude-keep-awake.log`,
with `holding`, `released`, `locked` and an hourly `alive`.

**Windows still decides the rest.** Closing the lid, pressing the power button or choosing Sleep still
sleeps it: Windows ends power requests then, by design. On battery Windows ends the request about five
minutes after the sleep timeout anyway, so by default the watcher doesn't hold it on battery at all. The
display still turns off as normal, and the screensaver still starts.

## Locking: where Windows would have locked

Keeping a laptop awake must not keep it unlocked. Without the request, an idle laptop locks at the first of:

1. **sleeping, when waking needs a sign-in**: the sleep timeout for the current power source, plus the
   "Require sign-in" grace;
2. **a secure screensaver**: active, "on resume, display logon screen", a screensaver chosen, and a timeout;
3. **the machine inactivity limit**, a security policy.

**While it holds the request, the watcher locks the session (as Windows+L does) at the earliest of these.**
If none applies (sleep set to never, no secure screensaver, no inactivity limit), Windows would never have
locked, and neither does the watcher. It locks once per idle stretch. **It doesn't lock while another program
is keeping the display on**, such as a call, a video or a presentation, because then Windows wouldn't have
slept or started the screensaver either.

The values are read from Windows every minute, so a change in Settings applies straight away:

| Value | Where it's read |
|---|---|
| Sleep after, display off after | The active power plan, for the current power source (`PowerReadACValueIndex` / `PowerReadDCValueIndex`); a Group Policy value under `HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings` wins; `powercfg /query` as a fallback. 0 means never. If it can't be read, the display timeout is assumed, then 5 minutes |
| Sign-in on wake | Settings > Accounts > Sign-in options, "If you've been away, when should Windows require you to sign in again?". **On Modern Standby** that's `DelayLockInterval` in `HKCU\Control Panel\Desktop` (0 every time, a number of seconds, or 0xFFFFFFFF never; missing is taken as every time). **On other machines** it's the "require a password on wakeup" power setting (CONSOLELOCK). A Group Policy value for that setting wins on either |
| Screensaver | `SystemParametersInfo`, which gives the effective active, secure and timeout values, Group Policy included; the screensaver program from the policy key, then the user's own |
| Machine inactivity limit | `InactivityTimeoutSecs` under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` |
| Another program keeping the display on | `CallNtPowerInformation(SystemExecutionState)`, which needs no admin rights (unlike `powercfg /requests`) |

`claude-keep-awake status` shows each value, where it came from, and the lock point they give. To lock at
a fixed point instead, set `lock = 10` (minutes) in the settings; `lock = "never"` turns locking off.

## Commands

```
claude-keep-awake install [--settings PATH] [--dry-run] [--no-autostart]
claude-keep-awake uninstall [--settings PATH] [--dry-run] [--no-autostart]
claude-keep-awake status
claude-keep-awake config                  create the settings file (if missing) and say where it is
claude-keep-awake autostart on|off|status
claude-keep-awake watch                   the watcher itself; what the logon task runs
claude-keep-awake hook busy|stop|idle|end what the hooks do, for trying by hand (JSON on stdin)
```

**`install` changes only its own entries.** It backs the settings file up first
(`settings.json.claude-keep-awake-<time>.bak`, beside it), refuses a file that isn't valid JSON, adds each
hook in a matcher group of its own, and leaves every other key and every other hook as it was. Its entries are
the command hooks whose arguments name this package's `hook.py`; running `install` again replaces them rather
than adding more, and `uninstall` removes exactly those. `--dry-run` prints the change as a diff, and the
task it would register.

The hooks run `pythonw.exe -I -S <path>\hook.py <action>` directly, in exec form (`args`, no shell): no
console window, and Python starting as fast as it can. The script uses nothing but `json`, `os`, `sys` and
`time`, always exits 0 and never prints, because Claude Code reads a hook's output as context or a decision.
Measured on a laptop: about 50 to 60 ms a call, of which about 33 ms is Python starting.

**Already-open sessions** pick up new hooks when Claude Code next reads its settings; a new session
certainly will. Registering the logon task needs you at the console.

## Settings

Every setting has a default, so no file is needed. `claude-keep-awake config` creates
`%APPDATA%\claude-keep-awake\config.toml` with them all commented out (or point `--config`, or
`CLAUDE_KEEP_AWAKE_CONFIG`, somewhere else).

```toml
reason = "Claude Code is working"      # what powercfg /requests shows
lease_minutes = 15                      # one prompt or tool call keeps it awake this long
background_lease_minutes = 45           # after a turn ends with background work still running
unattended_hours = 3                    # stop keeping it awake after this long with no keyboard or mouse
mains_only = true
lock = "windows"                        # "windows", "never", or a number of minutes
check_seconds = 15
check_pid = true                        # ignore a lease whose Claude Code process has gone
leases_dir = "%LOCALAPPDATA%/claude-keep-awake/leases"
log_file = "%LOCALAPPDATA%/claude-keep-awake/claude-keep-awake.log"
state_file = "%LOCALAPPDATA%/claude-keep-awake/state.json"
claude_settings = "~/.claude/settings.json"
```

The hooks carry the lease lengths and the leases folder, so after changing those, run `install` again.

**To leave one run out**, set `CLAUDE_KEEP_AWAKE=0` in its environment: the hook then does nothing. Headless
`claude -p` runs fire user hooks too, so a scheduled run keeps the laptop awake while it works unless you
opt it out.

## Edge cases

| Case | What happens |
|---|---|
| Several sessions at once | One lease each; held while any is live |
| Esc mid-turn | Stop doesn't fire; `idle_prompt` clears the lease about a minute later, or it expires |
| A tool or model call longer than 15 minutes | Covered up to 15 minutes from the last renewal (the Bash tool's limit is 10) |
| API error or usage limit | StopFailure clears the lease |
| `/loop` and scheduled wake-ups | Deliberately not kept awake between wake-ups |
| A background shell that never ends | Bounded by the 45-minute lease and the 3-hour cap |

## Tests

```
python -m unittest discover -s tests
```

They use fakes for Windows, Claude Code and the clock, except one that holds a real lock from a second process.
