# Explorer Session

**English** | [繁體中文](README.zh-TW.md)

Brings your File Explorer windows and tabs back after a crash, an unclean shutdown, or an accidental close.

## Contents

- [Why this exists](#why-this-exists)
- [Getting started](#getting-started)
- [Usage](#usage)
- [How it works](#how-it-works)
- [Known limitations](#known-limitations)
- [Development](#development)
- [Getting help](#getting-help)
- [License](#license)

---

## Why this exists

**When you juggle several projects at once, your open folders are your to-do list, and Windows can throw that list away without warning.**

Picture an ordinary afternoon. One window holds the client deck, another the data pipeline with five subfolder tabs, and somewhere at the back sits the side project you haven't touched since Tuesday. Then File Explorer crashes. In our case it had been running for two weeks and quietly leaked graphics resources until it fell over. The taskbar flickers, and every folder window is gone.

- **The urgent work comes back**, because you remember it and dig the folders up again.
- **The side project doesn't come back**, because that open window was the only reminder it existed. Out of sight, out of mind.

Windows does have "Restore previous folder windows at logon", but it only kicks in after a clean restart or sign-out. A crash, a power cut, running out of memory, or one careless click on ✕ all leave you with nothing.

**Explorer Session closes that gap:**

| What happened | Windows on its own | With Explorer Session |
|---|---|---|
| Clean restart, shutdown or sign-out | ✅ Restored | ✅ Restored (Windows does it; this tool stays out of the way) |
| File Explorer crashes or restarts | ❌ Everything gone | ✅ Restored automatically within seconds |
| Unclean shutdown (out of memory, power loss) | ❌ Everything gone | ✅ Restored automatically after sign-in |
| You close a window or tab by mistake | ❌ Gone | ✅ One click on the tray icon, like Ctrl+Shift+T in a browser |
| "What did I have open yesterday morning?" | ❌ No way to know | ✅ Pick a point in time from the history menu |

---

## Getting started

**Build it once, start it once, and from then on it runs by itself at every sign-in.**

**Requirements**: Windows 11 with tabbed File Explorer (developed on 25H2). Nothing to install: it builds with the .NET Framework compiler that ships with Windows.

```powershell
git clone https://github.com/chuanchihsu1219/explorer-session.git
cd explorer-session
powershell -ExecutionPolicy Bypass -File .\build.ps1
explorer.exe "$PWD\bin\ExplorerSession.exe"
```

- A File Explorer icon appears in the system tray. It may land in the hidden area (`^`); drag it onto the taskbar if you want it always visible.
- The first run turns on **Start at sign-in** and adds a **Restore File Explorer** shortcut to the Start menu.
- That's it. There is nothing to configure.

---

## Usage

**You don't have to change how you work: the tray icon is the only entry point, and if it is in the tray, recording is running.**

### What you'll see in each situation

| Situation | What you see | What you do |
|---|---|---|
| File Explorer crashes or restarts | The taskbar flickers. A few seconds later your windows reopen one by one, with the same tabs, positions and selected tabs, and a notification appears | Nothing. Clicking the taskbar icon first is fine too; you just get one extra Home window |
| Normal restart, shutdown or sign-out | Windows' built-in restore handles it; this tool stays out of the way | Nothing |
| Unclean shutdown (out of memory, power loss, forced shutdown) | About half a minute after sign-in, your windows come back | Nothing |
| You closed a window or tab by mistake | — | **Left-click the tray icon.** Each click reopens one more, like Ctrl+Shift+T in Chrome |
| You want the windows from a certain time | — | Right-click → "Restore from history" → pick an entry |

- **Folders that are already open are always skipped**, so nothing is opened twice.

### Right-click menu

| Item | What it does |
|---|---|
| Last snapshot HH:mm:ss · N windows, M tabs | Status line; if the time keeps moving, recording is running |
| Reopen last closed (same as left click) | Same as left click |
| Recently closed ▸ | The last 20 closed windows or tabs; pick any one |
| Restore from history ▸ | Saved points in time; ⚠ marks "before crash" or "before unclean shutdown" |
| Pause recording | Stop recording for a while, e.g. while you tidy up lots of windows |
| Start at sign-in | **On by default.** Runs in the background after you sign in, and keeps the "Restore File Explorer" shortcut in the Start menu. Untick it to stop starting automatically |
| Open data folder | Opens the runtime folder, where you can read `log.txt` |
| Exit | Closes the tool and records a clean exit |

### If the tool gets closed

**Press Start, search for "Restore File Explorer", and click it. That starts the tool again.**

**How to tell it is not running**
- The tray icon is gone. It may be in the hidden area, so expand `^` too.
- The "Last snapshot" time in the right-click menu stops moving.

**Ways to start it again**

| Way | When |
|---|---|
| **Start menu → "Restore File Explorer"** | The quickest way |
| Restart the PC | With "Start at sign-in" ticked, it starts by itself after sign-in |
| Run `bin\ExplorerSession.exe` directly | Fallback if the shortcut was deleted |

- **The same shortcut behaves differently depending on whether the tool is running**:
  - Not running: it starts the tool, without restoring anything.
  - Already running: it acts like a left click on the tray icon and reopens the most recently closed window or tab.
  - Only one copy ever runs, so clicking it can never start a second one.

**What happens while it is closed**
- **Everything already saved is kept**: history and "Recently closed" live on disk and work again once the tool restarts.
- **Nothing is recorded while it is closed**: if Explorer crashes during that time, there is no automatic restore. After restarting the tool, you can still pick the last snapshot from before it was closed under "Restore from history".

---

## How it works

**Every 15 seconds the tool reads all Explorer tabs. Switching folders only updates the current state. History is written only when the set of windows and tabs changes and then stays the same for 2 minutes.**

| Layer | File | What it holds | Used for |
|---|---|---|---|
| Current state | `live.json` | The folder each tab is in **right now**, plus positions and selected tabs | Automatic restore after a crash or unclean shutdown |
| Recently closed | `closed.json` | The last 20 windows or tabs that were really closed | Left click, "Recently closed" |
| History | `history\*.json` | Points where the layout was stable, plus checkpoints before a crash or shutdown | "Restore from history" |

- **Tabs are recognized by the tab itself, not by its path**: each tab has its own internal window handle, which does not change when it navigates. Moving between subfolders is therefore never mistaken for closing a tab, and it does not flood the history.
- **History retention**:

| Age | Kept |
|---|---|
| Under 1 hour | All |
| 1–24 hours | The last one in each 15-minute slot |
| 1–7 days | The last one each day |
| ⚠ Before crash, before unclean shutdown, before shutdown | 30 days |

- **After a crash, only the "before crash" state is restored**: each snapshot records which Explorer run it belongs to. Windows you open after the crash belong to the new run and are never used for the restore.
- **Explorer restarts are detected through the `TaskbarCreated` broadcast**: the tray icon needs that message anyway to re-register, so there is no need to watch the event log.

---

## Known limitations

- **A window closed within 15 seconds of opening** has not been recorded yet, so it cannot be reopened.
- **Search result tabs**: only a display name is available, not a path, so they are skipped.
- **Adding tabs uses an undocumented Explorer command**: if a Windows update breaks it, the remaining tabs open as separate windows and `log.txt` says `new-tab failed`.
- **Two tabs with the same name in one window**: the selected tab or tab order may come back swapped.

---

## Development

| Task | Command (PowerShell in this folder) |
|---|---|
| Rebuild after changing the code | `bin\ExplorerSession.exe --quit`, then `.\build.ps1` |
| Print one snapshot | `bin\ExplorerSession.exe --snapshot-once out.json` |
| Restore from a given file | `bin\ExplorerSession.exe --restore some.json` |
| Simulate an Explorer restart / sign-in after an unclean shutdown | `--simulate-shell-restart` / `--simulate-unclean-logon` (run by the resident instance) |

- **Source** is in `src\` (C#, .NET Framework 4.x, no NuGet packages).
- **Language**: the UI is Traditional Chinese when the Windows display language is zh-TW, zh-HK or zh-MO, and English otherwise. To force one, set the environment variable `EXPLORER_SESSION_LANG=zh` or `en`.
- **Runtime data** lives in `%LOCALAPPDATA%\ExplorerSession\`. It is deliberately not in OneDrive or other synced folders, because it is written every 15 seconds.
- **Start it through `explorer.exe`**:
  - Started from a terminal inside an MSIX-packaged app (such as Claude Desktop), it becomes a child of that app and is killed when the app restarts.
  - Its `AppData` writes are also redirected to that app's private copy.
- **Start at sign-in** writes `HKCU\...\Run\ExplorerSession`. No scheduled task or admin rights needed.

---

## Getting help

- **Bugs and questions**: [open an issue](https://github.com/chuanchihsu1219/explorer-session/issues). Please attach `log.txt` (right-click the tray icon → "Open data folder"), which records every restore and its result.
- **Contributions**: pull requests are welcome. For anything larger than a small fix, open an issue first so we can agree on the approach.

---

## License

[MIT](LICENSE) © 2026 Chuan-Chi Hsu
