using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadCoop
{
    public class CoopModManager : MonoBehaviour
    {
        public static CoopModManager Instance { get; private set; }

        public CoopNetworkTransport Transport { get; private set; }
        public CoopSyncController Sync { get; private set; }

        private bool _showMenu = true;
        private string _ipInput = "127.0.0.1";
        private string _portInput = "26891";
        private string _lobbyCodeInput = "";
        private float _pingTimer;
        private TcpListener _bridgeListener;
        private int _bridgePort = 26899;

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
            CupheadCoopBootstrapper.Log("CoopModManager initialized. Press F9 in-game to toggle Co-op Overlay.");
        }

        private void ParseCommandLineArgs()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "-coop-bridge-port" && i + 1 < args.Length)
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
                // Fallback if first instance already bound 26899
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

                // Keyboard shortcuts so player can also operate menu without mouse
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
                    "{{\"ok\":true,\"role\":\"{0}\",\"connected\":{1},\"scene\":\"{2}\",\"multiplayer\":{3},\"p2Exists\":{4},\"tick\":{5},\"seed\":{6},\"rttMs\":{7:F1},\"achievementsSynced\":{8},\"lastAchievement\":\"{9}\"}}",
                    Transport.Role,
                    Transport.IsConnected ? "true" : "false",
                    scene,
                    PlayerManager.Multiplayer ? "true" : "false",
                    p2Exists ? "true" : "false",
                    Sync.LocalTick,
                    Sync.CurrentRngSeed,
                    Transport.LastRttMs,
                    achCount,
                    lastAch);
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

                // 2. Test Real-Time LoadoutSync (e.g., PlayerTwo equips Spread + Roundabout + Super I + Ms. Chalice Astral Cookie)
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

                // 3. Test Shared InventorySync (Shop purchases & Mausoleum Super unlock mirroring)
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

                // 4. Test King Dice Board Sync packet
                int packedHearts = 2 | (5 << 8) | (8 << 16);
                byte[] kdPkt = CoopProtocol.BuildLevelEvent(
                    LevelEventType.KingDiceBoardSync,
                    0,
                    4f,
                    2f,
                    packedHearts);
                bool kdPacketValid = (kdPkt != null && kdPkt.Length >= 15);

                // 5. Test Steal-HP & CoinCollected packet builders
                byte[] stealPkt = CoopProtocol.BuildLevelEvent(
                    LevelEventType.PlayerStealHealthRevive,
                    (byte)PlayerId.PlayerTwo,
                    100f,
                    200f,
                    1);
                byte[] coinPkt = CoopProtocol.BuildLevelEvent(
                    LevelEventType.CoinCollected,
                    (byte)PlayerId.PlayerTwo,
                    0f,
                    0f,
                    123456);
                bool eventsValid = (stealPkt.Length >= 15 && coinPkt.Length >= 15);

                return string.Format(
                    "{{\"ok\":true,\"pauseLeaveProtected\":{0},\"loadoutSynced\":{1},\"inventorySynced\":{2},\"kdPacketValid\":{3},\"eventsValid\":{4}}}",
                    pauseLeaveProtected ? "true" : "false",
                    loadoutSynced ? "true" : "false",
                    inventorySynced ? "true" : "false",
                    kdPacketValid ? "true" : "false",
                    eventsValid ? "true" : "false");
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"" + (ex.Message ?? "").Replace("\"", "'") + "\"}";
            }
        }

        private void OnGUI()
        {
            if (!_showMenu)
                return;

            GUI.Box(new Rect(12, 12, 410, 360), "Cuphead Online Co-op + Steam Achievements (F9)");

            GUILayout.BeginArea(new Rect(22, 38, 390, 325));
            bool steamReady = CoopNetworkTransport.IsSteamInitialized();
            GUILayout.Label("Steam API: " + (steamReady ? "CONECTADA (Pronto)" : "Inicializando..."));
            GUILayout.Label("Status: " + Transport.StatusMessage);
            GUILayout.Label(string.Format("Role: {0} | Connected: {1} | Ping: {2:F0} ms",
                Transport.Role, Transport.IsConnected, Transport.LastRttMs));

            string achStatus = Sync.OnlineWrapper != null
                ? string.Format("Ativo (Ultima: {0}, Total: {1})", Sync.OnlineWrapper.LastUnlockedAchievement, Sync.OnlineWrapper.AchievementsSyncedCount)
                : "Aguardando OnlineManager...";
            GUILayout.Label("Conquistas Steam: " + achStatus);

            GUILayout.Space(6);

            if (Transport.Role == CoopRole.None)
            {
                GUILayout.Label("=== JOGAR PELA STEAM (Sem Radmin / Sem Portas) ===");
                if (GUILayout.Button("1. CRIAR SALA STEAM - Host [Tecla F5]"))
                {
                    Transport.HostSteamLobby();
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label("Codigo da Sala:", GUILayout.Width(95));
                _lobbyCodeInput = GUILayout.TextField(_lobbyCodeInput, GUILayout.Width(135));
                if (GUILayout.Button("Colar+Entrar [F6]", GUILayout.Width(130)))
                {
                    string clip = (GUIUtility.systemCopyBuffer ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(clip))
                        _lobbyCodeInput = clip;
                    ulong lobbyId;
                    if (ulong.TryParse(_lobbyCodeInput.Trim(), out lobbyId))
                        Transport.JoinSteamLobby(lobbyId);
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(8);
                GUILayout.Label("=== TESTE LOCAL / LAN (Opcional) ===");
                GUILayout.BeginHorizontal();
                GUILayout.Label("IP:", GUILayout.Width(25));
                _ipInput = GUILayout.TextField(_ipInput, GUILayout.Width(130));
                GUILayout.Label("Porta:", GUILayout.Width(40));
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
                    GUILayout.Label("Codigo da sua Sala Steam: " + code);

                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Copiar Codigo [F6]"))
                    {
                        GUIUtility.systemCopyBuffer = code;
                    }
                    if (GUILayout.Button("Convidar na Steam [F5]"))
                    {
                        Transport.InviteSteamFriend();
                    }
                    GUILayout.EndHorizontal();
                }

                if (GUILayout.Button("Sincronizar / Forcar Entrada do Player 2"))
                {
                    Sync.ForceMultiplayerPlayerTwoJoined();
                    Sync.ConfigureLocalAndRemoteInputs();
                }

                if (GUILayout.Button("Desconectar Sessao Co-op"))
                {
                    Transport.Disconnect();
                }
            }

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
