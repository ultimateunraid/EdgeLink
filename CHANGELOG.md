# Changelog

Each version's section becomes the notes of its GitHub release, and EdgeLink shows those notes on the Edge before you upgrade. Keep the heading format `## <version>`.

## Unreleased

### Security
- **Updates fail closed:** EdgeLink installs an update only when GitHub published its SHA-256, the setup program is code-signed, and it is signed by the same publisher as the running copy (revocation is checked too). Unsigned copies of EdgeLink no longer install updates themselves; they open the download page instead.
- **You approve every upgrade:** tapping **Upgrade now** on the Edge opens a confirmation on your main screen. Nothing downloads until you click Yes.
- **Local bridge hardened:** EdgeLink only answers requests addressed to `localhost`, `127.0.0.1` or `[::1]` on its own port (blocks DNS-rebinding attacks), and other websites can no longer read your character data.
- **Removed `UpdateToken`** (the repository is public). `UpdateFeed` must now be an `https` address.
- **Release builds:** signing runs in separate jobs that never execute test or build code, all GitHub Actions are pinned, and a version tag fails instead of publishing unsigned unless `ALLOW_UNSIGNED_RELEASE` is set.

### Note for existing installs
Copies installed from unsigned releases cannot update themselves any more. Install the first signed release by hand once; updates after that are automatic again.

## 1.8.0

### New
- **Updates from the Edge:** when a new version is out, tap the gold version tag to see what changed in every version since yours, then choose **Upgrade now**, **Later** or **Skip**. EdgeLink downloads the setup program from GitHub, checks it against GitHub's checksum, installs it and restarts by itself. Type `/reload` in WoW afterwards to load the new addon.
- **Version tag:** the panel shows which version you are running. Tap it to see the widget, companion and addon versions; it turns amber if they don't match.
- **Friends on the map:** group members show as class-coloured dots you can route to, and online friends and Battle.net friends are counted on the zone they are in. `/edgelink friends off` turns it off.

### Changed
- The tray menu's "Update available" item now shows the notes and can install the update too.

## 1.7.0

### New
- Trainers and services from Polestar on the map, with a **Trainer** button per profession and **Buy from** for missing reagents.
- Auction values from Auctioneer as well as Auctionator.
- Header chips: Hearthstone cooldown, rested XP, new mail, repair estimate; **Auto tabs** switch to the matching tab when a vendor, bank, profession window or flight map opens.
- `/edgelink corner` moves the pixel strip to another corner of the game window.
- Gathering loop for learned herb and ore nodes; quests ready to hand in grouped at the top.
