# Cuphead (2017)

| | |
|---|---|
| Engine | Unity 2017.4.9f1 (64-bit, Mono `.NET 3.5` runtime, Rewired input, `Steamworks.NET`) |
| Versions tested | Steam Build ID `20241530` (`v1.3.4` + *The Delicious Last Course* DLC, `Cuphead.exe` SHA-256 `C5FFFD221234EA520B9B5D545D9FFF65EBA497A0CE1B852334D293770D7EE02D`) |
| Projects | `CupheadCoop` (`src/CupheadCoop/`) — clean-room online co-op + Steam achievement sync mod |
| QA level | `automated` |
| Last updated | 2026-10-23 |

## Where things stand

Cuphead ships with 2-player local co-op only; when played over Steam Remote Play Together, the guest streams video from the host, suffers video/input latency, and **cannot unlock Steam achievements or save progression on their own Steam account**. `CupheadCoop` (`src/CupheadCoop/`) adds native peer-to-peer online co-op (via Steamworks P2P Lobbies and Direct UDP) with full **Steam achievement and stat synchronization** (`CoopOnlineInterface`) and 0ms local input routing for both players.

## Existing work

- **Official Local Co-op & Steam Remote Play Together:** Built-in 2-player local co-op (`PlayerId.PlayerOne` and `PlayerId.PlayerTwo`). Remote Play Together streams video from Host to Client; only the Host runs the game executable or earns Steam achievements.
- **`CupheadCoop` (`src/CupheadCoop/`):** Built from zero against the game's own managed assemblies (`Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`, `Rewired_Core.dll`) loaded directly via Unity Doorstop (`Doorstop.Entrypoint.Start()`), with zero external mod-loader dependencies.

## How the game works (for co-op)

- **Managed Layer (`Cuphead_Data/Managed/Assembly-CSharp.dll`):**
  - `PlayerManager` manages `PlayerId.PlayerOne` (0) and `PlayerId.PlayerTwo` (1) via `PlayerSlot[] playerSlots` and `Dictionary<int, Rewired.Player> playerInputs`.
  - Gameplay controllers (`LevelPlayerController`, `PlanePlayerController`, `MapPlayerController`) read input from `PlayerInput.actions` (`Rewired.Player`).
  - Scene transitions go through `SceneLoader.LoadScene` / `SceneLoader.LoadLevel`. Boss HP and phase markers live on `Level.Current.timeline` (`Level.Timeline`).
- **Steamworks & Achievements (`Assembly-CSharp-firstpass.dll` & `Assembly-CSharp.dll`):**
  - `Assembly-CSharp-firstpass.dll` ships full `Steamworks.NET` (`SteamMatchmaking`, `SteamNetworking`, `SteamFriends`, `SteamUserStats`).
  - All achievements and stats in Cuphead route through `OnlineManager.Instance.Interface` (`OnlineInterfaceSteam`) and `LocalAchievementsManager`.

## What we changed

1. **Steam Achievement & Stat Synchronization (`CoopOnlineInterface`):**
   - **Root cause:** In online co-op, level win checks (`Level::zHack_OnWin`), shop purchases (`ShopSceneItem::Purchase`), secret paths (`MapSecretAchievementUnlocker::OnTriggerEnter2D`), and Mausoleum wins trigger locally, and several DLC checks (`DefeatBossAsChalice`, `DefeatXBossesAsChalice`) only inspect `PlayerId.PlayerOne`.
   - **Fix:** `CoopOnlineInterface` wraps `OnlineManager.Instance.Interface` (`<Interface>k__BackingField`), executes every `UnlockAchievement`, `SetStat`, `IncrementStat`, and `SyncAchievementsAndStats` locally on Steam (`OnlineInterfaceSteam` + `LocalAchievementsManager`), and broadcasts `PacketType.AchievementSync` reliably to the remote peer. `AuditCoopPostWinAchievements()` also checks `PlayerId.PlayerTwo` for Ms. Chalice boss completion achievements.
   - **Test:** `tests/run_automated_qa.py` Steps 3 & 4 verify both local achievement interception (`DefeatBoss`) and remote partner achievement unlock (`DefeatSaltbaker`) inside a live `v1.3.4` instance.
2. **Zero-Latency Local Input Routing + Puppet Isolation (`CoopSyncController`):**
   - **Root cause:** Forwarding raw button presses over the internet adds network RTT to jumps, dashes, and parries, making Cuphead's tight platforming/boss fights feel sluggish and causing physics drift.
   - **Fix:** On the Client, `PlayerTwo.input.actions` (`<actions>k__BackingField`) is bound to `ReInput.players.GetPlayer(0)` (the Client's local keyboard/controller), giving the Client 0ms local control over Mugman/Ms. Chalice, while `PlayerOne` is bound to `ReInput.players.GetPlayer(1)` with `motor.DisableInput()` and driven by Host state packets.
   - **Test:** `tests/run_automated_qa.py` Steps 5 & 6 verify both players spawning in `scene_level_veggies` and remote `PlayerTwo` transform/shooting replication.
3. **Unity Doorstop Mono JIT Early-Init Guard (`Entrypoint.cs`):**
   - **Root cause:** Unity Doorstop invokes `Doorstop.Entrypoint.Start()` right after `mono_jit_init_version`, before `UnityEngine.dll` is loaded. Any direct reference to `UnityEngine` inside `Start()` or methods it calls synchronously fails Mono JIT compilation.
   - **Fix:** Marked `AttachUnityHooks()` and `OnSceneLoaded()` with `[MethodImpl(MethodImplOptions.NoInlining)]` and deferred them until `AppDomain.CurrentDomain.AssemblyLoad` observes `"Assembly-CSharp"`.

## QA

Run the automated in-game QA suite (launches an isolated `v1.3.4` test instance with redirected saves and `-coop-test-mode` so the owner's Steam install and saves are untouched):

```powershell
python tests/run_automated_qa.py
```

Last suite result (`2026-10-23`, 6/6 checks passed):

```text
[QA] Launching isolated Cuphead test instance (1.3.4)...
[QA] Step 1 PASS — In-game bridge online: {"ok": true, "role": "Host", "connected": false, "scene": "scene_start", "multiplayer": false, "p2Exists": false, "tick": 0, "seed": 1337, "rttMs": 0.0, "achievementsSynced": 0, "lastAchievement": "None"}
[QA] Step 2 PASS — Status after Peer Handshake: {"ok": true, "role": "Host", "connected": true, "scene": "scene_start", "multiplayer": true, "p2Exists": false, "tick": 6, "seed": 10768622, "rttMs": 0.0, "achievementsSynced": 0, "lastAchievement": "None"}
[QA] Step 3 PASS — Local achievement intercepted: {"ok": true, "role": "Host", "connected": true, "scene": "scene_start", "multiplayer": true, "p2Exists": false, "tick": 17, "seed": 10768622, "rttMs": 0.0, "achievementsSynced": 1, "lastAchievement": "DefeatBoss"}
[QA] Step 4 PASS — Remote partner achievement synced locally: {"ok": true, "role": "Host", "connected": true, "scene": "scene_start", "multiplayer": true, "p2Exists": false, "tick": 27, "seed": 10768622, "rttMs": 0.0, "achievementsSynced": 2, "lastAchievement": "DefeatSaltbaker"}
[QA] Step 5 PASS — Both Cuphead (P1) and Mugman (P2) spawned in Boss Level 'scene_level_veggies': {"ok": true, "p1Exists": true, "p2Exists": true, "p1x": -370.0, "p1y": -240.0, "p2x": -535.0, "p2y": -240.0, "p2Shooting": false}
[QA] Step 6 PASS — PlayerTwo synced position & shooting in Boss Level: {"ok": true, "p1Exists": true, "p2Exists": true, "p1x": -370.0, "p1y": -250.0, "p2x": -125.0, "p2y": -250.0, "p2Shooting": true}
RESULT: PASS
```

## How to play

1. Copy `build/CupheadCoop.dll`, `winhttp.dll` (Unity Doorstop), and `doorstop_config.ini` (`target_assembly = CupheadCoop.dll`) into the Cuphead root folder (both Host and Client).
2. Launch Cuphead through Steam and press **`F9`** to toggle the Co-op overlay.
3. **Steam P2P (No Port Forwarding):** Host clicks **Host Steam Friend Lobby**, then **Invite Friend via Steam Overlay** (or Client joins via Steam Friend list / Direct UDP).
4. Load into your save slot; Player 2 joins automatically and all Steam achievements and save progression sync to both players.

## Known issues

- Boss projectile sub-patterns that compute target angles directly from local player positions rather than `UnityEngine.Random` can diverge visually if latency is high (>120ms), though boss HP (`Level.Timeline.damage`), phase changes, revives, and win states are authoritatively synchronized by the Host.

## Next steps

- Add per-boss projectile/hitbox ownership tags for multi-target bosses (e.g., Werner Werman, Saltbaker) under high-latency conditions using `tools/chaos`.
