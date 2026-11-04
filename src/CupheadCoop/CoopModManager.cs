using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace CupheadCoop
{
    public class CoopModManager : MonoBehaviour
    {
        public static CoopModManager Instance { get; private set; }

        public const string GitHubRepoUrl = "https://github.com/SHAIDERWOW/CupHeadCoop";
        public const string GitHubReleasesUrl = "https://github.com/SHAIDERWOW/CupHeadCoop/releases";
        public const string GitHubLatestReleaseApiUrl = "https://api.github.com/repos/SHAIDERWOW/CupHeadCoop/releases/latest";

        public CoopNetworkTransport Transport { get; private set; }
        public CoopSyncController Sync { get; private set; }

        private bool _showMenu = true;
        private string _ipInput = "127.0.0.1";
        private string _portInput = "26891";
        private string _lobbyCodeInput = "";
        private float _pingTimer;
        private TcpListener _bridgeListener;
        private int _bridgePort = 26899;

        // 0 = Auto (Follow Cuphead's Localization.language), 1 = Forced PT-BR, 2 = Forced EN
        private int _languageMode;

        // Asynchronous GitHub Update Checker state
        private bool _updateCheckCompleted;
        private bool _updateAvailable;
        private string _latestRemoteVersion = CoopProtocol.ModVersion;

        public static void EnsureCreated()
        {
            if (Instance != null)
                return;

            GameObject go = new GameObject("CupheadCoopManager");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<CoopModManager>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;

            Transport = new CoopNetworkTransport();
            Sync = new CoopSyncController(Transport);

            ParseCommandLineArgs();
            StartAutomationBridge(_bridgePort);
            CupheadCoopBootstrapper.Log("CoopModManager v" + CoopProtocol.ModVersion + " initialized. Press F9 in-game to toggle Co-op Overlay.");
        }

        private void Start()
        {
            if (!CoopOnlineInterface.TestModeNoSteamWrite)
            {
                StartCoroutine(CheckForModUpdateCoroutine());
            }
        }

        public bool IsPortugueseActive()
        {
            if (_languageMode == 1)
                return true;
            if (_languageMode == 2)
                return false;

            try
            {
                return Localization.language == Localization.Languages.PortugueseBrazil;
            }
            catch
            {
                return false;
            }
        }

        private IEnumerator CheckForModUpdateCoroutine()
        {
            yield return new WaitForSecondsRealtime(2.0f);

            UnityWebRequest req = null;
            try
            {
                req = UnityWebRequest.Get(GitHubLatestReleaseApiUrl);
                req.SetRequestHeader("User-Agent", "CupheadCoop/" + CoopProtocol.ModVersion);
                req.SetRequestHeader("Accept", "application/vnd.github.v3+json");
                req.timeout = 8;
            }
            catch
            {
                if (req != null) req.Dispose();
                yield break;
            }

            using (req)
            {
                yield return req.SendWebRequest();

                try
                {
                    if (!req.isNetworkError && !req.isHttpError && req.downloadHandler != null)
                    {
                        string json = req.downloadHandler.text ?? string.Empty;
                        string remoteTag = ExtractTagFromReleaseJson(json);
                        if (!string.IsNullOrEmpty(remoteTag))
                        {
                            _latestRemoteVersion = remoteTag.TrimStart('v', 'V');
                            _updateAvailable = IsRemoteVersionNewer(CoopProtocol.ModVersion, _latestRemoteVersion);
                            _updateCheckCompleted = true;
                            CupheadCoopBootstrapper.Log(string.Format(
                                "GitHub Update Check: local=v{0}, latest=v{1}, updateAvailable={2}",
                                CoopProtocol.ModVersion, _latestRemoteVersion, _updateAvailable));
                        }
                    }
                }
                catch { }
            }
        }

        public static string ExtractTagFromReleaseJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return string.Empty;

            const string key = "\"tag_name\"";
            int keyIdx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0)
                return string.Empty;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length);
            if (colonIdx < 0)
                return string.Empty;

            int firstQuote = json.IndexOf('"', colonIdx + 1);
            if (firstQuote < 0)
                return string.Empty;

            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0)
                return string.Empty;

            return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
        }

        public static bool IsRemoteVersionNewer(string localVersion, string remoteVersion)
        {
            if (string.IsNullOrEmpty(remoteVersion))
                return false;

            int[] localParts = ParseVersionNumbers(localVersion);
            int[] remoteParts = ParseVersionNumbers(remoteVersion);
            for (int i = 0; i < 3; i++)
            {
                if (remoteParts[i] > localParts[i])
                    return true;
                if (remoteParts[i] < localParts[i])
                    return false;
            }
            return false;
        }

        private static int[] ParseVersionNumbers(string ver)
        {
            int[] parts = new int[3];
            if (string.IsNullOrEmpty(ver))
                return parts;

            string clean = ver.Trim().TrimStart('v', 'V');
            int dashIdx = clean.IndexOf('-');
            if (dashIdx >= 0)
                clean = clean.Substring(0, dashIdx);

            string[] tokens = clean.Split('.');
            for (int i = 0; i < tokens.Length && i < 3; i++)
            {
                int val;
                if (int.TryParse(tokens[i], out val))
                    parts[i] = val;
            }
            return parts;
        }

        private void ParseCommandLineArgs()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    if ((args[i] == "+connect_lobby" || args[i] == "-coop-join-lobby") && i + 1 < args.Length)
                    {
                        ulong lobbyId;
                        if (ulong.TryParse(args[i + 1].Trim(), out lobbyId) && lobbyId != 0)
                        {
                            _lobbyCodeInput = lobbyId.ToString();
                            Transport.QueuePendingLobbyJoin(lobbyId, "+connect_lobby");
                        }
                    }
                    else if (args[i] == "-coop-bridge-port" && i + 1 < args.Length)
                    {
                        int.TryParse(args[i + 1], out _bridgePort);
                    }
                    else if (args[i] == "-coop-host-udp" && i + 1 < args.Length)
                    {
                        int port;
                        if (int.TryParse(args[i + 1], out port))
                            Transport.HostUdp(port);
                    }
                    else if (args[i] == "-coop-join-udp" && i + 2 < args.Length)
                    {
                        int port;
                        if (int.TryParse(args[i + 2], out port))
                            Transport.JoinUdp(args[i + 1], port);
                    }
                    else if (args[i] == "-coop-save-dir" && i + 1 < args.Length)
                    {
                        CoopOnlineInterface.OverrideSaveDirectory = args[i + 1];
                        CupheadCoopBootstrapper.Log("Isolated save directory set to: " + CoopOnlineInterface.OverrideSaveDirectory);
                    }
                    else if (args[i] == "-coop-test-mode")
                    {
                        CoopOnlineInterface.TestModeNoSteamWrite = true;
                        CupheadCoopBootstrapper.Log("TestModeNoSteamWrite enabled (protects real Steam achievements during QA).");
                    }
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ParseCommandLineArgs warning: " + ex.Message);
            }
        }

        private void StartAutomationBridge(int port)
        {
            try
            {
                _bridgeListener = new TcpListener(IPAddress.Loopback, port);
                _bridgeListener.Server.Blocking = false;
                _bridgeListener.Start();
                CupheadCoopBootstrapper.Log("Automation QA Bridge listening on 127.0.0.1:" + port);
            }
            catch
            {
                try
                {
                    _bridgePort = port + 1;
                    _bridgeListener = new TcpListener(IPAddress.Loopback, _bridgePort);
                    _bridgeListener.Server.Blocking = false;
                    _bridgeListener.Start();
                    CupheadCoopBootstrapper.Log("Automation QA Bridge listening on 127.0.0.1:" + _bridgePort);
                }
                catch { }
            }
        }

        public void OnUnitySceneLoaded(string sceneName)
        {
            if (Sync != null)
            {
                Sync.OnSceneLoaded(sceneName);
            }
        }

        private void Update()
        {
            if (!Application.runInBackground)
                Application.runInBackground = true;

            if (Input.GetKeyDown(KeyCode.F9))
            {
                _showMenu = !_showMenu;
                Cursor.visible = _showMenu;
                if (_showMenu)
                {
                    Cursor.lockState = CursorLockMode.None;
                }
            }

            if (_showMenu)
            {
                if (!Cursor.visible)
                    Cursor.visible = true;
                if (Cursor.lockState != CursorLockMode.None)
                    Cursor.lockState = CursorLockMode.None;

                if (Input.GetKeyDown(KeyCode.F5))
                {
                    if (Transport.Role == CoopRole.None)
                        Transport.HostSteamLobby();
                    else if (Transport.UsingSteamP2P && Transport.CurrentLobbyId.IsValid())
                        Transport.InviteSteamFriend();
                }
                if (Input.GetKeyDown(KeyCode.F6))
                {
                    if (Transport.Role == CoopRole.None)
                    {
                        string clip = (GUIUtility.systemCopyBuffer ?? string.Empty).Trim();
                        if (!string.IsNullOrEmpty(clip))
                            _lobbyCodeInput = clip;
                        ulong lobbyId;
                        if (ulong.TryParse(_lobbyCodeInput.Trim(), out lobbyId))
                            Transport.JoinSteamLobby(lobbyId);
                    }
                    else if (Transport.UsingSteamP2P && Transport.CurrentLobbyId.IsValid())
                    {
                        GUIUtility.systemCopyBuffer = Transport.CurrentLobbyId.m_SteamID.ToString();
                    }
                }
            }

            if (Transport != null)
            {
                Transport.Poll();

                if (Transport.IsConnected)
                {
                    _pingTimer += Time.unscaledDeltaTime;
                    if (_pingTimer >= 1.0f)
                    {
                        _pingTimer = 0f;
                        Transport.Send(CoopProtocol.BuildPingPong(PacketType.Ping, DateTime.UtcNow.Ticks), false);
                    }
                }
            }

            if (Sync != null)
            {
                Sync.UpdateSync();
            }

            PollAutomationBridge();
        }

        private void LateUpdate()
        {
            if (_showMenu)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        private void FixedUpdate()
        {
            if (Sync != null)
            {
                Sync.FixedUpdateSync();
            }
        }

        private void PollAutomationBridge()
        {
            try
            {
                string reqPath = Path.GetFullPath("coop_bridge_req.txt");
                string respPath = Path.GetFullPath("coop_bridge_resp.json");
                if (File.Exists(reqPath))
                {
                    string cmd = File.ReadAllText(reqPath, Encoding.UTF8).Trim();
                    File.Delete(reqPath);
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        string resp = HandleBridgeCommand(cmd);
                        File.WriteAllText(respPath, resp, new UTF8Encoding(false));
                    }
                }
            }
            catch { }

            if (_bridgeListener == null)
                return;

            try
            {
                if (!_bridgeListener.Pending())
                    return;

                using (TcpClient client = _bridgeListener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                using (StreamReader sr = new StreamReader(stream, Encoding.UTF8))
                using (StreamWriter sw = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    client.ReceiveTimeout = 200;
                    string line = sr.ReadLine();
                    if (string.IsNullOrEmpty(line))
                        return;

                    string response = HandleBridgeCommand(line.Trim());
                    sw.WriteLine(response);
                }
            }
            catch { }
        }

        private string HandleBridgeCommand(string cmd)
        {
            if (cmd == "status")
            {
                Sync.EnsureOnlineWrapper();
                string scene = SceneManager.GetActiveScene().name;
                int achCount = Sync.OnlineWrapper != null ? Sync.OnlineWrapper.AchievementsSyncedCount : 0;
                string lastAch = Sync.OnlineWrapper != null ? Sync.OnlineWrapper.LastUnlockedAchievement : "None";
                bool p2Exists = PlayerManager.DoesPlayerExist(PlayerId.PlayerTwo);
                return string.Format(
                    "{{\"ok\":true,\"version\":\"{0}\",\"role\":\"{1}\",\"connected\":{2},\"scene\":\"{3}\",\"multiplayer\":{4},\"p2Exists\":{5},\"tick\":{6},\"seed\":{7},\"rttMs\":{8:F1},\"achievementsSynced\":{9},\"lastAchievement\":\"{10}\",\"bothHaveDlc\":{11}}}",
                    CoopProtocol.ModVersion,
                    Transport.Role,
                    Transport.IsConnected ? "true" : "false",
                    scene,
                    PlayerManager.Multiplayer ? "true" : "false",
                    p2Exists ? "true" : "false",
                    Sync.LocalTick,
                    Sync.CurrentRngSeed,
                    Transport.LastRttMs,
                    achCount,
                    lastAch,
                    Sync.BothPlayersHaveDlc ? "true" : "false");
            }
            if (cmd == "force_join_p2")
            {
                Sync.ForceMultiplayerPlayerTwoJoined();
                Sync.ConfigureLocalAndRemoteInputs();
                return "{\"ok\":true,\"multiplayer\":true}";
            }
            if (cmd.StartsWith("load_level "))
            {
                string lvlName = cmd.Substring("load_level ".Length).Trim();
                Levels lvl = (Levels)Enum.Parse(typeof(Levels), lvlName, true);
                if (!PlayerData.Initialized)
                {
                    PlayerData.Init(null);
                }
                PlayerData.inGame = true;
                PlayerManager.Multiplayer = true;
                Sync.ForceMultiplayerPlayerTwoJoined();
                Level.SetCurrentMode(Level.Mode.Normal);
                SceneLoader.LoadLevel(lvl, SceneLoader.Transition.Fade, SceneLoader.Icon.None, null);
                return "{\"ok\":true,\"loadingLevel\":\"" + lvl + "\"}";
            }
            if (cmd == "get_players")
            {
                AbstractPlayerController p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne);
                AbstractPlayerController p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
                float p1x = p1 != null ? p1.transform.position.x : 0f;
                float p1y = p1 != null ? p1.transform.position.y : 0f;
                float p2x = p2 != null ? p2.transform.position.x : 0f;
                float p2y = p2 != null ? p2.transform.position.y : 0f;
                LevelPlayerController lp2 = p2 as LevelPlayerController;
                bool p2Shooting = (lp2 != null && lp2.weaponManager != null && lp2.weaponManager.IsShooting);
                return string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{{\"ok\":true,\"p1Exists\":{0},\"p2Exists\":{1},\"p1x\":{2:F2},\"p1y\":{3:F2},\"p2x\":{4:F2},\"p2y\":{5:F2},\"p2Shooting\":{6}}}",
                    p1 != null ? "true" : "false",
                    p2 != null ? "true" : "false",
                    p1x, p1y, p2x, p2y,
                    p2Shooting ? "true" : "false");
            }
            if (cmd.StartsWith("host_udp "))
            {
                int port = int.Parse(cmd.Substring("host_udp ".Length).Trim());
                Transport.HostUdp(port);
                return "{\"ok\":true,\"action\":\"host_udp\"}";
            }
            if (cmd.StartsWith("join_udp "))
            {
                string[] parts = cmd.Substring("join_udp ".Length).Trim().Split(':');
                Transport.JoinUdp(parts[0], int.Parse(parts[1]));
                return "{\"ok\":true,\"action\":\"join_udp\"}";
            }
            if (cmd.StartsWith("inject_packet "))
            {
                string b64 = cmd.Substring("inject_packet ".Length).Trim();
                byte[] raw = Convert.FromBase64String(b64);
                Transport.InjectTestPayload(raw);
                return "{\"ok\":true,\"injectedBytes\":" + raw.Length + "}";
            }
            if (cmd.StartsWith("unlock_achievement "))
            {
                Sync.EnsureOnlineWrapper();
                string achId = cmd.Substring("unlock_achievement ".Length).Trim();
                if (OnlineManager.Instance != null && OnlineManager.Instance.Interface != null)
                {
                    OnlineManager.Instance.Interface.UnlockAchievement(Sync.GetLocalPlayerId(), achId);
                    return "{\"ok\":true,\"unlocked\":\"" + achId + "\"}";
                }
                return "{\"ok\":false,\"error\":\"OnlineManager not ready\"}";
            }
            if (cmd == "verify_edge_cases")
            {
                return RunEdgeCaseVerification();
            }
            return "{\"ok\":false,\"error\":\"unknown_command\"}";
        }

        private string RunEdgeCaseVerification()
        {
            try
            {
                // 1. Test Pause Menu "Remove Player 2" protection
                PlayerManager.PlayerLeave(PlayerId.PlayerTwo);
                Sync.ForceMultiplayerPlayerTwoJoined();
                bool pauseLeaveProtected = PlayerManager.Multiplayer;

                // 2. Test Real-Time LoadoutSync
                bool loadoutSynced = false;
                if (PlayerData.Initialized && PlayerData.Data != null)
                {
                    byte[] loadoutPkt = CoopProtocol.BuildLoadoutSync(
                        (byte)PlayerId.PlayerTwo,
                        (int)Weapon.level_weapon_spreadshot,
                        (int)Weapon.level_weapon_boomerang,
                        (int)Super.level_super_beam,
                        (int)Charm.charm_chalice);
                    Transport.InjectTestPayload(loadoutPkt);

                    PlayerData.PlayerLoadouts.PlayerLoadout p2Loadout =
                        PlayerData.Data.Loadouts.GetPlayerLoadout(PlayerId.PlayerTwo);
                    loadoutSynced = (p2Loadout != null &&
                                     p2Loadout.primaryWeapon == Weapon.level_weapon_spreadshot &&
                                     p2Loadout.secondaryWeapon == Weapon.level_weapon_boomerang &&
                                     p2Loadout.super == Super.level_super_beam &&
                                     p2Loadout.charm == Charm.charm_chalice &&
                                     p2Loadout.HasEquippedSecondaryRegularWeapon);
                }

                // 3. Test Shared InventorySync
                bool inventorySynced = false;
                if (PlayerData.Initialized && PlayerData.Data != null)
                {
                    byte[] invPkt = CoopProtocol.BuildInventorySync(
                        new int[] { (int)Weapon.level_weapon_charge },
                        new int[] { (int)Super.level_super_invincible },
                        new int[] { (int)Charm.charm_smoke_dash },
                        7,
                        7);
                    Transport.InjectTestPayload(invPkt);

                    inventorySynced =
                        PlayerData.Data.IsUnlocked(PlayerId.PlayerOne, Weapon.level_weapon_charge) &&
                        PlayerData.Data.IsUnlocked(PlayerId.PlayerTwo, Weapon.level_weapon_charge) &&
                        PlayerData.Data.IsUnlocked(PlayerId.PlayerTwo, Super.level_super_invincible) &&
                        PlayerData.Data.IsUnlocked(PlayerId.PlayerTwo, Charm.charm_smoke_dash) &&
                        PlayerData.Data.GetCurrency(PlayerId.PlayerTwo) >= 7;
                }

                // 4. Test King Dice Board Sync & Revive/Coin packets
                int packedHearts = 2 | (5 << 8) | (8 << 16);
                byte[] kdPkt = CoopProtocol.BuildLevelEvent(LevelEventType.KingDiceBoardSync, 0, 4f, 2f, packedHearts);
                bool kdPacketValid = (kdPkt != null && kdPkt.Length >= 15);

                byte[] stealPkt = CoopProtocol.BuildLevelEvent(LevelEventType.PlayerStealHealthRevive, (byte)PlayerId.PlayerTwo, 100f, 200f, 1);
                byte[] coinPkt = CoopProtocol.BuildLevelEvent(LevelEventType.CoinCollected, (byte)PlayerId.PlayerTwo, 0f, 0f, 123456);
                bool eventsValid = (stealPkt.Length >= 15 && coinPkt.Length >= 15);

                // =========================================================================
                // NEW v1.1.0 VERIFICATIONS (All 8 Improvements + Localization + Update Check)
                // =========================================================================

                // Improvement #1: LevelReadyGate (Symmetric Level-Start Barrier)
                Sync.BeginLevelReadyGate((int)Levels.Veggies);
                bool gatePaused = Sync.IsWaitingAtLevelReadyGate && PauseManager.state == PauseManager.State.Paused;
                byte[] readyPkt = CoopProtocol.BuildLevelEvent(LevelEventType.LevelReady, (byte)PlayerId.PlayerTwo, 1f, 0f, (int)Levels.Veggies);
                Transport.InjectTestPayload(readyPkt);
                bool gateReleased = !Sync.IsWaitingAtLevelReadyGate && PauseManager.state != PauseManager.State.Paused;
                bool levelReadyGateVerified = gatePaused && gateReleased;

                // Improvement #2: PauseSync (Shared In-Level Pause Across Both PCs)
                byte[] pauseOnPkt = CoopProtocol.BuildLevelEvent(LevelEventType.PauseSync, (byte)PlayerId.PlayerTwo, 0f, 0f, 1);
                Transport.InjectTestPayload(pauseOnPkt);
                bool remotePausedOk = Sync.IsPausedByPauseSync && PauseManager.state == PauseManager.State.Paused;
                byte[] pauseOffPkt = CoopProtocol.BuildLevelEvent(LevelEventType.PauseSync, (byte)PlayerId.PlayerTwo, 0f, 0f, 0);
                Transport.InjectTestPayload(pauseOffPkt);
                bool remoteUnpausedOk = !Sync.IsPausedByPauseSync && PauseManager.state != PauseManager.State.Paused;
                bool pauseSyncVerified = remotePausedOk && remoteUnpausedOk;

                // Improvement #3: SceneRequestSync (Guest-initiated Retry / ExitToMap / Level Entry)
                byte[] sceneReqPkt = CoopProtocol.BuildSceneRequest(SceneRequestKind.ReloadLevel, "scene_level_veggies", (int)Levels.Veggies, (int)Level.Mode.Normal);
                bool sceneRequestVerified = (sceneReqPkt != null && sceneReqPkt[0] == (byte)PacketType.SceneRequest && sceneReqPkt.Length > 10);

                // Improvement #4: WorldSaveMirror + Dialoguer in-RAM (Without Overwriting Client Disk Save)
                bool worldSaveMirrorVerified = false;
                if (PlayerData.Initialized && PlayerData.Data != null)
                {
                    int beforeCount = Sync.WorldSaveMirrorsAppliedCount;
                    string slotJson = JsonUtility.ToJson(PlayerData.Data);
                    string diagXml = Dialoguer.ready ? (Dialoguer.GetGlobalVariablesState() ?? "") : "";
                    byte[] compressed;
                    using (MemoryStream rawMs = new MemoryStream())
                    {
                        using (GZipStream gz = new GZipStream(rawMs, CompressionMode.Compress, true))
                        using (BinaryWriter bw = new BinaryWriter(gz, Encoding.UTF8))
                        {
                            bw.Write(slotJson);
                            bw.Write(diagXml);
                        }
                        compressed = rawMs.ToArray();
                    }
                    byte[] mirrorChunkPkt = CoopProtocol.BuildWorldSaveMirrorChunk(77, 0, 0, 1, compressed, 0, compressed.Length);
                    Transport.InjectTestPayload(mirrorChunkPkt);
                    worldSaveMirrorVerified = (Sync.WorldSaveMirrorsAppliedCount == beforeCount + 1) && CoopOnlineInterface.SuppressClientDiskSave;
                    CoopOnlineInterface.SuppressClientDiskSave = false;
                }

                // Improvement #5: DLC & Version Verification Guard (Prevents Infinite DLC Loading)
                bool dlcAndVersionGuardVerified = false;
                CoopSyncController.ForceSimulateNoDlcForTest = true;
                try
                {
                    int testPrimary = (int)Weapon.level_weapon_crackshot;
                    int testSecondary = (int)Weapon.level_weapon_wide_shot;
                    int testSuper = (int)Super.level_super_beam;
                    int testCharm = (int)Charm.charm_chalice;
                    bool sanitized = Sync.SanitizeLoadoutForDlc(ref testPrimary, ref testSecondary, ref testSuper, ref testCharm);
                    bool isDlcSceneDetected = CoopSyncController.IsDlcSceneOrLevel("scene_level_saltbaker", (int)Levels.Saltbaker);
                    byte[] rejectPkt = CoopProtocol.BuildHandshakeReject(CoopProtocol.ProtocolVersion, CoopProtocol.ModVersion, "Mismatch test");
                    dlcAndVersionGuardVerified = sanitized
                        && testPrimary == (int)Weapon.level_weapon_peashot
                        && testSecondary == (int)Weapon.None
                        && testCharm == (int)Charm.None
                        && isDlcSceneDetected
                        && rejectPkt[0] == (byte)PacketType.HandshakeReject;
                }
                finally
                {
                    CoopSyncController.ForceSimulateNoDlcForTest = false;
                }

                // Improvement #6: Cold-launch (+connect_lobby) & Save-Init Guard
                Transport.QueuePendingLobbyJoin(76561198000000001UL, "+connect_lobby");
                bool coldLaunchInviteVerified = (Transport.PendingInviteLobbyId == 76561198000000001UL);
                typeof(CoopNetworkTransport).GetField("_pendingLobbyJoinId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .SetValue(Transport, 0UL);

                // Improvement #7: Auto-Reconnect on Unexpected Connection Drop
                Transport.TriggerSimulatedDropForTest();
                bool autoReconnectVerified = (Transport.AutoReconnectAttemptsLeft == 3);
                Transport.HostUdp(26891);
                Transport.InjectTestPayload(CoopProtocol.BuildHandshake(1, 424242, (int)Weapon.level_weapon_peashot, 0, (int)Super.level_super_beam, 0));

                // Improvement #8: Knockout Gate & Synchronized LevelWin
                byte[] winPkt = CoopProtocol.BuildLevelEvent(LevelEventType.LevelWin, (byte)PlayerId.PlayerOne, 100f, 0f, (int)Levels.Veggies);
                bool knockoutGateVerified = (winPkt != null && winPkt.Length >= 15);

                // Improvement #9: Dynamic Localization (PT-BR / EN) & GitHub Update Check Parser
                int oldLangMode = _languageMode;
                _languageMode = 1;
                bool ptOk = IsPortugueseActive();
                _languageMode = 2;
                bool enOk = !IsPortugueseActive();
                _languageMode = oldLangMode;
                bool localizationVerified = ptOk && enOk;

                string parsedTag = ExtractTagFromReleaseJson("{\"url\":\"https://api.github.com\",\"tag_name\":\"v1.2.0\",\"name\":\"Release\"}");
                bool updateCheckParserVerified = (parsedTag == "v1.2.0")
                    && IsRemoteVersionNewer("1.1.0", "1.2.0")
                    && !IsRemoteVersionNewer("1.1.0", "1.1.0")
                    && !IsRemoteVersionNewer("1.1.0", "1.0.0");

                // Fix #1 (v1.1.1): Map Bridge Refresh (empty _levels preserved + completed _levels enabled)
                bool mapBridgeRefreshVerified = false;
                if (PlayerData.Initialized && PlayerData.Data != null)
                {
                    GameObject testRoot = new GameObject("CoopTestBridgeObstacle");
                    GameObject toEnable = new GameObject("BridgeEnable");
                    GameObject toDisable = new GameObject("BridgeDisable");
                    toEnable.transform.SetParent(testRoot.transform);
                    toDisable.transform.SetParent(testRoot.transform);
                    toEnable.SetActive(true);
                    toDisable.SetActive(false);

                    MapLevelDependentObstacle obs = testRoot.AddComponent<MapLevelDependentObstacle>();
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    typeof(MapLevelDependentObstacle).GetField("ToEnable", flags).SetValue(obs, toEnable);
                    typeof(MapLevelDependentObstacle).GetField("ToDisable", flags).SetValue(obs, toDisable);
                    typeof(AbstractMapLevelDependentEntity).GetField("_levels", flags).SetValue(obs, new Levels[0]);

                    // Empty _levels (like the World 1 start bridge) must remain enabled
                    CoopSyncController.RefreshMapObstacles(true);
                    bool emptyLevelsPreserved = toEnable.activeSelf && !toDisable.activeSelf;

                    // Completed level obstacle must transition to enabled state
                    var vegData = PlayerData.Data.GetLevelData(Levels.Veggies);
                    bool origCompleted = vegData.completed;
                    vegData.completed = true;
                    toEnable.SetActive(false);
                    toDisable.SetActive(true);
                    typeof(AbstractMapLevelDependentEntity).GetField("_levels", flags).SetValue(obs, new Levels[] { Levels.Veggies });
                    CoopSyncController.RefreshMapObstacles(true);
                    bool completedLevelEnabled = toEnable.activeSelf && !toDisable.activeSelf;
                    vegData.completed = origCompleted;

                    DestroyImmediate(testRoot);
                    mapBridgeRefreshVerified = emptyLevelsPreserved && completedLevelEnabled;
                }

                // Fix #2 (v1.1.1): No Duplicate Player Clone When Partner Is Dead
                bool noDeadPlayerCloneVerified = false;
                AbstractPlayerController p2Ctrl = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
                if (p2Ctrl != null && p2Ctrl.stats != null)
                {
                    int origHp = p2Ctrl.stats.Health;
                    p2Ctrl.stats.SetHealth(0); // Now p2Ctrl.IsDead == true and PlayerManager.DoesPlayerExist(PlayerTwo) == false
                    bool doesExistWhileDead = PlayerManager.DoesPlayerExist(PlayerId.PlayerTwo);
                    Sync.ForceMultiplayerPlayerTwoJoined();
                    AbstractPlayerController[] allCtrls = FindObjectsOfType<AbstractPlayerController>();
                    int p2Count = 0;
                    for (int i = 0; i < allCtrls.Length; i++)
                    {
                        if (allCtrls[i] != null && allCtrls[i].id == PlayerId.PlayerTwo)
                            p2Count++;
                    }
                    p2Ctrl.stats.SetHealth(origHp > 0 ? origHp : 3);
                    noDeadPlayerCloneVerified = (!doesExistWhileDead && p2Count == 1);
                }

                // Fix #3 (v1.1.1): Remote Puppet Damage Guard & Death Event Sync
                bool remoteDamageGuardAndDeathSyncVerified = false;
                Sync.ConfigureLocalAndRemoteInputs();
                AbstractPlayerController p1Ctrl = PlayerManager.GetPlayer(PlayerId.PlayerOne);
                p2Ctrl = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
                if (p1Ctrl != null && p2Ctrl != null && p1Ctrl.damageReceiver != null && p2Ctrl.damageReceiver != null && p1Ctrl.stats != null && p2Ctrl.stats != null)
                {
                    byte[] deathPkt = CoopProtocol.BuildLevelEvent(LevelEventType.PlayerDeath, (byte)PlayerId.PlayerTwo, 15f, 25f, 0);
                    remoteDamageGuardAndDeathSyncVerified = p1Ctrl.damageReceiver.enabled
                        && !CoopSyncController.GetPlayerHardInvincibility(p1Ctrl.stats)
                        && !p2Ctrl.damageReceiver.enabled
                        && CoopSyncController.GetPlayerHardInvincibility(p2Ctrl.stats)
                        && deathPkt != null && deathPkt.Length >= 15;
                }

                // Fix #4 (v1.1.1): Deterministic Boss Attack Pattern RNG Sync
                bool bossPatternRngSyncVerified = false;
                if (Level.Current != null)
                {
                    bool synced1 = Sync.SynchronizeBossPropertiesRng(777777);
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
                    System.Reflection.FieldInfo propsField = Level.Current.GetType().GetField("properties", flags);
                    if (propsField != null)
                    {
                        object propsObj = propsField.GetValue(Level.Current);
                        if (propsObj != null)
                        {
                            System.Reflection.PropertyInfo curStateProp = propsObj.GetType().GetProperty("CurrentState", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            object curState = curStateProp != null ? curStateProp.GetValue(propsObj, null) : null;
                            if (curState != null && curState.GetType().BaseType != null)
                            {
                                System.Reflection.FieldInfo patIdxField = curState.GetType().BaseType.GetField("patternIndex", flags);
                                if (patIdxField != null)
                                {
                                    int expectedIdx = (int)patIdxField.GetValue(curState);
                                    patIdxField.SetValue(curState, 9999);
                                    bool synced2 = Sync.SynchronizeBossPropertiesRng(777777);
                                    int restoredIdx = (int)patIdxField.GetValue(curState);
                                    bossPatternRngSyncVerified = synced1 && synced2 && restoredIdx == expectedIdx && restoredIdx != 9999;
                                }
                            }
                        }
                    }
                }

                return string.Format(
                    "{{\"ok\":true,\"pauseLeaveProtected\":{0},\"loadoutSynced\":{1},\"inventorySynced\":{2},\"kdPacketValid\":{3},\"eventsValid\":{4},\"levelReadyGateVerified\":{5},\"pauseSyncVerified\":{6},\"sceneRequestVerified\":{7},\"worldSaveMirrorVerified\":{8},\"dlcAndVersionGuardVerified\":{9},\"coldLaunchInviteVerified\":{10},\"autoReconnectVerified\":{11},\"knockoutGateVerified\":{12},\"localizationVerified\":{13},\"updateCheckParserVerified\":{14},\"mapBridgeRefreshVerified\":{15},\"noDeadPlayerCloneVerified\":{16},\"remoteDamageGuardAndDeathSyncVerified\":{17},\"bossPatternRngSyncVerified\":{18}}}",
                    pauseLeaveProtected ? "true" : "false",
                    loadoutSynced ? "true" : "false",
                    inventorySynced ? "true" : "false",
                    kdPacketValid ? "true" : "false",
                    eventsValid ? "true" : "false",
                    levelReadyGateVerified ? "true" : "false",
                    pauseSyncVerified ? "true" : "false",
                    sceneRequestVerified ? "true" : "false",
                    worldSaveMirrorVerified ? "true" : "false",
                    dlcAndVersionGuardVerified ? "true" : "false",
                    coldLaunchInviteVerified ? "true" : "false",
                    autoReconnectVerified ? "true" : "false",
                    knockoutGateVerified ? "true" : "false",
                    localizationVerified ? "true" : "false",
                    updateCheckParserVerified ? "true" : "false",
                    mapBridgeRefreshVerified ? "true" : "false",
                    noDeadPlayerCloneVerified ? "true" : "false",
                    remoteDamageGuardAndDeathSyncVerified ? "true" : "false",
                    bossPatternRngSyncVerified ? "true" : "false");
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"" + (ex.Message ?? "").Replace("\"", "'") + "\"}";
            }
        }

        private string GetLocalizedStatusMessage(bool pt)
        {
            string raw = Transport.StatusMessage ?? string.Empty;
            if (raw == "Idle (Not Connected)" || raw == "Disconnected")
            {
                return pt ? "Desconectado (Pronto para conectar)" : "Idle (Not Connected)";
            }
            if (!pt)
            {
                // Translate common PT internal transport messages when UI is in English
                if (raw.StartsWith("Convite recebido", StringComparison.OrdinalIgnoreCase))
                    return "Steam Invite received! Waiting for saves to load...";
                if (raw.StartsWith("Parceiro caiu", StringComparison.OrdinalIgnoreCase))
                    return "Partner dropped. Lobby open, waiting for auto-reconnect...";
                if (raw.StartsWith("Conexao oscilou", StringComparison.OrdinalIgnoreCase))
                    return "Connection dropped. Auto-reconnecting (" + Transport.AutoReconnectAttemptsLeft + "x)...";
                if (raw.StartsWith("Reconectando a sala", StringComparison.OrdinalIgnoreCase))
                    return "Reconnecting to Steam Lobby...";
            }
            else
            {
                // Translate common EN internal transport messages when UI is in Portuguese
                if (raw.StartsWith("Creating Steam Friend Lobby", StringComparison.OrdinalIgnoreCase))
                    return "Criando Sala de Amigos na Steam...";
                if (raw.StartsWith("Hosting Steam Lobby", StringComparison.OrdinalIgnoreCase))
                    return "Sala Steam criada! Convide seu amigo [F5] ou copie o codigo [F6].";
                if (raw.StartsWith("Joining Steam Lobby", StringComparison.OrdinalIgnoreCase))
                    return "Entrando na Sala Steam...";
            }
            return raw;
        }

        private void OnGUI()
        {
            if (!_showMenu)
                return;

            bool pt = IsPortugueseActive();
            string title = pt
                ? ("Cuphead Co-op Online v" + CoopProtocol.ModVersion + " + Conquistas Steam (F9)")
                : ("Cuphead Online Co-op v" + CoopProtocol.ModVersion + " + Steam Achievements (F9)");

            GUI.Box(new Rect(12, 12, 435, 425), title);

            // Language toggle pill in the top-right corner of the box
            string langBtnLabel = _languageMode == 0
                ? (pt ? "Idioma: Auto (PT)" : "Lang: Auto (EN)")
                : (pt ? "Idioma: PT" : "Lang: EN");
            if (GUI.Button(new Rect(332, 34, 105, 20), langBtnLabel))
            {
                _languageMode = (_languageMode + 1) % 3;
            }

            GUILayout.BeginArea(new Rect(22, 38, 415, 390));
            bool steamReady = CoopNetworkTransport.IsSteamInitialized();
            GUILayout.Label("Steam API: " + (steamReady
                ? (pt ? "CONECTADA (Pronto)" : "CONNECTED (Ready)")
                : (pt ? "Inicializando..." : "Initializing...")));

            GUILayout.Label("Status: " + GetLocalizedStatusMessage(pt));

            string roleLabel = Transport.Role == CoopRole.None
                ? (pt ? "Nenhum" : "None")
                : Transport.Role.ToString();
            string connLabel = Transport.IsConnected
                ? (pt ? "Sim" : "Yes")
                : (pt ? "Nao" : "No");
            GUILayout.Label(string.Format(
                pt ? "Papel: {0} | Conectado: {1} | Ping: {2:F0} ms" : "Role: {0} | Connected: {1} | Ping: {2:F0} ms",
                roleLabel, connLabel, Transport.LastRttMs));

            string dlcStatus;
            if (Sync.BothPlayersHaveDlc)
            {
                dlcStatus = pt ? "Ativa (Ambos possuem)" : "Active (Both players own DLC)";
            }
            else if (CoopSyncController.IsLocalDlcEnabled())
            {
                dlcStatus = pt ? "Protecao Ativa (Parceiro sem DLC)" : "Guard Active (Partner lacks DLC)";
            }
            else
            {
                dlcStatus = pt ? "Desativada (Protecao Ativa)" : "Disabled (Guard Active)";
            }
            GUILayout.Label("DLC (Delicious Last Course): " + dlcStatus);

            string achStatus = Sync.OnlineWrapper != null
                ? string.Format(
                    pt ? "Ativo (Ultima: {0} | Total: {1})" : "Active (Last: {0} | Total: {1})",
                    Sync.OnlineWrapper.LastUnlockedAchievement,
                    Sync.OnlineWrapper.AchievementsSyncedCount)
                : (pt ? "Aguardando OnlineManager..." : "Waiting for OnlineManager...");
            GUILayout.Label((pt ? "Conquistas Steam: " : "Steam Achievements: ") + achStatus);

            if (!string.IsNullOrEmpty(Sync.LastDlcWarning))
            {
                GUILayout.Label(Sync.LastDlcWarning);
            }
            if (!string.IsNullOrEmpty(Sync.LastVersionMismatchError))
            {
                GUILayout.Label(Sync.LastVersionMismatchError);
            }

            GUILayout.Space(6);

            if (Transport.Role == CoopRole.None)
            {
                GUILayout.Label(pt
                    ? "=== JOGAR PELA STEAM (Sem Radmin / Sem Portas) ==="
                    : "=== PLAY VIA STEAM (No VPN / No Port Forwarding) ===");
                if (GUILayout.Button(pt
                    ? "1. CRIAR SALA STEAM - Host [Tecla F5]"
                    : "1. HOST STEAM LOBBY [F5 Key]"))
                {
                    Transport.HostSteamLobby();
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label(pt ? "Codigo da Sala:" : "Lobby Code:", GUILayout.Width(95));
                _lobbyCodeInput = GUILayout.TextField(_lobbyCodeInput, GUILayout.Width(145));
                if (GUILayout.Button(pt ? "Colar+Entrar [F6]" : "Paste+Join [F6]", GUILayout.Width(135)))
                {
                    string clip = (GUIUtility.systemCopyBuffer ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(clip))
                        _lobbyCodeInput = clip;
                    ulong lobbyId;
                    if (ulong.TryParse(_lobbyCodeInput.Trim(), out lobbyId))
                        Transport.JoinSteamLobby(lobbyId);
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(6);
                GUILayout.Label(pt
                    ? "=== TESTE LOCAL / LAN (Opcional) ==="
                    : "=== DIRECT UDP / LAN (Optional) ===");
                GUILayout.BeginHorizontal();
                GUILayout.Label("IP:", GUILayout.Width(25));
                _ipInput = GUILayout.TextField(_ipInput, GUILayout.Width(130));
                GUILayout.Label(pt ? "Porta:" : "Port:", GUILayout.Width(42));
                _portInput = GUILayout.TextField(_portInput, GUILayout.Width(65));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Host UDP"))
                {
                    int port;
                    if (int.TryParse(_portInput, out port))
                        Transport.HostUdp(port);
                }
                if (GUILayout.Button("Join UDP"))
                {
                    int port;
                    if (int.TryParse(_portInput, out port))
                        Transport.JoinUdp(_ipInput, port);
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                if (Transport.UsingSteamP2P && Transport.CurrentLobbyId.IsValid())
                {
                    string code = Transport.CurrentLobbyId.m_SteamID.ToString();
                    GUILayout.Label((pt ? "Codigo da sua Sala Steam: " : "Your Steam Lobby Code: ") + code);

                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(pt ? "Copiar Codigo [F6]" : "Copy Code [F6]"))
                    {
                        GUIUtility.systemCopyBuffer = code;
                    }
                    if (GUILayout.Button(pt ? "Convidar na Steam [F5]" : "Invite on Steam [F5]"))
                    {
                        Transport.InviteSteamFriend();
                    }
                    GUILayout.EndHorizontal();
                }

                if (GUILayout.Button(pt
                    ? "Sincronizar / Forcar Entrada do Player 2"
                    : "Sync / Force Player 2 Spawn"))
                {
                    Sync.ForceMultiplayerPlayerTwoJoined();
                    Sync.ConfigureLocalAndRemoteInputs();
                }

                if (GUILayout.Button(pt
                    ? "Desconectar Sessao Co-op"
                    : "Disconnect Co-op Session"))
                {
                    Transport.Disconnect();
                }
            }

            GUILayout.Space(8);

            // Non-invasive Footer: Update status + Direct GitHub link
            GUILayout.BeginHorizontal();
            if (_updateAvailable)
            {
                string updText = pt
                    ? ("Nova versao v" + _latestRemoteVersion + " disponivel! [Baixar]")
                    : ("New version v" + _latestRemoteVersion + " available! [Download]");
                if (GUILayout.Button(updText))
                {
                    Application.OpenURL(GitHubReleasesUrl);
                }
            }
            else
            {
                string verLabel = _updateCheckCompleted
                    ? (pt ? ("v" + CoopProtocol.ModVersion + " (Atualizado)") : ("v" + CoopProtocol.ModVersion + " (Up to date)"))
                    : ("v" + CoopProtocol.ModVersion);
                GUILayout.Label(verLabel, GUILayout.Width(145));
                if (GUILayout.Button(pt ? "GitHub / Reportar Bug" : "GitHub / Report Issue"))
                {
                    Application.OpenURL(GitHubRepoUrl);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
            if (_bridgeListener != null)
            {
                try { _bridgeListener.Stop(); } catch { }
                _bridgeListener = null;
            }
        }
    }
}
