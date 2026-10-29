# Explorer Session

**English** | [繁體中文](README.zh-TW.md)

**A Windows tray tool that brings your File Explorer windows and tabs back exactly as they were, after Explorer crashes, the PC shuts down uncleanly, or you close a window by mistake. You don't have to change how you work.**

- **Crash or unclean shutdown**: restored automatically, nothing to click.
- **Closed something by mistake**: click the tray icon once.
- **Want an earlier layout back**: pick a point in time from the right-click menu.

Windows' own "Restore previous folder windows at logon" only covers a clean sign-out or shutdown. This tool covers everything else.

---

## 1. What you'll see in each situation

**The File Explorer icon in the system tray is the only entry point. If it is in the tray, recording is running.**

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
| Start at sign-in | **On by default.** Runs in the background after you sign in, and adds a "Restore File Explorer" shortcut to the Start menu. Untick it to stop starting automatically |
| Open data folder | Opens the runtime folder, where you can read `log.txt` |
| Exit | Closes the tool and records a clean exit |

---

## 2. If the tool gets closed

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

## 3. How recording works: "current state" and "history" are separate

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

---

## 4. Known limitations

- **A window closed within 15 seconds of opening** has not been recorded yet, so it cannot be reopened.
- **Search result tabs**: only a display name is available, not a path, so they are skipped.
- **Adding tabs uses an undocumented Explorer command**: if a Windows update breaks it, the remaining tabs open as separate windows and `log.txt` says `new-tab failed`.
- **Two tabs with the same name in one window**: the selected tab or tab order may come back swapped.

---

## 5. Install and maintenance

| Task | Command (PowerShell in this folder) |
|---|---|
| First use | `.\build.ps1`, then `explorer.exe "$PWD\bin\ExplorerSession.exe"`. The first run turns on "Start at sign-in" |
| Rebuild after changing the code | `bin\ExplorerSession.exe --quit`, then `.\build.ps1` |
| Print one snapshot | `bin\ExplorerSession.exe --snapshot-once out.json` |
| Restore from a given file | `bin\ExplorerSession.exe --restore some.json` |
| Simulate an Explorer restart / sign-in after an unclean shutdown | `--simulate-shell-restart` / `--simulate-unclean-logon` (run by the resident instance) |

- **Language**: the UI is Traditional Chinese when the Windows display language is zh-TW, zh-HK or zh-MO, and English otherwise. To force one, set the environment variable `EXPLORER_SESSION_LANG=zh` or `en`.
- **No SDK needed**: it builds with the .NET Framework `csc.exe` that ships with Windows. Source is in `src\`.
- **Requirements**: Windows 11 with tabbed File Explorer (developed on 25H2).
- **Runtime data** lives in `%LOCALAPPDATA%\ExplorerSession\`. It is deliberately not in OneDrive, because it is written every 15 seconds.
- **Start it through `explorer.exe`**:
  - Started from a terminal inside an MSIX-packaged app (such as Claude Desktop), it becomes a child of that app and is killed when the app restarts.
  - Its `AppData` writes are also redirected to that app's private copy.
- **Start at sign-in** writes `HKCU\...\Run\ExplorerSession`. No scheduled task or admin rights needed.
- **Explorer restarts are detected through the `TaskbarCreated` broadcast**: the tray icon needs that message anyway to re-register, so there is no need to watch the event log.
