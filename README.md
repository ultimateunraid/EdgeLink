# EdgeLink

Touch panels for **World of Warcraft: Forever** on the **Corsair Xeneon Edge** (2560x720): bags, gear, quests, a live map with routes, professions and session stats.

EdgeLink is free and open source (MIT). It is a hobby project, not affiliated with or endorsed by Blizzard Entertainment, Corsair or Elgato.

## Read this first: how it works, and the risk

WoW addons cannot talk to other programs. EdgeLink gets around that in a way you should understand before installing:

1. The **addon** draws a thin strip of coloured pixels (640x4) in the top-left corner of the game window. The strip encodes your bags, quests, position and so on.
2. The **companion app** (`EdgeLink.exe`, in the system tray) captures only that strip from the screen, decodes it, and serves it on `localhost:8787`.
3. The **iCUE widget** on the Edge shows the data.

Optionally, taps on the Edge can do things in WoW (track a quest, set a map waypoint, open a window). That works by the companion sending a short burst of Ctrl+Shift+F-key presses that the addon understands. It is **off by default**; you turn it on in the tray menu.

What EdgeLink does **not** do: read game memory, inject code, touch network traffic, move your character, or do anything in combat. Game data is only published out of combat, and one tap does one interface action.

Even so, a program that reads the game window and can send keys is a grey area under Blizzard's terms, and Blizzard decides what is acceptable. **Use it at your own risk.**

## Requirements

- Windows 10 or 11 with .NET Framework 4.8 (included in Windows)
- WoW Forever
- Corsair iCUE 5.47 or newer and a Xeneon Edge
- The game window must be visible on a monitor (the strip has to be on screen)

Optional addons: **Auctionator** (auction values for your bags) and **TomTom** (its waypoints appear on the Edge map).

## Install

1. Download the latest release and unzip it anywhere.
2. Run `EdgeLink.exe`. It appears in the system tray.
3. Right-click the tray icon:
   - **Install / update WoW addon** copies the addon into your WoW folder.
   - **Install widget in iCUE** opens the widget package; add it to the Edge at full width.
4. Log in to WoW (or type `/reload`).

Releases are not code-signed yet, so Windows SmartScreen or your antivirus may warn about or quarantine `EdgeLink.exe`. See [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md). You can also build it yourself (below).

## What you get

| Tab | Contents |
|---|---|
| Bags | Every bag on one screen, vendor and auction value, junk total, and an all-characters view of bags and bank |
| Gear | Equipped items with durability, and your stats |
| Quests | Quest log by zone, with Track and Route buttons |
| Map | Zone and continent maps with your live position, flight points, points of interest, learned herb and ore nodes, rare alerts, coordinates entry, and routes |
| Professions | Skill levels, recipes with how many you can make from your bags and bank, and materials |
| Session | XP and gold per hour, time to level, gathering counts, FPS, latency, durability, reputations |

Routes show a line from you to the target with distance and turn direction. "Suggested path" follows trails you have walked before and proposes flight legs for long trips.

Some data can only be read while a game window is open: open your **bank** once and each **profession window** once, and EdgeLink remembers them.

## Slash commands

`/edgelink on|off|refresh|areas|nodes [clear]|casts|rate <seconds>|cmd <digits>`

## Privacy

Everything stays on your PC, with two exceptions, both optional (set `Icons=false` in `EdgeLink.ini` to turn them off):

- **Item icons:** item ID numbers are sent to Wowhead to look up icon images.
- **Map art:** zone names are sent to Wowhead to look up map images.

No account names, character names or other personal data are sent anywhere. The companion only listens on `localhost`.

Saved data: `%LOCALAPPDATA%\EdgeLink` (log, image cache, learned trails, per-character inventories) and the addon's `EdgeLink.lua` SavedVariables file.

## Build from source

Needs only Windows and Node.js (for packaging the widget):

```
companion\build.cmd
```

Output goes to `dist\EdgeLink\`. The script uses the C# compiler that ships with Windows; the code is C# 5 for that reason.

Tests (Python 3 with `lupa` and `pillow`):

```
python tests/roundtrip.py
python tests/input_test.py
```

## Layout

| Path | What |
|---|---|
| `addon/EdgeLink/` | The WoW addon (Lua) |
| `companion/` | Tray app and bridge (C#), build script, optional PowerShell launcher |
| `widget/` | The iCUE widget (HTML, CSS, JavaScript) |
| `tests/` | Protocol and input-decoder tests |

## Troubleshooting

- **Widget says "WoW not running" or "Waiting for addon":** the game window must be on screen and not minimised, with the addon enabled. Loading screens and a hidden interface (Alt+Z) pause the data.
- **A tap does nothing in WoW:** check the tray option is ticked, and that the chat box is not open; key presses are ignored while you are typing.
- **No herbs or ore on the map:** nodes are learned as you gather. `/edgelink casts` shows what the addon saw.
- **No recipes or bank items:** open the profession window or the bank once.

## Licence

[MIT](LICENSE). World of Warcraft is a trademark of Blizzard Entertainment, Inc. Map and icon images are fetched from Wowhead at run time and are not part of this repository.
