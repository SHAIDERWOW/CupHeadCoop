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
        public const string NexusModsUrl = "https://www.nexusmods.com/cuphead/mods/219";
        private const string RichPresencePrefKey = "CupheadCoop_RichPresence";

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

        // Dynamic Steam Rich Presence & Copy Feedback state (v1.2.0 / v1.3.0)
        private bool _richPresenceEnabled = true;
        private string _lastAppliedRichPresence = string.Empty;
        private float _richPresenceTimer;
        private float _logCopiedFeedbackUntil;
        private float _codeCopiedFeedbackUntil;

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

            try
            {
                _richPresenceEnabled = PlayerPrefs.GetInt(RichPresencePrefKey, 1) != 0;
            }
            catch
            {
                _richPresenceEnabled = true;
            }

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
            UpdateSteamRichPresence(true);
        }

        private void Update()
        {
            if (!Application.runInBackground)
                Application.runInBackground = true;

            _richPresenceTimer += Time.unscaledDeltaTime;
            if (_richPresenceTimer >= 1.5f)
            {
                _richPresenceTimer = 0f;
                UpdateSteamRichPresence(false);
            }

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
                        _codeCopiedFeedbackUntil = Time.unscaledTime + 2.5f;
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

                // Fix #1 (v1.1.1+): Map Bridge Refresh (disabled Bridge 1 component with Platforming_Level_1_1 preserved + completed _levels enabled + 33-coin inflation guard)
                bool mapBridgeRefreshVerified = false;
                if (PlayerData.Initialized && PlayerData.Data != null)
                {
                    GameObject testRoot = new GameObject("Bridge 1");
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

                    // Disabled component (like "Bridge 1" in scene_map_world_1 with _levels=[Platforming_Level_1_1] and enabled=false) must remain enabled!
                    obs.enabled = false;
                    typeof(AbstractMapLevelDependentEntity).GetField("_levels", flags).SetValue(obs, new Levels[] { Levels.Platforming_Level_1_1 });
                    CoopSyncController.RefreshMapObstacles(true);
                    bool disabledBridge1Preserved = toEnable.activeSelf && !toDisable.activeSelf;

                    // Completed level obstacle (with enabled=true) must transition to enabled state
                    testRoot.name = "Bridge 2";
                    obs.enabled = true;
                    var vegData = PlayerData.Data.GetLevelData(Levels.Veggies);
                    bool origCompleted = vegData.completed;
                    vegData.completed = true;
                    toEnable.SetActive(false);
                    toDisable.SetActive(true);
                    typeof(AbstractMapLevelDependentEntity).GetField("_levels", flags).SetValue(obs, new Levels[] { Levels.Veggies });
                    CoopSyncController.RefreshMapObstacles(true);
                    bool completedLevelEnabled = toEnable.activeSelf && !toDisable.activeSelf;
                    vegData.completed = origCompleted;

                    // Verify 33-coin singleplayer P2 unspent coin inflation guard (P1=0, P2=33 -> both become 0)
                    int origC1 = PlayerData.Data.GetCurrency(PlayerId.PlayerOne);
                    int origC2 = PlayerData.Data.GetCurrency(PlayerId.PlayerTwo);
                    CoopSyncController.SetPlayerCurrencyExact(PlayerId.PlayerOne, 0);
                    CoopSyncController.SetPlayerCurrencyExact(PlayerId.PlayerTwo, 33);
                    Sync.MirrorAndSyncPlayerInventories(false);
                    bool no33CoinInflation = (PlayerData.Data.GetCurrency(PlayerId.PlayerOne) == 0 && PlayerData.Data.GetCurrency(PlayerId.PlayerTwo) == 0);
                    CoopSyncController.SetPlayerCurrencyExact(PlayerId.PlayerOne, origC1);
                    CoopSyncController.SetPlayerCurrencyExact(PlayerId.PlayerTwo, origC2);

                    DestroyImmediate(testRoot);
                    mapBridgeRefreshVerified = disabledBridge1Preserved && completedLevelEnabled && no33CoinInflation;
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

                // Fix #5 (v1.2.0): Dynamic Steam Rich Presence (Official PT-BR / EN boss & stage names + Toggle) & Copy Log to Clipboard
                string rpPtCoop = FormatRichPresenceText("scene_level_veggies", (int)Levels.Veggies, true, true);
                string rpEnSolo = FormatRichPresenceText("scene_map_world_1", -1, false, false);
                bool richPresenceVerified = (rpPtCoop == "Enfrentando Botânico Rebelde (2 Jogadores)")
                    && (rpEnSolo == "Exploring Inkwell Isle I (Solo)");

                bool copyLogVerified = CopyRecentLogToClipboard() && !string.IsNullOrEmpty(GUIUtility.systemCopyBuffer);

                // =========================================================================
                // NEW v1.3.0 VERIFICATIONS (HELLguy14's 7 Fixes + UI Refinements)
                // =========================================================================

                // 1. NPC / Ms. Chalice Dialogue Auto-Positioning & Interactor.Either
                bool npcDialogueAutoPosVerified = false;
                AbstractPlayerController remotePlayer = PlayerManager.GetPlayer(Sync.GetRemotePlayerId());
                if (remotePlayer != null)
                {
                    Vector3 origRemotePos = remotePlayer.transform.position;
                    GameObject dlgGo = new GameObject("TestDialoguePoint");
                    dlgGo.transform.position = new Vector3(100f, 0f, 0f);
                    DialogueInteractionPoint dip = dlgGo.AddComponent<DialogueInteractionPoint>();
                    var allFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    dip.interactor = AbstractLevelInteractiveEntity.Interactor.Both;
                    typeof(DialogueInteractionPoint).GetField("conversationIsActive", allFlags).SetValue(dip, true);
                    typeof(DialogueInteractionPoint).GetField("playerOneDialoguePosition", allFlags).SetValue(dip, new Vector2(-40f, 0f));
                    typeof(DialogueInteractionPoint).GetField("playerTwoDialoguePosition", allFlags).SetValue(dip, new Vector2(45f, 0f));

                    bool snapped = Sync.MonitorActiveDialoguesAndInteractions(true);
                    // Issue #1 & #5 (v1.3.2): playerOneDialoguePosition and playerTwoDialoguePosition are WORLD X coordinates (-40f / 45f), NOT offsets from dip.transform.position.x (100f)!
                    float expectedX = Sync.GetRemotePlayerId() == PlayerId.PlayerTwo ? 45f : -40f;
                    npcDialogueAutoPosVerified = snapped
                        && dip.interactor == AbstractLevelInteractiveEntity.Interactor.Either
                        && Mathf.Abs(remotePlayer.transform.position.x - expectedX) < 0.1f;

                    DestroyImmediate(dlgGo);
                    remotePlayer.transform.position = origRemotePos;
                }

                // 2. Stale Death Retry Guard & Invisible Spirit Self-Healing
                bool staleDeathRetryGuardVerified = false;
                if (remotePlayer != null && remotePlayer.stats != null)
                {
                    int origRemoteHp = remotePlayer.stats.Health;
                    Sync.SetIgnoreRemoteDeathWindow(2.5f);
                    byte[] staleDeathPkt = CoopProtocol.BuildLevelEvent(
                        LevelEventType.PlayerDeath,
                        (byte)Sync.GetRemotePlayerId(),
                        remotePlayer.transform.position.x,
                        remotePlayer.transform.position.y,
                        0);
                    Transport.InjectTestPayload(staleDeathPkt);
                    bool ignoredStaleDeath = !remotePlayer.IsDead && Sync.IsRemoteDeathCurrentlyIgnored;
                    Sync.SetIgnoreRemoteDeathWindow(0f);
                    remotePlayer.stats.SetHealth(origRemoteHp > 0 ? origRemoteHp : 3);
                    staleDeathRetryGuardVerified = ignoredStaleDeath;
                }

                // 3. Boss Phase Transition Sync + Mausoleum Ghost Parry & Map Level Select Sync Packets
                byte[] phasePkt = CoopProtocol.BuildLevelEvent(LevelEventType.BossPhaseChange, 1, 100f, 500f, 777777);
                byte[] ghostParryPkt = CoopProtocol.BuildLevelEvent(LevelEventType.MausoleumGhostParried, (byte)PlayerId.PlayerOne, 12f, 34f, 0);
                byte[] mapSelPkt = CoopProtocol.BuildMapLevelSelectSync(1, true, "Slime", (int)Level.Mode.Hard, 2);
                bool v130PacketsVerified = phasePkt != null && phasePkt.Length >= 15
                    && ghostParryPkt != null && ghostParryPkt.Length >= 15
                    && mapSelPkt != null && mapSelPkt.Length >= 10;

                // 4. (v1.3.1): Map Player Position Normalization (anti-offscreen camera trap) & DLC Boatman Disabler
                bool mapTetherAndBoatmanGuardVerified = false;
                if (PlayerData.Initialized && PlayerData.Data != null)
                {
                    PlayerData.MapData md2 = PlayerData.Data.GetMapData(Scenes.scene_map_world_2);
                    bool origStarted = md2.sessionStarted;
                    Vector3 origP1 = md2.playerOnePosition;
                    Vector3 origP2 = md2.playerTwoPosition;

                    md2.sessionStarted = true;
                    md2.playerOnePosition = new Vector3(-6f, 5f, 0f);
                    md2.playerTwoPosition = new Vector3(38f, -24f, 0f); // Distant Boatman dock vs Funfair Fever
                    bool normalized = CoopSyncController.NormalizeAllMapPlayerPositions();
                    bool tetheredClose = Vector2.Distance(
                        new Vector2(md2.playerOnePosition.x, md2.playerOnePosition.y),
                        new Vector2(md2.playerTwoPosition.x, md2.playerTwoPosition.y)) < 1.5f;

                    md2.sessionStarted = origStarted;
                    md2.playerOnePosition = origP1;
                    md2.playerTwoPosition = origP2;

                    GameObject boatRoot = new GameObject("TestBoatman");
                    GameObject boatEn = new GameObject("BoatmanEnable");
                    GameObject boatDis = new GameObject("BoatmanDisable");
                    boatEn.transform.SetParent(boatRoot.transform);
                    boatDis.transform.SetParent(boatRoot.transform);
                    boatEn.SetActive(true);
                    boatDis.SetActive(false);

                    BoatmanEnabler boatComp = boatRoot.AddComponent<BoatmanEnabler>();
                    var bFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    typeof(MapLevelDependentObstacle).GetField("ToEnable", bFlags).SetValue(boatComp, boatEn);
                    typeof(MapLevelDependentObstacle).GetField("ToDisable", bFlags).SetValue(boatComp, boatDis);

                    int disabledBoats = CoopSyncController.DisableDlcBoatmanOnMap();
                    bool boatDisabled = disabledBoats >= 1 && !boatEn.activeSelf && boatDis.activeSelf;
                    DestroyImmediate(boatRoot);

                    mapTetherAndBoatmanGuardVerified = normalized && tetheredClose && boatDisabled;
                }

                // 5. (v1.3.2): DialogueSync, EquipMenuSync, ShopStateSync, MausoleumGhostSync, EnemySync, Residual Motion Stop & Rich Context Logger
                byte[] dlgSyncPkt = CoopProtocol.BuildDialogueSync(1, 10, 0f, -120f, -60f);
                byte[] equipSyncPkt = CoopProtocol.BuildEquipMenuSync(true, 1, 1, 0, 2, 0, 1, false, (int)Weapon.level_weapon_peashot, 0, (int)Super.level_super_beam, 0);
                byte[] shopSyncPkt = CoopProtocol.BuildShopStateSync(1, 5, 2, true);
                MausoleumGhostEntry[] testGhosts = new MausoleumGhostEntry[]
                {
                    new MausoleumGhostEntry { GhostId = 101, GhostType = 1, PosX = 250f, PosY = 120f, AngleZ = 45f, Speed = 100f }
                };
                byte[] ghostSyncPkt = CoopProtocol.BuildMausoleumGhostSync(testGhosts, 1);
                EnemySyncEntry[] testEnemies = new EnemySyncEntry[]
                {
                    new EnemySyncEntry { EnemyKey = 202, TypeHash = 12345, PosX = 300f, PosY = -50f, Health = 25f, ScaleX = 1, StateFlags = 1 }
                };
                byte[] enemySyncPkt = CoopProtocol.BuildEnemySync(true, testEnemies, 1);
                string diagCtx = Sync.FormatSceneAndLevelContext("scene_map_world_1", (int)Levels.Veggies, (int)Level.Mode.Normal);
                CoopSyncController.StopAllResidualPlayerMovement();

                bool v132FixesVerified = dlgSyncPkt != null && dlgSyncPkt[0] == (byte)PacketType.DialogueSync
                    && equipSyncPkt != null && equipSyncPkt[0] == (byte)PacketType.EquipMenuSync
                    && shopSyncPkt != null && shopSyncPkt[0] == (byte)PacketType.ShopStateSync
                    && ghostSyncPkt != null && ghostSyncPkt[0] == (byte)PacketType.MausoleumGhostSync
                    && enemySyncPkt != null && enemySyncPkt[0] == (byte)PacketType.EnemySync
                    && !string.IsNullOrEmpty(diagCtx) && diagCtx.Contains("Inkwell Isle I");

                return string.Format(
                    "{{\"ok\":true,\"pauseLeaveProtected\":{0},\"loadoutSynced\":{1},\"inventorySynced\":{2},\"kdPacketValid\":{3},\"eventsValid\":{4},\"levelReadyGateVerified\":{5},\"pauseSyncVerified\":{6},\"sceneRequestVerified\":{7},\"worldSaveMirrorVerified\":{8},\"dlcAndVersionGuardVerified\":{9},\"coldLaunchInviteVerified\":{10},\"autoReconnectVerified\":{11},\"knockoutGateVerified\":{12},\"localizationVerified\":{13},\"updateCheckParserVerified\":{14},\"mapBridgeRefreshVerified\":{15},\"noDeadPlayerCloneVerified\":{16},\"remoteDamageGuardAndDeathSyncVerified\":{17},\"bossPatternRngSyncVerified\":{18},\"richPresenceVerified\":{19},\"copyLogVerified\":{20},\"npcDialogueAutoPosVerified\":{21},\"staleDeathRetryGuardVerified\":{22},\"v130PacketsVerified\":{23},\"mapTetherAndBoatmanGuardVerified\":{24},\"v132FixesVerified\":{25}}}",
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
                    bossPatternRngSyncVerified ? "true" : "false",
                    richPresenceVerified ? "true" : "false",
                    copyLogVerified ? "true" : "false",
                    npcDialogueAutoPosVerified ? "true" : "false",
                    staleDeathRetryGuardVerified ? "true" : "false",
                    v130PacketsVerified ? "true" : "false",
                    mapTetherAndBoatmanGuardVerified ? "true" : "false",
                    v132FixesVerified ? "true" : "false");
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"" + (ex.Message ?? "").Replace("\"", "'") + "\"}";
            }
        }

        public static string FormatRichPresenceText(string sceneName, int levelId, bool isCoopConnected, bool pt)
        {
            string suffix = isCoopConnected
                ? (pt ? " (2 Jogadores)" : " (2 Players)")
                : (pt ? " (Sozinho)" : " (Solo)");

            string s = (sceneName ?? string.Empty).Trim();

            if (s.IndexOf("scene_title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("scene_slot_select", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("scene_cutscene_intro", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (pt ? "No Menu Principal" : "In Main Menu") + suffix;
            }
            if (s.IndexOf("scene_map_world_1", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (pt ? "Explorando a Ilha Tinteiro I" : "Exploring Inkwell Isle I") + suffix;
            }
            if (s.IndexOf("scene_map_world_2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (pt ? "Explorando a Ilha Tinteiro II" : "Exploring Inkwell Isle II") + suffix;
            }
            if (s.IndexOf("scene_map_world_3", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (pt ? "Explorando a Ilha Tinteiro III" : "Exploring Inkwell Isle III") + suffix;
            }
            if (s.IndexOf("scene_map_world_4", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (pt ? "No Cassino do Rei Dado" : "In King Dice's Casino") + suffix;
            }
            if (s.IndexOf("scene_map_world_DLC", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (pt ? "Explorando a Ilha Tinteiro IV" : "Exploring Inkwell Isle IV") + suffix;
            }
            if (s.IndexOf("scene_shop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (pt ? "No Empório do Torresmo" : "In Porkrind's Emporium") + suffix;
            }
            if (s.IndexOf("scene_win", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return (pt ? "Tela de Vitória" : "Victory Screen") + suffix;
            }

            if (Enum.IsDefined(typeof(Levels), levelId))
            {
                Levels lvl = (Levels)levelId;
                bool isRunAndGun = lvl == Levels.Platforming_Level_1_1
                    || lvl == Levels.Platforming_Level_1_2
                    || lvl == Levels.Platforming_Level_2_1
                    || lvl == Levels.Platforming_Level_2_2
                    || lvl == Levels.Platforming_Level_3_1
                    || lvl == Levels.Platforming_Level_3_2;
                bool isMausoleum = lvl == Levels.Mausoleum
                    || s.IndexOf("mausoleum", StringComparison.OrdinalIgnoreCase) >= 0;

                string lvlName = GetOfficialLevelName(lvl, pt);
                if (!string.IsNullOrEmpty(lvlName))
                {
                    if (isRunAndGun || isMausoleum)
                        return (pt ? "Jogando " : "Playing ") + lvlName + suffix;
                    return (pt ? "Enfrentando " : "Fighting ") + lvlName + suffix;
                }
            }

            if (s.StartsWith("scene_level_", StringComparison.OrdinalIgnoreCase))
            {
                return (pt ? "Em Combate" : "In Battle") + suffix;
            }

            return (pt ? "Jogando Cuphead" : "Playing Cuphead") + suffix;
        }

        public static string GetOfficialLevelName(Levels level, bool pt)
        {
            switch (level)
            {
                // World 1 Bosses
                case Levels.Veggies: return pt ? "Botânico Rebelde" : "The Root Pack";
                case Levels.Slime: return pt ? "Goopy Le Grande" : "Goopy Le Grande";
                case Levels.Frogs: return pt ? "Ribby e Croaks" : "Ribby and Croaks";
                case Levels.FlyingBlimp: return pt ? "Hilda Berg" : "Hilda Berg";
                case Levels.Flower: return pt ? "Cagney Cravo" : "Cagney Carnation";

                // World 2 Bosses
                case Levels.Baroness: return pt ? "Baronesa Von Bombom" : "Baroness Von Bon Bon";
                case Levels.Clown: return pt ? "Beppi, o Palhaço" : "Beppi The Clown";
                case Levels.FlyingGenie: return pt ? "Djimmi, o Grande" : "Djimmi The Great";
                case Levels.Dragon: return pt ? "Grim Matchstick" : "Grim Matchstick";
                case Levels.FlyingBird: return pt ? "Wally Warbles" : "Wally Warbles";

                // World 3 Bosses
                case Levels.Bee: return pt ? "Rumor Honeybottoms" : "Rumor Honeybottoms";
                case Levels.Pirate: return pt ? "Capitão Brineybeard" : "Captain Brineybeard";
                case Levels.SallyStagePlay: return pt ? "Sally Stageplay" : "Sally Stageplay";
                case Levels.Mouse: return pt ? "Werner Werman" : "Werner Werman";
                case Levels.Robot: return pt ? "Robô do Dr. Kahl" : "Dr. Kahl's Robot";
                case Levels.FlyingMermaid: return pt ? "Cala Maria" : "Cala Maria";
                case Levels.Train: return pt ? "Expresso Fantasma" : "Phantom Express";

                // World 4 (King Dice & Devil)
                case Levels.DicePalaceMain:
                case Levels.DicePalaceBooze:
                case Levels.DicePalaceChips:
                case Levels.DicePalaceCigar:
                case Levels.DicePalaceDomino:
                case Levels.DicePalaceEightBall:
                case Levels.DicePalaceFlyingHorse:
                case Levels.DicePalaceFlyingMemory:
                case Levels.DicePalaceRabbit:
                case Levels.DicePalaceRoulette:
                    return pt ? "Rei Dado" : "King Dice";
                case Levels.Devil:
                    return pt ? "O Diabo" : "The Devil";

                // DLC (Inkwell Isle IV) Bosses
                case Levels.OldMan: return pt ? "Glumstone, o Gigante" : "Glumstone The Giant";
                case Levels.RumRunners: return pt ? "Máfia do Contrabando" : "Moonshine Mob";
                case Levels.Airplane: return pt ? "Os Irmãos Uivantes" : "The Howling Aces";
                case Levels.SnowCult: return pt ? "Mortimer Freeze" : "Mortimer Freeze";
                case Levels.FlyingCowboy: return pt ? "Esther Winchester" : "Esther Winchester";
                case Levels.Saltbaker: return pt ? "Chef Saltbaker" : "Chef Saltbaker";
                case Levels.Graveyard: return pt ? "Anjo e Demônio" : "Angel and Demon";
                case Levels.ChessPawn:
                case Levels.ChessKnight:
                case Levels.ChessBishop:
                case Levels.ChessRook:
                case Levels.ChessQueen:
                    return pt ? "Torre do Rei" : "The King's Leap";

                // Run & Gun + Mausoleum
                case Levels.Platforming_Level_1_1: return pt ? "Loucura na Floresta" : "Forest Follies";
                case Levels.Platforming_Level_1_2: return pt ? "Problema na Árvore" : "Treetop Trouble";
                case Levels.Platforming_Level_2_1: return pt ? "Febre do Circo" : "Funfair Fever";
                case Levels.Platforming_Level_2_2: return pt ? "Casa de Diversões" : "Funhouse Frazzle";
                case Levels.Platforming_Level_3_1: return pt ? "Perigo no Píer" : "Perilous Piers";
                case Levels.Platforming_Level_3_2: return pt ? "Cume Tempestuoso" : "Rugged Ridge";
                case Levels.Mausoleum: return pt ? "Mausoléu" : "Mausoleum";
                default:
                    return string.Empty;
            }
        }

        private void UpdateSteamRichPresence(bool force)
        {
            if (CoopOnlineInterface.TestModeNoSteamWrite)
                return;

            try
            {
                if (!CoopNetworkTransport.IsSteamInitialized())
                    return;

                if (!_richPresenceEnabled)
                {
                    if (_lastAppliedRichPresence != "__CLEARED__")
                    {
                        Steamworks.SteamFriends.ClearRichPresence();
                        _lastAppliedRichPresence = "__CLEARED__";
                    }
                    return;
                }

                string sceneName = SceneManager.GetActiveScene().name;
                int levelId = Level.Current != null ? (int)Level.Current.CurrentLevel : (int)Level.PreviousLevel;
                if (sceneName == null || !sceneName.StartsWith("scene_level_", StringComparison.OrdinalIgnoreCase))
                {
                    if (Level.Current == null)
                        levelId = -1;
                }

                bool isConnected = Transport != null && Transport.IsConnected;
                bool pt = IsPortugueseActive();
                string statusText = FormatRichPresenceText(sceneName, levelId, isConnected, pt);

                if (!force && statusText == _lastAppliedRichPresence)
                    return;

                Steamworks.SteamFriends.SetRichPresence("status", statusText);
                _lastAppliedRichPresence = statusText;
            }
            catch { }
        }

        private string GetCurrentActivityText(bool pt)
        {
            try
            {
                string sceneName = SceneManager.GetActiveScene().name;
                int levelId = Level.Current != null ? (int)Level.Current.CurrentLevel : (int)Level.PreviousLevel;
                if (sceneName == null || !sceneName.StartsWith("scene_level_", StringComparison.OrdinalIgnoreCase))
                {
                    if (Level.Current == null)
                        levelId = -1;
                }
                bool isConnected = Transport != null && Transport.IsConnected;
                return FormatRichPresenceText(sceneName, levelId, isConnected, pt);
            }
            catch
            {
                return pt ? "Jogando Cuphead" : "Playing Cuphead";
            }
        }

        public bool CopyRecentLogToClipboard()
        {
            try
            {
                string path = Path.GetFullPath("CupheadCoop.log");
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    GUIUtility.systemCopyBuffer = "CupheadCoop v" + CoopProtocol.ModVersion + " - Log file not found.";
                    _logCopiedFeedbackUntil = Time.unscaledTime + 3.0f;
                    return true;
                }

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                int lastSessionIdx = -1;
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i] != null && lines[i].IndexOf("InitializeEarly()", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        lastSessionIdx = i;
                        break;
                    }
                }

                int start = lastSessionIdx >= 0
                    ? Math.Max(0, lastSessionIdx)
                    : Math.Max(0, lines.Length - 180);
                if (lines.Length - start > 180)
                    start = lines.Length - 180;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== CupheadCoop v" + CoopProtocol.ModVersion + " Log ===");
                for (int i = start; i < lines.Length; i++)
                {
                    sb.AppendLine(lines[i]);
                }
                GUIUtility.systemCopyBuffer = sb.ToString();
                _logCopiedFeedbackUntil = Time.unscaledTime + 3.0f;
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    GUIUtility.systemCopyBuffer = "CupheadCoop v" + CoopProtocol.ModVersion + " Log Error: " + ex.Message;
                    _logCopiedFeedbackUntil = Time.unscaledTime + 3.0f;
                }
                catch { }
                return false;
            }
        }

        private string GetLocalPlayerDisplayName(bool pt)
        {
            try
            {
                if (CoopNetworkTransport.IsSteamInitialized())
                {
                    string name = Steamworks.SteamFriends.GetPersonaName();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
            catch { }
            return pt ? "Voce" : "You";
        }

        private string GetRemotePlayerDisplayName(bool pt)
        {
            if (Transport == null || !Transport.IsConnected)
                return pt ? "Aguardando parceiro..." : "Waiting for partner...";

            try
            {
                if (Transport.UsingSteamP2P && Transport.RemoteSteamId.IsValid() && CoopNetworkTransport.IsSteamInitialized())
                {
                    string friendName = Steamworks.SteamFriends.GetFriendPersonaName(Transport.RemoteSteamId);
                    if (!string.IsNullOrEmpty(friendName))
                        return friendName;
                }
            }
            catch { }

            return Transport.Role == CoopRole.Host ? "Player 2" : "Host";
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

            bool dlcLockedInSession = Transport.IsConnected && !Sync.BothPlayersHaveDlc;
            int boxHeight = (_updateAvailable ? 485 : 445) + (dlcLockedInSession ? 22 : 0);
            GUI.Box(new Rect(12, 12, 435, boxHeight), title);

            // Language toggle pill in the top-right corner of the box (wider so "Idioma: Auto (PT)" never clips)
            string langBtnLabel = _languageMode == 0
                ? (pt ? "Idioma: Auto (PT)" : "Lang: Auto (EN)")
                : (pt ? "Idioma: PT" : "Lang: EN");
            if (GUI.Button(new Rect(324, 34, 118, 20), langBtnLabel))
            {
                _languageMode = (_languageMode + 1) % 3;
                UpdateSteamRichPresence(true);
            }

            GUILayout.BeginArea(new Rect(22, 38, 415, boxHeight - 32));
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

            GUIStyle richLabelStyle = new GUIStyle(GUI.skin.label) { richText = true };
            string dlcStatus;
            if (!Transport.IsConnected)
            {
                if (CoopSyncController.IsLocalDlcEnabled())
                    dlcStatus = pt ? "Ativa (Detectada)" : "Active (Detected)";
                else
                    dlcStatus = pt ? "Desativada" : "Not detected";
            }
            else if (Sync.BothPlayersHaveDlc)
            {
                dlcStatus = pt ? "Ativa (Ambos possuem)" : "Active (Both players own DLC)";
            }
            else if (CoopSyncController.IsLocalDlcEnabled())
            {
                dlcStatus = pt
                    ? "<color=#FF6B6B><b>[!]</b></color> Bloqueada (Parceiro sem DLC)"
                    : "<color=#FF6B6B><b>[!]</b></color> Locked (Partner lacks DLC)";
            }
            else
            {
                dlcStatus = pt
                    ? "<color=#FF6B6B><b>[!]</b></color> Desativada (Apenas jogo base)"
                    : "<color=#FF6B6B><b>[!]</b></color> Disabled (Base game only)";
            }
            GUILayout.Label("DLC (Delicious Last Course): " + dlcStatus, richLabelStyle);

            if (dlcLockedInSession)
            {
                string dlcLockExplain = pt
                    ? "<color=#FF6B6B><b>[!]</b></color> Conteudo da DLC (Ilha IV, barco, armas, Chalice) bloqueado nesta sessao."
                    : "<color=#FF6B6B><b>[!]</b></color> DLC content (Isle IV, boat, weapons, Chalice) locked this session.";
                GUILayout.Label(dlcLockExplain, richLabelStyle);
            }

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

            GUILayout.Space(4);

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

                GUILayout.Space(4);
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
                // Connected Players List with Role & Ping
                string localName = GetLocalPlayerDisplayName(pt);
                string remoteName = GetRemotePlayerDisplayName(pt);
                int pingMs = Mathf.RoundToInt(Transport.LastRttMs);
                string hostLine;
                string p2Line;
                if (Transport.Role == CoopRole.Host)
                {
                    hostLine = string.Format(pt ? "  [Host] {0} (Voce) - 0 ms" : "  [Host] {0} (You) - 0 ms", localName);
                    p2Line = Transport.IsConnected
                        ? string.Format("  [Player 2] {0} - {1} ms", remoteName, pingMs)
                        : ("  [Player 2] " + remoteName);
                }
                else
                {
                    hostLine = Transport.IsConnected
                        ? string.Format("  [Host] {0} - {1} ms", remoteName, pingMs)
                        : ("  [Host] " + remoteName);
                    p2Line = string.Format(pt ? "  [Player 2] {0} (Voce) - {1} ms" : "  [Player 2] {0} (You) - {1} ms", localName, pingMs);
                }
                GUILayout.Label(pt ? "Jogadores na Sessao:" : "Players in Session:");
                GUILayout.Label(hostLine);
                GUILayout.Label(p2Line);

                if (Transport.UsingSteamP2P && Transport.CurrentLobbyId.IsValid())
                {
                    string code = Transport.CurrentLobbyId.m_SteamID.ToString();
                    GUILayout.Label((pt ? "Codigo da sua Sala Steam: " : "Your Steam Lobby Code: ") + code);

                    bool showCodeCopied = Time.unscaledTime < _codeCopiedFeedbackUntil;
                    string copyCodeLabel = showCodeCopied
                        ? (pt ? "✔ Codigo Copiado!" : "✔ Code Copied!")
                        : (pt ? "Copiar Codigo [F6]" : "Copy Code [F6]");

                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(copyCodeLabel))
                    {
                        GUIUtility.systemCopyBuffer = code;
                        _codeCopiedFeedbackUntil = Time.unscaledTime + 2.5f;
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
                    UpdateSteamRichPresence(true);
                }
            }

            GUILayout.Space(6);

            // Row 1 of Footer: Standardized GitHub + Nexus Mods (Feedback & Bugs)
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(pt ? "GitHub (Feedback & Bugs)" : "GitHub (Feedback & Bugs)"))
            {
                Application.OpenURL(GitHubRepoUrl);
            }
            if (GUILayout.Button(pt ? "Nexus Mods (Feedback & Bugs)" : "Nexus Mods (Feedback & Bugs)"))
            {
                Application.OpenURL(NexusModsUrl);
            }
            GUILayout.EndHorizontal();

            // Optional Dual Download Row when a new update is available
            if (_updateAvailable)
            {
                GUIStyle updStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                string updNotice = pt
                    ? ("Nova versao v" + _latestRemoteVersion + " disponivel! Escolha onde baixar:")
                    : ("New version v" + _latestRemoteVersion + " available! Choose download:");
                GUILayout.Label(updNotice, updStyle);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(pt ? "Baixar no GitHub" : "Download on GitHub"))
                {
                    Application.OpenURL(GitHubReleasesUrl);
                }
                if (GUILayout.Button(pt ? "Baixar no Nexus Mods" : "Download on Nexus Mods"))
                {
                    Application.OpenURL(NexusModsUrl);
                }
                GUILayout.EndHorizontal();
            }

            // Bottom Row of Footer: Centered Version status + Copy Log to Clipboard with Instant Feedback
            GUILayout.BeginHorizontal();
            GUIStyle verStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            string verLabel = _updateAvailable
                ? ("v" + CoopProtocol.ModVersion + " -> v" + _latestRemoteVersion)
                : (_updateCheckCompleted
                    ? (pt ? ("v" + CoopProtocol.ModVersion + " (Atualizado)") : ("v" + CoopProtocol.ModVersion + " (Up to date)"))
                    : ("v" + CoopProtocol.ModVersion));
            GUILayout.Label(verLabel, verStyle, GUILayout.Width(185), GUILayout.Height(22));

            bool showCopied = Time.unscaledTime < _logCopiedFeedbackUntil;
            string copyLogLabel = showCopied
                ? (pt ? "✔ Log Copiado!" : "✔ Log Copied!")
                : (pt ? "Copiar Log (CupheadCoop.log)" : "Copy Log (CupheadCoop.log)");
            if (GUILayout.Button(copyLogLabel))
            {
                CopyRecentLogToClipboard();
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
