import base64
import json
import os
import struct
import subprocess
import sys
import time

ROOT_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
TEST_INSTALL_DIR = os.path.join(ROOT_DIR, "local", "test_install")
CUPHEAD_EXE = os.path.join(TEST_INSTALL_DIR, "Cuphead.exe")
HOST_SAVE_DIR = os.path.join(ROOT_DIR, "local", "test_saves", "host")
LOG_FILE = os.path.join(TEST_INSTALL_DIR, "CupheadCoop.log")
REQ_FILE = os.path.join(TEST_INSTALL_DIR, "coop_bridge_req.txt")
RESP_FILE = os.path.join(TEST_INSTALL_DIR, "coop_bridge_resp.json")


def bridge_cmd(cmd: str, timeout: float = 5.0) -> dict:
    if os.path.exists(RESP_FILE):
        try:
            os.remove(RESP_FILE)
        except OSError:
            pass

    tmp_req = REQ_FILE + ".tmp"
    with open(tmp_req, "w", encoding="utf-8") as f:
        f.write(cmd + "\n")
    os.replace(tmp_req, REQ_FILE)

    deadline = time.time() + timeout
    while time.time() < deadline:
        if os.path.exists(RESP_FILE):
            time.sleep(0.03)
            with open(RESP_FILE, "r", encoding="utf-8") as f:
                raw = f.read().strip()
            try:
                os.remove(RESP_FILE)
            except OSError:
                pass
            if raw:
                return json.loads(raw)
        time.sleep(0.05)

    raise TimeoutError(f"Bridge command timed out: {cmd}")


def build_handshake_payload(epoch: int = 1, rng_seed: int = 424242) -> bytes:
    return struct.pack("<BIIiiiii", 1, 1, epoch, rng_seed, 1456773641, 0, 1456815409, 1522153206)


def build_achievement_sync_payload(ach_id: str) -> bytes:
    encoded = ach_id.encode("utf-8")
    assert len(encoded) < 128
    return struct.pack("<BBB", 8, 1, len(encoded)) + encoded + struct.pack("<if", 0, 0.0)


def build_player_state_payload(tick: int, player_id: int, pos_x: float, pos_y: float) -> bytes:
    # PacketType.PlayerState = 4
    # uint Tick, byte PlayerId, float PosX, float PosY, float VelX, float VelY,
    # sbyte LookX, sbyte LookY, sbyte MoveX, sbyte MoveY, ushort Flags, byte Health, byte HealthMax, float SuperMeter, int WeaponId
    return struct.pack(
        "<BIBffffbbbbHBBfi",
        4,
        tick,
        player_id,
        pos_x,
        pos_y,
        0.0,
        0.0,
        1,
        0,
        1,
        0,
        1,  # Grounded
        3,
        3,
        1.0,
        1456773641,
    )


def build_weapon_action_payload(player_id: int, action_type: int, weapon_id: int = 1456773641) -> bytes:
    # PacketType.WeaponAction = 5, byte playerId, byte action, int weaponId, float aimX, float aimY
    return struct.pack("<BBBiff", 5, player_id, action_type, weapon_id, 1.0, 0.0)


def main() -> int:
    if not os.path.exists(CUPHEAD_EXE):
        print(f"FAIL: {CUPHEAD_EXE} not found")
        return 1

    for path in (LOG_FILE, REQ_FILE, RESP_FILE):
        if os.path.exists(path):
            try:
                os.remove(path)
            except OSError:
                pass

    os.makedirs(HOST_SAVE_DIR, exist_ok=True)

    args = [
        CUPHEAD_EXE,
        "-screen-fullscreen", "0",
        "-screen-width", "640",
        "-screen-height", "360",
        "-coop-save-dir", HOST_SAVE_DIR,
        "-coop-test-mode",
        "-coop-host-udp", "26891",
    ]

    print("[QA] Launching isolated Cuphead test instance (1.3.4)...")
    proc = subprocess.Popen(args, cwd=TEST_INSTALL_DIR)

    try:
        # 1. Wait for in-game CupheadCoop Bridge to respond
        status = None
        deadline = time.time() + 20.0
        while time.time() < deadline:
            if proc.poll() is not None:
                print(f"FAIL: Cuphead.exe exited early with code {proc.returncode}")
                return 1
            try:
                status = bridge_cmd("status", timeout=1.0)
                if status.get("ok") and status.get("scene") in ("scene_start", "scene_title", "scene_slot_select"):
                    break
            except Exception:
                time.sleep(0.3)

        if not status or not status.get("ok"):
            print("FAIL: Timed out waiting for in-game CupheadCoop Bridge")
            return 1

        print(f"[QA] Step 1 PASS — In-game bridge online: {json.dumps(status)}")
        assert status["role"] == "Host", f"Expected role=Host, got {status['role']}"

        # 2. Inject Co-op Peer Handshake packet and verify Multiplayer activation
        hs_b64 = base64.b64encode(build_handshake_payload(1, 424242)).decode("ascii")
        res = bridge_cmd(f"inject_packet {hs_b64}")
        assert res.get("ok") is True

        status = bridge_cmd("status")
        print(f"[QA] Step 2 PASS — Status after Peer Handshake: {json.dumps(status)}")
        assert status["connected"] is True, "Expected connected=true after peer Handshake"
        assert status["multiplayer"] is True, "Expected PlayerManager.Multiplayer=true"

        # 3. Trigger local achievement ("DefeatBoss") and verify CoopOnlineInterface intercepts it
        res = bridge_cmd("unlock_achievement DefeatBoss")
        assert res.get("ok") is True
        status = bridge_cmd("status")
        print(f"[QA] Step 3 PASS — Local achievement intercepted: {json.dumps(status)}")
        assert status["lastAchievement"] == "DefeatBoss"
        assert status["achievementsSynced"] == 1

        # 4. Inject remote partner AchievementSync packet ("DefeatSaltbaker") and verify local unlock
        ach_b64 = base64.b64encode(build_achievement_sync_payload("DefeatSaltbaker")).decode("ascii")
        res = bridge_cmd(f"inject_packet {ach_b64}")
        assert res.get("ok") is True

        status = bridge_cmd("status")
        print(f"[QA] Step 4 PASS — Remote partner achievement synced locally: {json.dumps(status)}")
        assert status["lastAchievement"] == "DefeatSaltbaker"
        assert status["achievementsSynced"] == 2

        # 5. Load Boss Level ("Veggies" — The Root Pack) in Multiplayer Co-op and verify both players spawn
        res = bridge_cmd("load_level Veggies")
        assert res.get("ok") is True

        players = None
        lvl_deadline = time.time() + 15.0
        while time.time() < lvl_deadline:
            try:
                status = bridge_cmd("status", timeout=2.0)
                if status.get("scene") == "scene_level_veggies" and status.get("p2Exists") is True:
                    players = bridge_cmd("get_players", timeout=2.0)
                    if players.get("p1Exists") and players.get("p2Exists"):
                        break
            except Exception:
                pass
            time.sleep(0.4)

        assert players and players.get("p1Exists") and players.get("p2Exists"), f"Expected both P1 and P2 in level_veggies, got {players}"
        print(f"[QA] Step 5 PASS — Both Cuphead (P1) and Mugman (P2) spawned in Boss Level 'scene_level_veggies': {json.dumps(players)}")

        # 6. Inject PlayerTwo State + WeaponAction packets and verify live Puppet Transform & Weapon Firing
        state_b64 = base64.b64encode(build_player_state_payload(100, 1, -125.0, -210.0)).decode("ascii")
        wep_b64 = base64.b64encode(build_weapon_action_payload(1, 1)).decode("ascii")  # BasicStart = 1
        bridge_cmd(f"inject_packet {state_b64}")
        bridge_cmd(f"inject_packet {wep_b64}")

        players_after = bridge_cmd("get_players")
        print(f"[QA] Step 6 PASS — PlayerTwo synced position & shooting in Boss Level: {json.dumps(players_after)}")
        assert abs(players_after["p2x"] - (-125.0)) < 1.0, f"Expected p2x near -125.0, got {players_after['p2x']}"
        assert players_after["p2Shooting"] is True, "Expected p2Shooting=true after WeaponAction BasicStart"

        # 7. Verify Edge Cases (Pause Player2Leave protection, Live LoadoutSync, Shared InventorySync, KingDice & Steal-HP Revive packets)
        edge = bridge_cmd("verify_edge_cases")
        print(f"[QA] Step 7 PASS — Edge-Case Subsystems Verified: {json.dumps(edge)}")
        assert edge.get("ok") is True, f"Expected ok=True, got {edge}"
        assert edge.get("pauseLeaveProtected") is True, "Expected Pause Player2Leave protection to keep Multiplayer=true"
        assert edge.get("loadoutSynced") is True, "Expected real-time LoadoutSync to update PlayerTwo loadout (including Ms. Chalice)"
        assert edge.get("inventorySynced") is True, "Expected InventorySync to mirror unlocked weapons/supers/charms and currency"
        assert edge.get("kdPacketValid") is True, "Expected King Dice board sync packet valid"
        assert edge.get("eventsValid") is True, "Expected Steal-HP & CoinCollected packets valid"

        print("RESULT: PASS")
        return 0

    finally:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=5.0)
            except subprocess.TimeoutExpired:
                proc.kill()


if __name__ == "__main__":
    sys.exit(main())
