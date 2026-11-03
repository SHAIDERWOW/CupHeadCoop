# CupheadCoop — Native Online Co-op + Steam Achievements Mod

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Steam%20(PC)-orange.svg)](https://store.steampowered.com/app/268910/Cuphead/)
[![Game Version](https://img.shields.io/badge/Cuphead-v1.3.4%20%2B%20DLC-brightgreen.svg)]()
[![Releases](https://img.shields.io/github/v/release/SHAIDERWOW/CupHeadCoop?color=red)](https://github.com/SHAIDERWOW/CupHeadCoop/releases)

[**Português (Brasil)**](README_PT.md) | **English**

> **A 100% clean-room, native online multiplayer mod for Cuphead (Steam version).**  
> Play the entire base campaign and *The Delicious Last Course* DLC together with a friend over the internet without Steam Remote Play Together lag, without Hamachi/Radmin, and with **full Steam Achievements & progression synchronized for BOTH players**!

---

## 🎮 Highlights & Features

- **🏆 Full Steam Achievements & Stats Sync (`CoopOnlineInterface`):**
  - All 42 Steam Achievements (Base Game + DLC) unlock for **both Host and Client** in real-time.
  - Beat a boss as Ms. Chalice, buy items in Porkrind's Emporium, or defeat secret bosses — achievements and progression mirror to both players' Steam accounts!
- **⚡ 0ms Input Latency (Local Authority):**
  - Unlike Steam Remote Play Together (which streams compressed video with high input lag), both players run Cuphead locally on their own PCs. Your jumps, parries, dashes, and shots respond instantly (0ms latency).
- **🌐 Built-in Steam P2P (No Port Forwarding):**
  - Connect directly through your Steam Friends list or by sharing a 1-click Lobby Code with hotkeys (`F5` / `F6`). No VPNs, no router port forwarding.
  - Also includes a Direct UDP mode for offline LAN play.
- **🌐 Dynamic Regionalization (PT-BR / EN) & Update Checker:**
  - Automatically matches your in-game language setting (Portuguese or English) across all menus and notices, with a manual quick-toggle button in the `F9` overlay.
  - Silent, non-intrusive background check informs you when a new mod release is available on GitHub without impacting performance.
- **🔄 Complete Gameplay Synchronization:**
  - **Symmetric Level-Start Barrier (`LevelReadyGate`):** If one player's PC loads a boss stage faster than the other, the fight waits until both players finish loading so both timelines start on the exact same millisecond.
  - **Shared In-Level Pause (`PauseSync`):** Pausing or unpausing during a level pauses/unpauses both players' screens simultaneously.
  - **Two-Way Scene & Retry Controls (`SceneRequestSync`):** Either player (Host or Client) can trigger Retry/Restart, Return to Map, or enter a stage from the World Map.
  - **In-Memory World Map & Dialoguer Mirror (`WorldSaveMirror`):** Mirrors the Host's World Map state (bridges, boss completion flags, NPC dialogues via `Dialoguer`) in the Client's RAM while protecting the Client's personal local save files on disk from being overwritten.
  - **Bosses, Properties & Knockout Gate:** Synchronizes boss `AbstractLevelProperties` HP, timeline progress, phase transitions, deterministic RNG seeds, and a synchronized `KNOCKOUT!` gate (`LevelOutcomeSync`).
  - **Run & Gun Stages:** Coins (`LevelCoin`) collected by either player are awarded to both (`5/5`).
  - **King Dice Casino:** Board marker position, bonus hearts (`HEART_INDEXES`), and dice parry rolls (`DicePalaceMainLevelDice`) are strictly synced.
  - **Revive Mechanics:** Both ghost pink-heart parry revives (`OnParrySwitch`) and top-of-screen Steal-1-HP revives (`ReviveOutOfFrame`) work seamlessly online.
  - **Plane Levels (Shmup):** Syncs shooting, bombs, EX moves, parries, and mini-plane shrink state (`Shrink_In` / `Shrink_Out`).
  - **Real-Time Loadout & Inventory Mirroring:** Changing weapons or charms (e.g. Astral Cookie, Smoke Bomb, Cursed Relic) on the World Map or Game Over screen instantly updates for both players.
- **🛡️ Anti-Softlock, DLC & Connection Protections:**
  - **DLC & Version Handshake Guard:** Automatically checks mod version compatibility and whether both players have *The Delicious Last Course* DLC installed, preventing infinite loading screens or crashes if one player lacks the DLC.
  - **Cold-Launch Steam Invites (`+connect_lobby`) & Auto-Reconnect:** Supports accepting Steam invites even while Cuphead is closed (waiting until local saves initialize before joining) and automatically reconnects up to 3 times if the P2P connection drops briefly.
  - Fixes vanilla multi-player menu soft-locks when closing the Equip Card (`MapEquipUI`) or exiting Porkrind's Shop (`ShopScene`).
  - Blocks accidental "Remove Player 2" clicks in the Pause menu during online sessions.
- **🧹 Clean & 100% Reversible:**
  - Built on Unity Doorstop. **Never modifies or overwrites your original game files**.
  - Effortless uninstallation: simply delete the 3 mod files to return to 100% vanilla anytime.

---

## 📥 Installation

> **Note:** Both you and your friend must have a legitimate copy of Cuphead on Steam.

1. Download the latest `CupheadCoop-v1.1.0.zip` from the [Releases](https://github.com/SHAIDERWOW/CupHeadCoop/releases) page.
2. Extract the contents of the `.zip` directly into your Cuphead game folder:
   - Default Steam path: `C:\Program Files (x86)\Steam\steamapps\common\Cuphead\`
3. Verify that the following files are present in the folder next to `Cuphead.exe`:
   - `winhttp.dll`
   - `doorstop_config.ini`
   - `CupheadCoop.dll`
4. Start Cuphead normally through Steam!

---

## 🕹️ How to Play Online

1. Launch Cuphead on Steam (both Host and Client).
2. Press **`F9`** on the Title Screen or World Map to open the in-game **CupheadCoop Overlay**:
   - The overlay shows your Steam API connection, ping, role, and lobby options.

### Option A: Steam Friend Lobby (Recommended — Easiest)
- **Host:** Press **`F5`** (or click **"1. CRIAR SALA STEAM - Host"**). Then press **`F6`** to copy the lobby code or click **"Convidar na Steam"** to invite your friend directly through the Steam Overlay.
- **Client:** Copy the lobby code your friend sent you, press **`F6`** (or click **"Colar+Entrar"**) in the `F9` menu.
- **Ready!** Load into any save file. Player 2 spawns automatically as Mugman / Ms. Chalice!

### Option B: Direct UDP (LAN / Offline)
- Host selects a port (default: `26891`) and clicks **Host UDP**.
- Client types the Host's local IP and clicks **Join UDP**.

---

## 🗑️ How to Uninstall

If you or your friend want to remove the mod and return to pure vanilla Cuphead:
1. Open your Cuphead game folder and simply delete the 3 mod files:
   - `winhttp.dll`
   - `doorstop_config.ini`
   - `CupheadCoop.dll`
2. That's it! Your original game files, saves, and DLC remain 100% untouched.

---

## ⌨️ Hotkeys & Shortcuts

| Key | Action |
| :---: | :--- |
| **`F9`** | Toggle in-game Mod Overlay & Mouse Cursor |
| **`F5`** | Create Steam Lobby (when idle) / Open Steam Invite Overlay (when hosting) |
| **`F6`** | Paste Lobby Code & Join (when idle) / Copy Lobby Code to Clipboard (when hosting) |

---

## 🐛 Bug Reporting & Diagnostics

If you encounter any edge cases or desyncs during a boss fight:
1. Locate `CupheadCoop.log` inside your Cuphead installation folder right after the session.
2. Ask your co-op partner to grab their `CupheadCoop.log` as well.
3. Open an issue on [GitHub Issues](https://github.com/SHAIDERWOW/CupHeadCoop/issues) and attach both log files.

---

## 🏗️ Building from Source

This mod is 100% clean-room C# code targeting .NET 3.5 / Unity Mono.

### Prerequisites
- Windows 10/11
- .NET Framework 4.0+ compiler (`csc.exe` in `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\`)
- A legitimate Cuphead Steam install (to reference the game's public assembly interfaces)

### Build Command
Run the following in PowerShell from the project root:

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /noconfig /target:library /optimize+ /nostdlib /out:build\CupheadCoop.dll `
  /r:<managed_dir>\mscorlib.dll `
  /r:<managed_dir>\System.dll `
  /r:<managed_dir>\System.Core.dll `
  /r:<managed_dir>\UnityEngine.dll `
  /r:<managed_dir>\UnityEngine.CoreModule.dll `
  /r:<managed_dir>\UnityEngine.IMGUIModule.dll `
  /r:<managed_dir>\UnityEngine.UI.dll `
  /r:<managed_dir>\UnityEngine.AudioModule.dll `
  /r:<managed_dir>\UnityEngine.Physics2DModule.dll `
  /r:<managed_dir>\UnityEngine.AnimationModule.dll `
  /r:<managed_dir>\Assembly-CSharp.dll `
  /r:<managed_dir>\Assembly-CSharp-firstpass.dll `
  /r:<managed_dir>\Rewired_Core.dll `
  src\CupheadCoop\*.cs
```

---

## 🧪 Automated QA Testing

We include a comprehensive, automated test runner ([`tests/run_automated_qa.py`](tests/run_automated_qa.py)) that launches an isolated game client in a sandbox and verifies 7 major multiplayer assertions:
1. Doorstop injection & in-game RPC IPC bridge online
2. Network handshake & 2-player multiplayer mode activation
3. Local Steam achievement interception
4. Remote peer Steam achievement synchronization
5. Dual-player spawning in level scenes (`scene_level_veggies`)
6. Real-time network puppet transform & continuous shooting replication
7. Edge-case subsystems verification (anti-softlock, loadout sync, inventory sync, King Dice packets)

To execute the test suite:
```powershell
python tests/run_automated_qa.py
```

---

## 📜 License & Acknowledgments

- Released under the [MIT License](LICENSE).
- Powered by [Unity Doorstop](https://github.com/NeighbourhoodRevival/UnityDoorstop).
- Research & guidelines inspired by [coop-anything](https://github.com/cranebeaker/coop-anything).
- Cuphead is a registered trademark of **Studio MDHR Entertainment Inc.** This project is an independent open-source modification and is not affiliated with or endorsed by Studio MDHR.
