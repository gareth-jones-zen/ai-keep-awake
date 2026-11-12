# ai-keep-awake

Keep a Windows laptop awake while an AI agent (Claude Code) is working, and let it sleep, and lock, as
normal otherwise.

A long Claude Code task can run for half an hour with nobody touching the keyboard. Windows counts only
keyboard and mouse input as activity, so on a laptop that sleeps after a few minutes the session is
suspended mid-task. Claude Code keeps a Mac awake while it works (`caffeinate`), but does nothing on Windows.

This tool uses Claude Code's own hooks to know when a session is working, and holds a named Windows power
request ("An AI agent is working (Claude Code)") only for that time. It needs no admin rights, never changes
a Windows setting, and never fakes input. It's two small native programs (about 3.7 MB each), with nothing
else to install first.

## Install

1. Download the zip from [Releases](https://github.com/gareth-jones-zen/ai-keep-awake/releases)
   (`ai-keep-awake-<version>-win-x64.zip`; the current one is the pre-release `v1.0.0-rc.1`).
2. Unzip it anywhere, open a terminal in that folder, and run:

```powershell
.\ai-keep-awake.exe install --dry-run    # see exactly what it would change
.\ai-keep-awake.exe install              # copy the programs, add the hooks, start the watcher at every logon
.\ai-keep-awake.exe status
```

`install` copies both programs to `%LOCALAPPDATA%\Programs\ai-keep-awake`, adds the hooks to your Claude Code
user settings (`~/.claude/settings.json`) and registers the logon task, which needs you at the console. The
unzipped folder isn't used after that, so it can be deleted.

`install` doesn't change your PATH. From then on, either run the installed copy by its full path:

```powershell
& "$env:LOCALAPPDATA\Programs\ai-keep-awake\ai-keep-awake.exe" status
```

or add `%LOCALAPPDATA%\Programs\ai-keep-awake` to your user PATH and type `ai-keep-awake`, as the rest of this
README does.

| To | Run |
|---|---|
| Upgrade | Download and unzip the new release, and run its `install`. It replaces the installed programs, even while hooks are running the old ones |
| Uninstall | `ai-keep-awake uninstall`. It takes out the hooks and the task, clears the leases, and says where the programs are; delete that folder to remove them |
| Check | `ai-keep-awake status` |

### The two programs

| Program | What it's for |
|---|---|
| `ai-keep-awake.exe` | The commands, with output. What you run |
| `ai-keep-awake-background.exe` | No window, ever. What the hooks and the logon task run, so nothing flashes up on every tool call |

### SmartScreen, and checking a download

The programs aren't code-signed, so Windows SmartScreen may warn the first time a downloaded copy is started
from Explorer. To check that a download is the one GitHub Actions built from this repo, each release has a
`SHA256SUMS.txt` (for the zip and both programs) and a signed build provenance attestation:

```powershell
Get-FileHash .\ai-keep-awake-1.0.0-rc.1-win-x64.zip -Algorithm SHA256    # compare with SHA256SUMS.txt
gh attestation verify .\ai-keep-awake-1.0.0-rc.1-win-x64.zip --repo gareth-jones-zen/ai-keep-awake
```

`sha256sum -c SHA256SUMS.txt --ignore-missing` works too, from Git Bash or WSL.

## How it works

**Hooks write a lease per session.** `install` adds command hooks to your Claude Code user settings, so every
project is covered. Each runs `ai-keep-awake-background.exe hook <action>`, which keeps one small file per
session in `%LOCALAPPDATA%\ai-keep-awake\leases\`:

| Hook | What it does |
|---|---|
| UserPromptSubmit, PreToolUse, SubagentStart | A 15-minute lease. Skipped if it was renewed under a minute ago, so a burst of tool calls costs one write |
| Stop | If `background_tasks` has anything still running (a background shell or subagent), a 45-minute background lease; otherwise the lease goes, because the turn is over |
| Notification `idle_prompt`, StopFailure | The lease goes, unless it's a background lease (`idle_prompt` fires while background work is still running) |
| SessionEnd | The lease goes |

A crash, or a terminal closed without SessionEnd, leaves a lease that simply expires. The lease records
Claude Code's process id, and the watcher ignores a lease whose process has gone.

**The watcher holds one power request** while any lease is live, the laptop is on mains, and someone has
used the keyboard or mouse in the last 3 hours. It runs at logon (the Task Scheduler task
`ai-keep-awake watch`), one copy at a time, and checks every 15 seconds. The task restarts it if it crashes,
and never stops it for time. If the 3-hour cap lets the laptop sleep while sessions are still working, it
says so once, in a notification; it also notifies if a check fails three times in a row. Otherwise it's
silent. The log is `%LOCALAPPDATA%\ai-keep-awake\ai-keep-awake.log`, with `started`, `holding`, `released`,
`locked` and an hourly `alive`.

**Windows still decides the rest.** Closing the lid, pressing the power button or choosing Sleep still
sleeps it: Windows ends power requests then, by design. On battery Windows ends the request about five
minutes after the sleep timeout anyway, so by default the watcher doesn't hold it on battery at all. The
display still turns off as normal, and the screensaver still starts. Windows also drops the request when the
watcher exits, so a crash can't leave the laptop awake.

While it's held, `powercfg /requests` (in an admin terminal) shows the request by name.

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
| Sleep after, display off after | The active power plan, for the current power source (`PowerReadACValueIndex` / `PowerReadDCValueIndex`); a Group Policy value under `HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings` wins; `powercfg /query` as a fallback. 0 means never. If the sleep timeout can't be read, the display timeout is assumed, then 5 minutes |
| Sign-in on wake | Settings > Accounts > Sign-in options, "If you've been away, when should Windows require you to sign in again?". **On Modern Standby** that's `DelayLockInterval` in `HKCU\Control Panel\Desktop` (0 every time, a number of seconds, or 0xFFFFFFFF never; missing is taken as every time). **On other machines** it's the "require a password on wakeup" power setting (CONSOLELOCK). A Group Policy value for that setting wins on either |
| Screensaver | `SystemParametersInfo`, which gives the effective active, secure and timeout values, Group Policy included; the screensaver program from the policy key, then the user's own |
| Machine inactivity limit | `InactivityTimeoutSecs` under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` |
| Another program keeping the display on | `CallNtPowerInformation(SystemExecutionState)`, which needs no admin rights (unlike `powercfg /requests`) |

`ai-keep-awake status` shows each value, where it came from, and the lock point they give. To lock at a fixed
point instead, set `lock = 10` (minutes) in the settings; `lock = "never"` turns locking off.

## Commands

```
ai-keep-awake [--config CONFIG] <command>

ai-keep-awake install [--dry-run] [--settings PATH] [--install-dir PATH] [--no-autostart]
ai-keep-awake uninstall                   take out exactly this tool's hooks, and remove the watcher's task
ai-keep-awake status                      hooks, watcher, leases, the power request, and the lock point from Windows
ai-keep-awake config                      create the settings file (if missing) and say where it is
ai-keep-awake autostart on|off|status     the logon task on its own
ai-keep-awake watch                       the watcher itself (what the logon task runs)
ai-keep-awake hook busy|stop|idle|end     what the hooks run (reads the hook's JSON on stdin)
ai-keep-awake --version
```

| `install` option | What it does |
|---|---|
| `--dry-run` | Shows the copy, the change to `settings.json` as a diff, and the task; changes nothing |
| `--settings PATH` | Another Claude Code settings file |
| `--install-dir PATH` | Another folder for the programs, for this run. To make it stick, set `install_dir` in the settings too, so `status` and `autostart` look there |
| `--no-autostart` | Only the programs and the hooks; no task |

Exit codes: 0 done, 1 failed, 2 bad arguments or settings, 3 a second watcher (it doesn't start).

### What `install` changes

**Only its own entries in `settings.json`.** It backs the file up first
(`settings.json.ai-keep-awake-<yyyyMMdd-HHmmss>.bak`, beside it, never over an earlier backup), refuses a file
that isn't valid JSON, adds each hook in a matcher group of its own, and leaves every other key and every other
hook as it was. Its entries are found by their shape: a command hook that runs `ai-keep-awake.exe` or
`ai-keep-awake-background.exe` with `hook` as the first argument. Running `install` again replaces them rather
than adding more, and `uninstall` removes exactly those. If Claude Code writes the file while `install` is
changing it, the change is made again on the new contents rather than losing that write. A symbolic link (a
dotfiles setup) is written through to the file it points at.

Each hook looks like this:

```json
{
  "type": "command",
  "command": "C:\\Users\\you\\AppData\\Local\\Programs\\ai-keep-awake\\ai-keep-awake-background.exe",
  "args": ["hook", "busy"],
  "timeout": 15
}
```

The hooks run the program directly, in exec form (`args`, no shell), so there's no console window. The hook
never reads the settings file, always exits 0 and never prints, because Claude Code reads a hook's output as
context or a decision. A call takes about 40 ms. The 15-second timeout is well over that because the first
run of a newly installed exe waits for Microsoft Defender to scan it, and a hook that Claude Code kills writes
no lease.

**The programs are copied in a way that works while they're running.** Every open session runs the installed
`ai-keep-awake-background.exe` on each tool call, and a running exe can't be overwritten. So `install` stops the
watcher, copies each program in as `.new`, renames the old one aside to `.old` and moves the new one into
place. A failure puts back anything already swapped, so the install is never half old and half new, and the
watcher is started again as it was. Leftover `.old` files are deleted by the next install.

**Already-open sessions** pick up new hooks when Claude Code next reads its settings; a new session certainly
will. Registering the logon task needs you at the console.

## Settings

Every setting has a default, so no file is needed. `ai-keep-awake config` creates
`%APPDATA%\ai-keep-awake\config.toml` with them all commented out. To use another file, pass `--config`, or
set `AI_KEEP_AWAKE_CONFIG`.

```toml
reason = "An AI agent is working (Claude Code)"   # what powercfg /requests shows
lease_minutes = 15                      # one prompt or tool call keeps it awake this long
background_lease_minutes = 45           # after a turn ends with background work still running
unattended_hours = 3                    # stop keeping it awake after this long with no keyboard or mouse
mains_only = true
lock = "windows"                        # "windows", "never", or a number of minutes
check_seconds = 15
check_pid = true                        # ignore a lease whose Claude Code process has gone
leases_dir = "%LOCALAPPDATA%/ai-keep-awake/leases"
log_file = "%LOCALAPPDATA%/ai-keep-awake/ai-keep-awake.log"
state_file = "%LOCALAPPDATA%/ai-keep-awake/state.json"
claude_settings = "~/.claude/settings.json"
install_dir = "%LOCALAPPDATA%/Programs/ai-keep-awake"
```

Paths may use `~` and `%VARIABLES%`; a relative path is relative to the settings file. An unknown setting is
an error, so a typo doesn't pass silently. The hooks carry the lease lengths, the leases folder and the
install folder, so after changing any of those, run `install` again.

**To leave one run out**, set `AI_KEEP_AWAKE=0` in its environment (`off`, `false` and `no` work too): the hook
then does nothing. Headless `claude -p` runs fire user hooks too, so a scheduled run keeps the laptop awake
while it works unless you opt it out.

## Moving from the Python claude-keep-awake

This tool was the Python package `claude-keep-awake` (0.1.0, tag `v0.1.0`). The .NET version needs no Python,
and `install` takes care of the switch:

- it replaces the old hooks (the ones that ran `claude_keep_awake\hook.py`) with its own;
- it removes the old task, `claude-keep-awake watch`, and stops its watcher;
- `status` shows any old hooks still in place, and `install --dry-run` says whether the old task would be removed.

Some names changed, and nothing is carried over from the old ones:

| | Python claude-keep-awake | ai-keep-awake |
|---|---|---|
| Settings | `%APPDATA%\claude-keep-awake\config.toml` | `%APPDATA%\ai-keep-awake\config.toml` |
| Settings variable | `CLAUDE_KEEP_AWAKE_CONFIG` | `AI_KEEP_AWAKE_CONFIG` |
| Off switch | `CLAUDE_KEEP_AWAKE=0` | `AI_KEEP_AWAKE=0` |
| Leases, log, state | `%LOCALAPPDATA%\claude-keep-awake\` | `%LOCALAPPDATA%\ai-keep-awake\` |
| Task | `claude-keep-awake watch` | `ai-keep-awake watch` |
| Request reason | "Claude Code is working" | "An AI agent is working (Claude Code)" |
| Hook timeout | 5 seconds | 15 seconds |

If you changed any settings, copy the old `config.toml` across (and change any paths in it that name
`claude-keep-awake`). Anything that sets `CLAUDE_KEEP_AWAKE=0` needs the new name. Afterwards the old Python
package can go (`pip uninstall claude-keep-awake`), and so can `%LOCALAPPDATA%\claude-keep-awake\`.

## Edge cases

| Case | What happens |
|---|---|
| Several sessions at once | One lease each; held while any is live |
| Esc mid-turn | Stop doesn't fire; `idle_prompt` clears the lease about a minute later, or it expires |
| A tool or model call longer than 15 minutes | Covered up to 15 minutes from the last renewal (the Bash tool's limit is 10) |
| API error or usage limit | StopFailure clears the lease |
| `/loop` and scheduled wake-ups | Deliberately not kept awake between wake-ups |
| A background shell that never ends | Bounded by the 45-minute lease and the 3-hour cap |
| Upgrading while sessions are working | The running programs are renamed aside, not overwritten; see *What `install` changes* |
| The first hook call after an install | Can wait a few seconds for Defender's scan; the 15-second timeout covers it |
| A second watcher started by hand | Logs that one is already running, and exits with code 3 |

## Building from source

You need the .NET 11 SDK (`global.json` pins the feature band). Native AOT publishing also needs the Visual
Studio C++ build tools ("Desktop development with C++"), with `vswhere.exe` on the PATH.

The Windows calls come from the `WinBackground` library in
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib), shared with other tools.
Check it out beside this repo, so the two folders sit side by side (for example `source\repos\ai-keep-awake` and
`source\repos\background-tools-lib`); the build references it there. To build against a checkout somewhere
else, pass `-p:BackgroundToolsLib=<its folder>`. The workflows check out its `main` beside this repo.

```powershell
git clone https://github.com/gareth-jones-zen/background-tools-lib ..\background-tools-lib
dotnet build -c Release
dotnet test -c Release
dotnet publish src/AiKeepAwake.Console -c Release -r win-x64 -o publish
dotnet publish src/AiKeepAwake.Background -c Release -r win-x64 -o publish
.\publish\ai-keep-awake.exe install --dry-run
```

`install` needs both programs side by side, as `publish` leaves them. Pushing a tag such as `v1.0.0` (or
`v1.0.0-rc.1`, which becomes a pre-release) runs the release workflow, which tests, publishes, zips, writes
`SHA256SUMS.txt` and attests the build.

| Folder | What's in it |
|---|---|
| `src/AiKeepAwake` | The hook, watcher, lock point, install and commands |
| `src/AiKeepAwake.Console` | `ai-keep-awake.exe` |
| `src/AiKeepAwake.Background` | `ai-keep-awake-background.exe` |
| `..\background-tools-lib\src\WinBackground` | The Windows calls: power requests, settings, Task Scheduler, notifications, locks, the log (with its own tests, in that repo) |
| `tests/AiKeepAwake.Tests` | xUnit tests, with fakes for Windows, Claude Code and the clock; a few run the built programs in a second process |
