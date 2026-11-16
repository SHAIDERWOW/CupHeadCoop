using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Rewired;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadCoop
{
    public class CoopSyncController
    {
        private readonly CoopNetworkTransport _transport;
        private CoopOnlineInterface _onlineWrapper;

        private uint _localTick;
        private uint _sessionEpoch = 1;
        private int _currentRngSeed = 1337;
        private string _lastSyncedScene = string.Empty;
        private int _sceneLoadSequence = 1;
        private int _lastReceivedSceneLoadSeq = -1;

        private Level _hookedLevel;
        private int _lastHookedLevelInstanceId;
        private AbstractPlayerController _hookedLocalPlayer;
        private float _lastSentBossDamage;
        private float _lastSentBossPropertiesHp = -1f;
        private bool _applyingRemoteSceneLoad;
        private bool _applyingRemoteWeaponAction;
        private bool _applyingRemoteDeath;
        private bool _wasLocalShooting;
        private bool _wasRemoteParrying;
        private bool _wasRemotePlaneShrunk;
        private bool _wasRemotePlaneShooting;
        private int _lastSentPlaneWeaponId;

        // Real-time Loadout & Inventory tracking
        private int _lastSentPrimaryWep;
        private int _lastSentSecondaryWep;
        private int _lastSentSuper;
        private int _lastSentCharm;
        private int _lastSyncedInventoryCount = -1;
        private bool _lastDjimmiState;

        // 1. LevelReadyGate state
        private bool _waitingAtLevelReadyGate;
        private bool _gatePausedGameplay;
        private int _levelReadyGateLevelId = -1;
        private int _remoteReadyForLevelId = -1;
        private float _levelReadyGateStartTime;
        private float _nextLevelReadyBroadcastTime;

        // 2. PauseSync state
        private bool _applyingRemotePause;
        private bool _isPausedByPauseSync;
        private bool _wasLocalPauseGuiOpen;
        private LevelPauseGUI _hookedPauseGui;

        // 3. SceneRequestSync state (Guest-initiated Retry / ExitToMap / Level Entry)
        private bool _wasSceneLoaderLoading;
        private string _lastClientRequestedScene = string.Empty;
        private float _lastClientRequestTime = -100f;
        private bool _clientInitiatedReload;
        private float _clientInitiatedReloadTime = -100f;
        private float _ignoreClientSceneRequestUntilTime = -100f;
        private bool _hasEnteredGameplayScene;

        // 4. WorldSaveMirror + Dialoguer in-RAM state (protects Client disk saves)
        private ushort _nextMirrorId = 1;
        private ushort _receivingMirrorId;
        private byte _receivingMirrorSlot;
        private ushort _receivingMirrorTotalChunks;
        private byte[][] _receivingMirrorChunks;
        private int _receivingMirrorReceivedCount;
        private bool _clientOriginalSavesBackedUp;
        private readonly string[] _clientOriginalSavesJson = new string[3];
        private int _clientOriginalSlotIndex;
        private string _clientOriginalDialoguerXml = string.Empty;
        private int _worldSaveMirrorsAppliedCount;

        // 5. 3-Way Handshake + Version Check + DLC Guard state
        private bool _handshakeCompleted;
        private bool _remoteHasDlc = true;
        private string _remoteModVersion = string.Empty;
        private string _lastDlcWarning = string.Empty;
        private string _lastVersionMismatchError = string.Empty;
        public static bool ForceSimulateNoDlcForTest { get; set; }

        // 8. LevelOutcomeSync (Knockout Gate + Boss AbstractLevelProperties reflection cache)
        private Level _cachedBossPropsLevel;
        private object _cachedBossProps;
        private PropertyInfo _bossCurrentHealthProp;
        private FieldInfo _bossCurrentHealthBackingField;
        private FieldInfo _bossTotalHealthField;
        private MethodInfo _bossDealDamageMethod;
        private MethodInfo _bossDealDamageToNextNamedStateMethod;
        private MethodInfo _bossGetNextStateHealthTriggerMethod;
        private MethodInfo _bossWinInstantlyMethod;
        private FieldInfo _bossStateIndexField;
        private float _lastHostBossCurrentHealth = -1f;
        private int _lastHostBossStateIndex;
        private bool _hostConfirmedLevelWin;

        // 9. Retry Stale-Death Guard & Spirit Self-Healing (v1.3.0)
        private float _ignoreRemoteDeathUntilTime;
        private int _consecutiveRemoteAlivePacketsWhileDead;

        // 10. Mausoleum Sync State (v1.3.0)
        private int _hookedMausoleumInstanceId;
        private bool _sentMausoleumLose;
        private readonly HashSet<int> _parriedMausoleumGhostIds = new HashSet<int>();

        // 11. Map Level Select / Difficulty UI Sync State (v1.3.0 / v1.3.2)
        private bool _applyingRemoteMapStartUi;
        private bool _isRemoteMapStartUiOpen;
        private byte _remoteMapStartUiKind;
        private bool _wasLocalMapStartUiOpen;
        private byte _lastSentMapStartUiKind;
        private string _lastSentMapStartLevel = string.Empty;
        private int _lastSentMapStartDiff = -1;
        private int _lastSentMapStartCursor = -1;
        private float _ignoreLocalMapStartUiOpenUntilTime = -100f;

        // 12. Dialogue Sync State (v1.3.2 - Issue #1 & #5)
        private bool _applyingRemoteDialogue;
        private readonly HashSet<int> _activeDialogueInstanceIds = new HashSet<int>();
        private readonly Dictionary<int, float> _dialogueStartTimeByInstanceId = new Dictionary<int, float>();

        // 13. Map Equip Menu Sync State (v1.3.2 - Issue #8)
        private bool _applyingRemoteEquipMenu;
        private bool _wasLocalEquipMenuOpen;
        private bool _isRemoteEquipMenuOpen;
        private float _ignoreLocalEquipMenuUntilTime = -100f;
        private byte _lastSentEquipSide = 255;
        private byte _lastSentEquipBack = 255;
        private byte _lastSentEquipSlot = 255;
        private int _lastSentEquipFrontIdx = -1;
        private int _lastSentEquipBackIdx = -1;
        private bool _lastSentEquipReady;

        // 14. Shop Scene Co-Wait Sync State (v1.3.2 - Issue #11)
        private bool _applyingRemoteShopState;
        private byte _lastSentShopState = 255;
        private int _lastSentShopItemIdx = -1;
        private bool _lastSentShopExiting;

        // 15. Mausoleum Ghost & Enemy/Minion Sync State (v1.3.2 - Issue #5 & #10)
        private readonly Dictionary<int, MausoleumLevelGhostBase> _clientBoundMausoleumGhosts = new Dictionary<int, MausoleumLevelGhostBase>();
        private readonly HashSet<int> _startedSpawnerHashes = new HashSet<int>();
        private readonly HashSet<int> _endedSpawnerHashes = new HashSet<int>();
        private readonly Dictionary<int, float> _clientEnemyLastHostSeenTime = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _clientEnemySpawnGraceTime = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _hostEnemyLastKnownHp = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector2> _hostEnemyLastKnownPos = new Dictionary<int, Vector2>();
        private readonly Dictionary<int, int> _hostEnemyLastKnownType = new Dictionary<int, int>();
        private readonly HashSet<int> _killedEnemyKeys = new HashSet<int>();
        private readonly Dictionary<int, float> _clientEnemyLastReportedHp = new Dictionary<int, float>();

        // 16. World Map Facing & Diagnostic Logging State (v1.3.2 - Issue #12 & Rich Logs)
        private sbyte _lastRemoteMapFacingX;
        private sbyte _lastRemoteMapFacingY = -1;
        private float _lastTetherLogTime = -100f;

        // DeathEffect & LevelCoin & KingDice tracking
        private readonly HashSet<int> _processedDeathEffects = new HashSet<int>();
        private readonly HashSet<int> _syncedCoinHashes = new HashSet<int>();
        private int _lastSyncedDiceInstanceId;
        private bool _sentKingDiceBoardInit;

        // Map post-win unstick & deferred obstacle refresh tracking
        private int _lastTrackedMapInstanceId;
        private float _mapSceneEnteredTime;
        private bool _pendingMapObstacleRefresh;

        private static readonly BindingFlags AllFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public uint LocalTick { get { return _localTick; } }
        public int CurrentRngSeed { get { return _currentRngSeed; } }
        public CoopOnlineInterface OnlineWrapper { get { return _onlineWrapper; } }
        public bool HandshakeCompleted { get { return _handshakeCompleted; } }
        public bool BothPlayersHaveDlc { get { return IsLocalDlcEnabled() && _remoteHasDlc; } }
        public string LastDlcWarning { get { return _lastDlcWarning; } }
        public string LastVersionMismatchError { get { return _lastVersionMismatchError; } }
        public bool IsWaitingAtLevelReadyGate { get { return _waitingAtLevelReadyGate; } }
        public bool IsPausedByPauseSync { get { return _isPausedByPauseSync; } }
        public int WorldSaveMirrorsAppliedCount { get { return _worldSaveMirrorsAppliedCount; } }
        public bool HostConfirmedLevelWin { get { return _hostConfirmedLevelWin; } }
        public bool IsRemoteDeathCurrentlyIgnored { get { return _waitingAtLevelReadyGate || Time.unscaledTime < _ignoreRemoteDeathUntilTime; } }
        public bool IsRemoteMapStartUiOpen { get { return _isRemoteMapStartUiOpen; } }

        public void SetIgnoreRemoteDeathWindow(float durationSeconds)
        {
            _ignoreRemoteDeathUntilTime = Time.unscaledTime + Mathf.Max(0f, durationSeconds);
            _consecutiveRemoteAlivePacketsWhileDead = 0;
        }

        public CoopSyncController(CoopNetworkTransport transport)
        {
            _transport = transport;
            _transport.OnPeerConnected += OnPeerConnected;
            _transport.OnPeerDisconnected += OnPeerDisconnected;
            _transport.OnPacketReceived += OnPacketReceived;
        }

        public static bool IsLocalDlcEnabled()
        {
            if (ForceSimulateNoDlcForTest)
                return false;
            try
            {
                return DLCManager.DLCEnabled();
            }
            catch
            {
                return false;
            }
        }

        public void EnsureOnlineWrapper()
        {
            if (_onlineWrapper == null)
            {
                _onlineWrapper = CoopOnlineInterface.Install(_transport);
            }
        }

        public PlayerId GetLocalPlayerId()
        {
            return _transport.Role == CoopRole.Client ? PlayerId.PlayerTwo : PlayerId.PlayerOne;
        }

        public PlayerId GetRemotePlayerId()
        {
            return _transport.Role == CoopRole.Client ? PlayerId.PlayerOne : PlayerId.PlayerTwo;
        }

        private void OnPeerConnected()
        {
            EnsureOnlineWrapper();
            _handshakeCompleted = false;
            _lastVersionMismatchError = string.Empty;
            ForceMultiplayerPlayerTwoJoined();
            MirrorAndSyncPlayerInventories(true);
            ConfigureLocalAndRemoteInputs();

            if (_transport.Role == CoopRole.Host)
            {
                _sessionEpoch++;
                _currentRngSeed = (Math.Abs(Environment.TickCount ^ 0x5A17C9) & 0x007FFFFF) | 1;
                UnityEngine.Random.InitState(_currentRngSeed);
            }

            SendHandshakeStage(HandshakeStage.Hello);
            SendLocalLoadout(true);

            if (_transport.Role == CoopRole.Host)
            {
                SendWorldSaveMirror();
            }
        }

        private void SendHandshakeStage(HandshakeStage stage)
        {
            PlayerId localId = GetLocalPlayerId();
            int pWeapon = 0, sWeapon = 0, superId = 0, charmId = 0;
            if (PlayerData.Initialized && PlayerData.Data != null)
            {
                PlayerData.PlayerLoadouts.PlayerLoadout loadout =
                    PlayerData.Data.Loadouts.GetPlayerLoadout(localId);
                if (loadout != null)
                {
                    pWeapon = (int)loadout.primaryWeapon;
                    sWeapon = (int)loadout.secondaryWeapon;
                    superId = (int)loadout.super;
                    charmId = (int)loadout.charm;
                }
            }

            SanitizeLoadoutForDlc(ref pWeapon, ref sWeapon, ref superId, ref charmId);
            byte[] handshake = CoopProtocol.BuildHandshake(
                _sessionEpoch,
                _currentRngSeed,
                pWeapon,
                sWeapon,
                superId,
                charmId,
                IsLocalDlcEnabled(),
                stage,
                CoopProtocol.ModVersion);
            _transport.Send(handshake, true);
        }

        public string FormatSceneAndLevelContext(string sceneName, int levelId, int difficultyMode)
        {
            string friendlyScene = sceneName ?? "unknown_scene";
            if (friendlyScene == "scene_map_world_1") friendlyScene = "Inkwell Isle I (World Map 1)";
            else if (friendlyScene == "scene_map_world_2") friendlyScene = "Inkwell Isle II (World Map 2)";
            else if (friendlyScene == "scene_map_world_3") friendlyScene = "Inkwell Isle III (World Map 3)";
            else if (friendlyScene == "scene_map_world_4") friendlyScene = "Inkwell Hell (World Map 4)";
            else if (friendlyScene == "scene_map_world_DLC") friendlyScene = "Inkwell Isle IV (DLC World Map)";
            else if (friendlyScene == "scene_shop") friendlyScene = "Porkrind's Emporium (Shop)";
            else if (friendlyScene == "scene_shop_DLC") friendlyScene = "Porkrind's Emporium (DLC Shop)";
            else if (friendlyScene == "scene_level_house_elder_kettle") friendlyScene = "Elder Kettle's House";
            else if (friendlyScene == "scene_level_kitchen") friendlyScene = "Chef Saltbaker's Bakery";

            string lvlLabel = "None";
            if (levelId != 0 && Enum.IsDefined(typeof(Levels), levelId))
            {
                lvlLabel = ((Levels)levelId).ToString();
            }
            else if (Level.Current != null)
            {
                lvlLabel = Level.Current.CurrentLevel.ToString();
            }

            string diffLabel = "Regular";
            if (Enum.IsDefined(typeof(Level.Mode), difficultyMode))
            {
                Level.Mode m = (Level.Mode)difficultyMode;
                diffLabel = (m == Level.Mode.Easy) ? "Simple" : (m == Level.Mode.Hard ? "Expert" : "Regular");
            }

            return string.Format(
                "{0} [raw={1}, level={2}, diff={3}, role={4}, LocalDLC={5}, PartnerDLC={6}, BothDLC={7}]",
                friendlyScene,
                sceneName ?? string.Empty,
                lvlLabel,
                diffLabel,
                _transport.Role,
                IsLocalDlcEnabled(),
                _remoteHasDlc,
                BothPlayersHaveDlc);
        }

        private void OnPeerDisconnected()
        {
            _handshakeCompleted = false;
            _wasLocalShooting = false;
            _wasRemoteParrying = false;
            _wasRemotePlaneShrunk = false;
            _wasRemotePlaneShooting = false;
            _isRemoteEquipMenuOpen = false;
            _wasLocalEquipMenuOpen = false;
            ReleaseLevelReadyGate();
            if (_isPausedByPauseSync)
            {
                ApplyRemotePauseSync(false);
            }
            _isPausedByPauseSync = false;

            // Issue #12 (v1.3.2): Immediately halt any residual movement/velocity/animator state on both players
            // so neither character continues walking or sliding forward when a peer leaves or drops!
            StopAllResidualPlayerMovement();

            if (_transport.Role == CoopRole.None && _transport.AutoReconnectAttemptsLeft == 0)
            {
                RestoreClientOriginalSavesIfBackedUp();
                try
                {
                    PlayerManager.PlayerLeave(PlayerId.PlayerTwo);
                    PlayerManager.Multiplayer = false;
                    if (Map.Current != null && Map.Current.players != null && Map.Current.players.Length > 1 && Map.Current.players[1] != null)
                    {
                        UnityEngine.Object.Destroy(Map.Current.players[1].gameObject);
                    }
                }
                catch { }
            }
            RestoreDefaultInputs();
            CupheadCoopBootstrapper.Log("[CoopDiag] Peer disconnected (role=" + _transport.Role + ", reconnectLeft=" + _transport.AutoReconnectAttemptsLeft + "). Restored inputs and stopped motion.");
        }

        public void OnSceneLoaded(string sceneName)
        {
            EnsureOnlineWrapper();

            // Issue #6 (v1.3.1): Returning to the Title Screen after entering gameplay automatically closes the lobby.
            // Never close the lobby on scene_slot_select (where the Host selects which save slot to load!).
            if (sceneName == "scene_title")
            {
                if (_hasEnteredGameplayScene && (_transport.IsConnected || _transport.Role != CoopRole.None))
                {
                    _hasEnteredGameplayScene = false;
                    CupheadCoopBootstrapper.Log("[CoopDiag] Returned to Main Menu (" + sceneName + "); automatically closing Co-op lobby.");
                    _transport.Disconnect();
                }
                return;
            }

            if (Map.Current != null || Level.Current != null)
            {
                _hasEnteredGameplayScene = true;
            }

            if (!_transport.IsConnected)
                return;

            // Ensure PauseManager is never left stuck in Paused state when transitioning scenes from the Pause Menu
            if (PauseManager.state == PauseManager.State.Paused && !_waitingAtLevelReadyGate)
            {
                PauseManager.Unpause();
            }

            // Hard DLC Scene Redirect (v1.3.1): If the Client loaded a DLC scene locally while !BothPlayersHaveDlc,
            // immediately bounce back to the Host's current map scene so the Client never gets stranded on the DLC island!
            if (_transport.Role == CoopRole.Client && !BothPlayersHaveDlc && IsDlcSceneOrLevel(sceneName, (int)SceneLoader.CurrentLevel))
            {
                _lastDlcWarning = "BLOQUEADO: O Host nao possui a DLC! Retornando para a ilha do Host.";
                CupheadCoopBootstrapper.Log(_lastDlcWarning);
                string fallbackScene = !string.IsNullOrEmpty(_lastSyncedScene) && !IsDlcSceneOrLevel(_lastSyncedScene, 0)
                    ? _lastSyncedScene
                    : "scene_map_world_1";
                _ignoreClientSceneRequestUntilTime = Time.unscaledTime + 4.0f;
                _applyingRemoteSceneLoad = true;
                try
                {
                    SceneLoader.LoadScene(fallbackScene, SceneLoader.Transition.Iris, SceneLoader.Transition.Iris, SceneLoader.Icon.Hourglass, null);
                }
                catch { }
                finally
                {
                    _applyingRemoteSceneLoad = false;
                }
                return;
            }

            // Issue #9 (v1.3.2): Reset all local/remote weapon & action edge-tracking flags on scene load / Retry
            // so Player 2's airplane and ground basic shots always resume firing after restarting a level!
            _wasLocalShooting = false;
            _wasRemoteParrying = false;
            _wasRemotePlaneShrunk = false;
            _wasRemotePlaneShooting = false;
            _lastSentPlaneWeaponId = 0;

            _processedDeathEffects.Clear();
            _syncedCoinHashes.Clear();
            _parriedMausoleumGhostIds.Clear();
            _clientBoundMausoleumGhosts.Clear();
            _activeDialogueInstanceIds.Clear();
            _dialogueStartTimeByInstanceId.Clear();
            _startedSpawnerHashes.Clear();
            _endedSpawnerHashes.Clear();
            _clientEnemyLastHostSeenTime.Clear();
            _clientEnemySpawnGraceTime.Clear();
            _hostEnemyLastKnownHp.Clear();
            _hostEnemyLastKnownPos.Clear();
            _hostEnemyLastKnownType.Clear();
            _killedEnemyKeys.Clear();
            _clientEnemyLastReportedHp.Clear();
            _hookedMausoleumInstanceId = 0;
            _sentMausoleumLose = false;
            _lastSyncedDiceInstanceId = 0;
            _sentKingDiceBoardInit = false;
            _hostConfirmedLevelWin = false;
            _lastHostBossCurrentHealth = -1f;
            _lastHostBossStateIndex = 0;
            _lastSentBossPropertiesHp = -1f;
            _wasLocalPauseGuiOpen = false;
            _isPausedByPauseSync = false;
            _wasLocalEquipMenuOpen = false;
            _isRemoteEquipMenuOpen = false;
            _lastSentEquipSide = 255;
            _lastSentShopState = 255;
            _lastSentShopItemIdx = -1;
            _wasLocalMapStartUiOpen = false;
            _isRemoteMapStartUiOpen = false;
            _remoteMapStartUiKind = 0;
            _lastSentMapStartUiKind = 0;
            _remoteReadyForLevelId = -1;
            _lastRemoteMapFacingX = 0;
            _lastRemoteMapFacingY = -1;
            SetIgnoreRemoteDeathWindow(2.5f);

            int currentModeInt = Level.Current != null ? (int)Level.Current.mode : (int)Level.CurrentMode;
            CupheadCoopBootstrapper.Log("[CoopDiag] SceneLoaded: " + FormatSceneAndLevelContext(sceneName, (int)SceneLoader.CurrentLevel, currentModeInt));

            NormalizeAllMapPlayerPositions();
            ForceMultiplayerPlayerTwoJoined();
            MirrorAndSyncPlayerInventories(false);
            ConfigureLocalAndRemoteInputs();
            SendLocalLoadout(true);

            if (Map.Current != null)
            {
                _lastTrackedMapInstanceId = Map.Current.GetInstanceID();
                _mapSceneEnteredTime = Time.unscaledTime;
                _pendingMapObstacleRefresh = true;
                EnforceMapPlayersAndCameraTether(true);
                if (!BothPlayersHaveDlc)
                {
                    DisableDlcBoatmanOnMap();
                }
                else
                {
                    RefreshDlcBoatmanOnMapIfUnlocked();
                }
                if (_transport.Role == CoopRole.Host)
                {
                    SendWorldSaveMirror();
                }
            }

            if (_transport.Role == CoopRole.Host && !_applyingRemoteSceneLoad && !string.IsNullOrEmpty(sceneName))
            {
                if (sceneName != "scene_load_helper" && sceneName != "scene_cutscene_load")
                {
                    int levelId = (int)SceneLoader.CurrentLevel;
                    if (IsDlcSceneOrLevel(sceneName, levelId) && !BothPlayersHaveDlc)
                    {
                        _lastDlcWarning = "BLOQUEADO: Um dos jogadores nao tem a DLC! Fase da DLC nao enviada para evitar loading infinito.";
                        CupheadCoopBootstrapper.Log(_lastDlcWarning);
                        return;
                    }

                    _lastSyncedScene = sceneName;
                    _sceneLoadSequence++;
                    _currentRngSeed = (Math.Abs(Environment.TickCount ^ (sceneName.GetHashCode() + _sceneLoadSequence)) & 0x007FFFFF) | 1;
                    UnityEngine.Random.InitState(_currentRngSeed);

                    int diffMode = Level.Current != null ? (int)Level.Current.mode : (int)Level.CurrentMode;
                    byte[] pkt = CoopProtocol.BuildSceneSync(sceneName, levelId, diffMode, _currentRngSeed, _sceneLoadSequence);
                    _transport.Send(pkt, true);
                }
            }

            if (Level.Current != null && !IsMenuOrMapScene(sceneName))
            {
                SynchronizeBossPropertiesRng(_currentRngSeed);
                BeginLevelReadyGate((int)Level.Current.CurrentLevel);
            }
            else
            {
                ReleaseLevelReadyGate();
            }
        }

        private static bool IsMenuOrMapScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
                return true;
            return sceneName == "scene_start"
                || sceneName == "scene_title"
                || sceneName == "scene_slot_select"
                || sceneName == "scene_load_helper"
                || sceneName == "scene_cutscene_load"
                || sceneName == "scene_win"
                || sceneName == "scene_shop"
                || sceneName == "scene_shop_DLC"
                || sceneName.StartsWith("scene_map_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Runs every Unity Update() frame (even while paused by PauseManager!) so LevelReadyGate,
        /// PauseSync, EquipMenuSync, ShopStateSync, MapLevelSelectSync, DialogueSync, and Client SceneLoader monitoring respond immediately.
        /// </summary>
        public void UpdateSync()
        {
            EnsureOnlineWrapper();
            MonitorMapPostWinAndObstacles();

            // Issue #6 (v1.3.1): Immediately close lobby if either Host or Client begins exiting to the Title Screen
            // after having entered gameplay (never close on scene_slot_select!).
            if (_hasEnteredGameplayScene && (_transport.IsConnected || _transport.Role != CoopRole.None) && SceneLoader.Exists && SceneLoader.CurrentlyLoading)
            {
                string loadingScene = SceneLoader.SceneName ?? string.Empty;
                if (loadingScene == "scene_title")
                {
                    _hasEnteredGameplayScene = false;
                    CupheadCoopBootstrapper.Log("[CoopDiag] Loading Main Menu (" + loadingScene + "); automatically closing Co-op lobby.");
                    _transport.Disconnect();
                    return;
                }
            }

            if (!_transport.IsConnected)
                return;

            UpdateLevelReadyGate();
            MonitorPauseSync();
            MonitorEquipMenuSync();
            MonitorShopSceneSync();
            MonitorActiveDialoguesAndInteractions(false);
            MonitorMapLevelSelectSync();
            MonitorClientSceneRequests();
        }

        public void FixedUpdateSync()
        {
            EnsureOnlineWrapper();
            if (!_transport.IsConnected)
                return;

            _localTick++;
            ForceMultiplayerPlayerTwoJoined();
            ConfigureLocalAndRemoteInputs();
            EnsureLevelHooks();
            UpdateLevelReadyGate();
            MonitorPauseSync();
            MonitorEquipMenuSync();
            MonitorShopSceneSync();
            MonitorActiveDialoguesAndInteractions(_localTick % 15 == 0);
            MonitorMapLevelSelectSync();

            // Periodic checks every 15 ticks (~0.25s)
            if (_localTick % 15 == 0)
            {
                SendLocalLoadout(false);
                MirrorAndSyncPlayerInventories(false);
                MonitorLevelCoins();
                MonitorMapDjimmiWish();
            }

            // Per-tick gameplay monitors
            MonitorDeathEffectsAndRevives();
            MonitorKingDiceBoard();
            MonitorMausoleumLevel();
            MonitorEnemiesAndSpawners();
            MonitorBossHealthAndKnockoutGate();

            SendLocalPlayerState();
        }

        // =====================================================================
        // 1. LEVEL READY GATE (Symmetric Level-Start Barrier)
        // =====================================================================

        public void BeginLevelReadyGate(int levelId)
        {
            SetIgnoreRemoteDeathWindow(2.5f);
            if (!_transport.IsConnected || !_handshakeCompleted)
                return;

            _levelReadyGateLevelId = levelId;
            _levelReadyGateStartTime = Time.unscaledTime;
            _nextLevelReadyBroadcastTime = 0f;

            if (_remoteReadyForLevelId == levelId)
            {
                // Partner already finished loading this level; notify them and start immediately!
                byte[] readyPkt = CoopProtocol.BuildLevelEvent(
                    LevelEventType.LevelReady,
                    (byte)GetLocalPlayerId(),
                    _sceneLoadSequence,
                    _transport.Role == CoopRole.Host ? (float)_currentRngSeed : 0f,
                    levelId);
                _transport.Send(readyPkt, true);
                ReleaseLevelReadyGate();
                return;
            }

            _waitingAtLevelReadyGate = true;
            if (PauseManager.state != PauseManager.State.Paused)
            {
                PauseManager.Pause();
                _gatePausedGameplay = true;
            }
            CupheadCoopBootstrapper.Log("LevelReadyGate active for level " + (Levels)levelId + " (waiting for partner to finish loading).");
        }

        private void UpdateLevelReadyGate()
        {
            if (Level.Current != null && _transport.IsConnected)
            {
                int currentLvlId = (int)Level.Current.CurrentLevel;
                bool keepBroadcastingAfterRelease = _levelReadyGateStartTime > 0f && (Time.unscaledTime - _levelReadyGateStartTime) < 2.5f;
                if (Time.unscaledTime >= _nextLevelReadyBroadcastTime && (_waitingAtLevelReadyGate || keepBroadcastingAfterRelease || _nextLevelReadyBroadcastTime == 0f))
                {
                    _nextLevelReadyBroadcastTime = Time.unscaledTime + 0.35f;
                    byte[] readyPkt = CoopProtocol.BuildLevelEvent(
                        LevelEventType.LevelReady,
                        (byte)GetLocalPlayerId(),
                        _sceneLoadSequence,
                        _transport.Role == CoopRole.Host ? (float)_currentRngSeed : 0f,
                        currentLvlId);
                    _transport.Send(readyPkt, true);
                }
            }

            if (!_waitingAtLevelReadyGate)
                return;

            if (Level.Current == null)
            {
                ReleaseLevelReadyGate();
                return;
            }

            int activeLvlId = (int)Level.Current.CurrentLevel;
            bool partnerReady = (_remoteReadyForLevelId == activeLvlId);
            bool timedOut = (Time.unscaledTime - _levelReadyGateStartTime) >= 8.0f;

            if (partnerReady || timedOut)
            {
                CupheadCoopBootstrapper.Log("LevelReadyGate released for " + Level.Current.CurrentLevel + " (partnerReady=" + partnerReady + ", timedOut=" + timedOut + ").");
                ReleaseLevelReadyGate();
                return;
            }

            if (PauseManager.state != PauseManager.State.Paused)
            {
                PauseManager.Pause();
                _gatePausedGameplay = true;
            }
        }

        public void ReleaseLevelReadyGate()
        {
            bool wasWaiting = _waitingAtLevelReadyGate;
            _waitingAtLevelReadyGate = false;
            SetIgnoreRemoteDeathWindow(2.0f);
            if (Level.Current != null)
            {
                SynchronizeBossPropertiesRng(_currentRngSeed);
                if (wasWaiting && _transport.IsConnected)
                {
                    byte[] readyPkt = CoopProtocol.BuildLevelEvent(
                        LevelEventType.LevelReady,
                        (byte)GetLocalPlayerId(),
                        _sceneLoadSequence,
                        _transport.Role == CoopRole.Host ? (float)_currentRngSeed : 0f,
                        (int)Level.Current.CurrentLevel);
                    _transport.Send(readyPkt, true);
                }
            }
            if (_gatePausedGameplay)
            {
                _gatePausedGameplay = false;
                if (PauseManager.state == PauseManager.State.Paused && !_isPausedByPauseSync && !_wasLocalPauseGuiOpen)
                {
                    PauseManager.Unpause();
                }
            }
        }

        private static FieldInfo GetFieldInHierarchy(Type type, string fieldName)
        {
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo fi = t.GetField(fieldName, AllFlags | BindingFlags.DeclaredOnly);
                if (fi != null)
                    return fi;
            }
            return null;
        }

        /// <summary>
        /// Ensures Level.Current.properties (whose State constructors run in Level.Awake() before OnSceneLoaded)
        /// has identical attack patterns[] and patternIndex across both Host and Client, and resets PlayerManager.currentId.
        /// Also deterministically normalizes Mausoleum wave strings so ghost spawns match across Host and Client.
        /// </summary>
        public bool SynchronizeBossPropertiesRng(int seed)
        {
            _currentRngSeed = (Math.Abs(seed) & 0x007FFFFF) | 1;
            UnityEngine.Random.InitState(_currentRngSeed);

            try
            {
                FieldInfo curIdField = typeof(PlayerManager).GetField("currentId", AllFlags);
                if (curIdField != null)
                {
                    curIdField.SetValue(null, PlayerId.PlayerOne);
                }
            }
            catch { }

            if (Level.Current == null)
                return false;

            try
            {
                FieldInfo propsField = GetFieldInHierarchy(Level.Current.GetType(), "properties");
                if (propsField == null)
                    return false;

                object existingProps = propsField.GetValue(Level.Current);
                if (existingProps == null)
                    return false;

                Type propsType = existingProps.GetType();
                MethodInfo getModeMethod = propsType.GetMethod("GetMode", AllFlags, null, new Type[] { typeof(Level.Mode) }, null);
                if (getModeMethod == null)
                    return false;

                UnityEngine.Random.InitState(_currentRngSeed);
                object freshProps = getModeMethod.Invoke(null, new object[] { Level.Current.mode });
                if (freshProps == null)
                    return false;

                FieldInfo statesField = GetFieldInHierarchy(propsType, "states");
                if (statesField != null)
                {
                    Array existingStates = statesField.GetValue(existingProps) as Array;
                    Array freshStates = statesField.GetValue(freshProps) as Array;
                    if (existingStates != null && freshStates != null && existingStates.Length == freshStates.Length)
                    {
                        for (int i = 0; i < existingStates.Length; i++)
                        {
                            object exState = existingStates.GetValue(i);
                            object frState = freshStates.GetValue(i);
                            if (exState == null || frState == null)
                                continue;

                            Type stType = exState.GetType();
                            FieldInfo patField = GetFieldInHierarchy(stType, "patterns");
                            FieldInfo patIdxField = GetFieldInHierarchy(stType, "patternIndex");
                            if (patField != null)
                                patField.SetValue(exState, patField.GetValue(frState));
                            if (patIdxField != null)
                                patIdxField.SetValue(exState, patIdxField.GetValue(frState));

                            // Issue #5 (v1.3.0): Deterministic Mausoleum wave string normalization
                            FieldInfo mainField = stType.GetField("main", AllFlags);
                            if (mainField != null)
                            {
                                object exMain = mainField.GetValue(exState);
                                object frMain = mainField.GetValue(frState);
                                if (exMain != null && frMain != null)
                                {
                                    NormalizeMausoleumWaveMain(exMain, frMain, _currentRngSeed + (i * 997));
                                }
                            }
                        }
                    }
                }

                UnityEngine.Random.InitState(_currentRngSeed ^ 0x3C6EF35F);
                return true;
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("SynchronizeBossPropertiesRng warning: " + ex.Message);
                return false;
            }
        }

        private static void NormalizeMausoleumWaveMain(object targetMain, object sourceMain, int waveSeed)
        {
            if (targetMain == null || sourceMain == null)
                return;

            Type mainType = targetMain.GetType();
            FieldInfo delayField = mainType.GetField("delayString", AllFlags);
            FieldInfo spawnField = mainType.GetField("spawnString", AllFlags);
            FieldInfo ghostField = mainType.GetField("ghostTypeString", AllFlags);

            if (delayField != null)
            {
                string[] src = delayField.GetValue(sourceMain) as string[];
                if (src != null && src.Length > 0)
                    delayField.SetValue(targetMain, new string[] { RotateCommaSeparatedString(src[Math.Abs(waveSeed) % src.Length], waveSeed >> 3) });
            }
            if (spawnField != null)
            {
                string[] src = spawnField.GetValue(sourceMain) as string[];
                if (src != null && src.Length > 0)
                    spawnField.SetValue(targetMain, new string[] { RotateCommaSeparatedString(src[Math.Abs(waveSeed ^ 0x1357) % src.Length], waveSeed >> 5) });
            }
            if (ghostField != null)
            {
                string[] src = ghostField.GetValue(sourceMain) as string[];
                if (src != null && src.Length > 0)
                    ghostField.SetValue(targetMain, new string[] { RotateCommaSeparatedString(src[Math.Abs(waveSeed ^ 0x2468) % src.Length], waveSeed >> 7) });
            }
        }

        private static string RotateCommaSeparatedString(string raw, int offset)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;
            string[] parts = raw.Split(',');
            if (parts.Length <= 1)
                return raw;
            int shift = Math.Abs(offset) % parts.Length;
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(parts[(i + shift) % parts.Length]);
            }
            return sb.ToString();
        }

        // =====================================================================
        // 2. PAUSE SYNC (Shared Visual Pause Menu Across Both PCs on Level & Map)
        // =====================================================================

        private void MonitorPauseSync()
        {
            if (Level.Current == null && Map.Current == null)
                return;

            try
            {
                if (_hookedPauseGui == null)
                {
                    _hookedPauseGui = UnityEngine.Object.FindObjectOfType<LevelPauseGUI>();
                }

                if (_hookedPauseGui == null)
                    return;

                // Issue #2 (v1.3.0): Never treat State.Animating (0.15s fade in/out) as Unpaused,
                // which previously caused Player 2's client to cancel the remote pause immediately!
                if (_hookedPauseGui.state == AbstractPauseGUI.State.Animating)
                    return;

                bool isGuiPaused = (_hookedPauseGui.state == AbstractPauseGUI.State.Paused);
                if (isGuiPaused != _wasLocalPauseGuiOpen)
                {
                    _wasLocalPauseGuiOpen = isGuiPaused;
                    if (!isGuiPaused)
                    {
                        _isPausedByPauseSync = false;
                    }

                    if (!_applyingRemotePause && _transport.IsConnected)
                    {
                        byte[] pausePkt = CoopProtocol.BuildLevelEvent(
                            LevelEventType.PauseSync,
                            (byte)GetLocalPlayerId(),
                            0f,
                            0f,
                            isGuiPaused ? 1 : 0);
                        _transport.Send(pausePkt, true);
                    }
                }
            }
            catch { }
        }

        public void ApplyRemotePauseSync(bool remotePaused)
        {
            _applyingRemotePause = true;
            try
            {
                _isPausedByPauseSync = remotePaused;
                LevelPauseGUI pauseGui = _hookedPauseGui != null ? _hookedPauseGui : UnityEngine.Object.FindObjectOfType<LevelPauseGUI>();
                FieldInfo stateBf = typeof(AbstractPauseGUI).GetField("<state>k__BackingField", AllFlags);
                MethodInfo showImm = typeof(AbstractPauseGUI).GetMethod("ShowImmediate", AllFlags);
                MethodInfo hideImm = typeof(AbstractPauseGUI).GetMethod("HideImmediate", AllFlags);

                if (remotePaused)
                {
                    _wasLocalPauseGuiOpen = true;
                    if (pauseGui != null)
                    {
                        if (pauseGui.state != AbstractPauseGUI.State.Paused && pauseGui.state != AbstractPauseGUI.State.Animating)
                        {
                            // AbstractPauseGUI.Pause() aborts if PauseManager.state != Unpaused; reset before invoking Pause()
                            PauseManager.state = PauseManager.State.Unpaused;
                            MethodInfo pauseMethod = typeof(AbstractPauseGUI).GetMethod("Pause", AllFlags);
                            if (pauseMethod != null) pauseMethod.Invoke(pauseGui, null);
                        }

                        // Ensure the visual menu is visible and state is Paused/Animating even if coroutine didn't advance yet
                        if (pauseGui.state == AbstractPauseGUI.State.Unpaused)
                        {
                            if (stateBf != null) stateBf.SetValue(pauseGui, AbstractPauseGUI.State.Paused);
                            if (showImm != null) showImm.Invoke(pauseGui, null);
                        }
                        if (PauseManager.state != PauseManager.State.Paused)
                        {
                            PauseManager.Pause();
                        }
                    }
                    else if (PauseManager.state != PauseManager.State.Paused)
                    {
                        PauseManager.Pause();
                    }
                }
                else
                {
                    _wasLocalPauseGuiOpen = false;
                    if (pauseGui != null && (pauseGui.state == AbstractPauseGUI.State.Paused || pauseGui.state == AbstractPauseGUI.State.Animating))
                    {
                        // AbstractPauseGUI.Unpause() aborts if state != State.Paused; ensure state is Paused if still Animating
                        if (pauseGui.state == AbstractPauseGUI.State.Animating && stateBf != null)
                        {
                            stateBf.SetValue(pauseGui, AbstractPauseGUI.State.Paused);
                        }
                        MethodInfo unpauseMethod = typeof(AbstractPauseGUI).GetMethod("Unpause", AllFlags);
                        if (unpauseMethod != null) unpauseMethod.Invoke(pauseGui, null);

                        if (pauseGui.state == AbstractPauseGUI.State.Paused)
                        {
                            if (stateBf != null) stateBf.SetValue(pauseGui, AbstractPauseGUI.State.Unpaused);
                            if (hideImm != null) hideImm.Invoke(pauseGui, null);
                        }
                        if (PauseManager.state == PauseManager.State.Paused && !_waitingAtLevelReadyGate)
                        {
                            PauseManager.Unpause();
                        }
                    }
                    else if (PauseManager.state == PauseManager.State.Paused && !_waitingAtLevelReadyGate)
                    {
                        PauseManager.Unpause();
                    }
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ApplyRemotePauseSync warning: " + ex.Message);
            }
            finally
            {
                _applyingRemotePause = false;
            }
        }

        // =====================================================================
        // 3. SCENE REQUEST SYNC (Guest-Initiated Retry / ExitToMap / Level Entry)
        // =====================================================================

        private void MonitorClientSceneRequests()
        {
            if (_transport.Role != CoopRole.Client || !_transport.IsConnected)
                return;

            try
            {
                bool currentlyLoading = SceneLoader.Exists && SceneLoader.CurrentlyLoading;
                if (currentlyLoading && !_wasSceneLoaderLoading && !_applyingRemoteSceneLoad && Time.unscaledTime >= _ignoreClientSceneRequestUntilTime)
                {
                    _wasSceneLoaderLoading = true;
                    string targetScene = SceneLoader.SceneName ?? string.Empty;
                    int targetLevel = (int)SceneLoader.CurrentLevel;
                    int targetMode = (int)Level.CurrentMode;
                    string activeScene = SceneManager.GetActiveScene().name;

                    if (targetScene == "scene_title" && _hasEnteredGameplayScene)
                    {
                        // Guest chose "Exit to Title" -> leave session cleanly
                        _hasEnteredGameplayScene = false;
                        _transport.Disconnect();
                        return;
                    }

                    if (targetScene == "scene_slot_select" || targetScene == "scene_start")
                    {
                        return;
                    }

                    if (IsDlcSceneOrLevel(targetScene, targetLevel) && !BothPlayersHaveDlc)
                    {
                        _lastDlcWarning = "BLOQUEADO: O Host nao possui a DLC! Fase da DLC cancelada.";
                        CupheadCoopBootstrapper.Log(_lastDlcWarning);
                        if (!string.IsNullOrEmpty(activeScene) && !IsDlcSceneOrLevel(activeScene, 0))
                        {
                            _ignoreClientSceneRequestUntilTime = Time.unscaledTime + 3.5f;
                            _applyingRemoteSceneLoad = true;
                            try
                            {
                                SceneLoader.LoadScene(activeScene, SceneLoader.Transition.Iris, SceneLoader.Transition.Iris, SceneLoader.Icon.Hourglass, null);
                            }
                            catch { }
                            finally
                            {
                                _applyingRemoteSceneLoad = false;
                            }
                        }
                        return;
                    }

                    if (Map.Current != null)
                    {
                        SyncMapReturnPositionsFromActivePlayers();
                    }

                    SceneRequestKind kind = SceneRequestKind.LoadScene;
                    if (targetScene == activeScene && Level.Current != null)
                    {
                        kind = SceneRequestKind.ReloadLevel;
                        _clientInitiatedReload = true;
                        _clientInitiatedReloadTime = Time.unscaledTime;
                    }
                    else if (targetScene.StartsWith("scene_map_", StringComparison.OrdinalIgnoreCase) && Level.Current != null)
                    {
                        kind = SceneRequestKind.LoadLastMap;
                    }

                    _lastClientRequestedScene = targetScene;
                    _lastClientRequestTime = Time.unscaledTime;

                    byte[] reqPkt = CoopProtocol.BuildSceneRequest(kind, targetScene, targetLevel, targetMode);
                    _transport.Send(reqPkt, true);
                    CupheadCoopBootstrapper.Log("Client forwarded SceneRequest (" + kind + " -> " + targetScene + ") to Host.");
                }
                else if (!currentlyLoading)
                {
                    _wasSceneLoaderLoading = false;
                }
            }
            catch { }
        }

        private void HandleSceneRequest(BinaryReader br)
        {
            SceneRequestKind kind = (SceneRequestKind)br.ReadByte();
            string sceneName = br.ReadString();
            int levelId = br.ReadInt32();
            int diffMode = br.ReadInt32();

            if (_transport.Role != CoopRole.Host || !_transport.IsConnected)
                return;

            if (IsDlcSceneOrLevel(sceneName, levelId) && !BothPlayersHaveDlc)
            {
                _lastDlcWarning = "BLOQUEADO: Um dos jogadores nao tem a DLC! Pedido de fase da DLC ignorado.";
                CupheadCoopBootstrapper.Log(_lastDlcWarning);
                return;
            }

            if (SceneLoader.Exists && SceneLoader.CurrentlyLoading)
                return;

            try
            {
                if (Map.Current != null)
                {
                    SyncMapReturnPositionsFromActivePlayers();
                }

                CupheadCoopBootstrapper.Log("Host executing Client SceneRequest: " + kind + " (" + sceneName + ", level=" + (Levels)levelId + ")");
                switch (kind)
                {
                    case SceneRequestKind.ReloadLevel:
                        if (Level.Current != null)
                        {
                            if (Level.IsDicePalace || Level.IsDicePalaceMain)
                                DicePalaceMainLevelGameInfo.CleanUpRetry();
                            SceneLoader.ReloadLevel();
                        }
                        break;

                    case SceneRequestKind.LoadLastMap:
                        SceneLoader.LoadLastMap();
                        break;

                    case SceneRequestKind.LoadLevel:
                        Level.SetCurrentMode((Level.Mode)diffMode);
                        SceneLoader.LoadLevel((Levels)levelId, SceneLoader.Transition.Iris, SceneLoader.Icon.Hourglass, null);
                        break;

                    case SceneRequestKind.LoadScene:
                        if (!string.IsNullOrEmpty(sceneName))
                        {
                            Level.SetCurrentMode((Level.Mode)diffMode);
                            SceneLoader.SetCurrentLevel((Levels)levelId);
                            if (SceneManager.GetActiveScene().name == sceneName && Level.Current != null)
                            {
                                SceneLoader.ReloadLevel();
                            }
                            else
                            {
                                SceneLoader.LoadScene(
                                    sceneName,
                                    SceneLoader.Transition.Iris,
                                    SceneLoader.Transition.Iris,
                                    SceneLoader.Icon.Hourglass,
                                    null);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("HandleSceneRequest warning: " + ex.Message);
            }
        }

        // =====================================================================
        // 4. WORLD SAVE MIRROR + DIALOGUER IN RAM (Without Overwriting Disk Save)
        // =====================================================================

        public void SendWorldSaveMirror()
        {
            if (!_transport.IsConnected || _transport.Role != CoopRole.Host || !PlayerData.Initialized || PlayerData.Data == null)
                return;

            try
            {
                if (Map.Current != null)
                {
                    SyncMapReturnPositionsFromActivePlayers();
                }
                NormalizeAllMapPlayerPositions();

                string dialoguerXml = string.Empty;
                if (Dialoguer.ready)
                {
                    dialoguerXml = Dialoguer.GetGlobalVariablesState() ?? string.Empty;
                    FieldInfo diagField = typeof(PlayerData).GetField("dialoguerState", AllFlags);
                    if (diagField != null && !string.IsNullOrEmpty(dialoguerXml))
                    {
                        diagField.SetValue(PlayerData.Data, dialoguerXml);
                    }
                }

                string slotJson = JsonUtility.ToJson(PlayerData.Data) ?? string.Empty;
                if (string.IsNullOrEmpty(slotJson))
                    return;

                byte[] compressed;
                using (MemoryStream rawMs = new MemoryStream())
                {
                    using (GZipStream gz = new GZipStream(rawMs, CompressionMode.Compress, true))
                    using (BinaryWriter bw = new BinaryWriter(gz, Encoding.UTF8))
                    {
                        bw.Write(slotJson);
                        bw.Write(dialoguerXml);
                    }
                    compressed = rawMs.ToArray();
                }

                const int MaxChunkSize = 900;
                ushort totalChunks = (ushort)((compressed.Length + MaxChunkSize - 1) / MaxChunkSize);
                if (totalChunks == 0)
                    return;

                ushort mirrorId = _nextMirrorId++;
                if (_nextMirrorId == 0) _nextMirrorId = 1;
                byte slotIdx = (byte)Mathf.Clamp(PlayerData.CurrentSaveFileIndex, 0, 2);

                for (ushort i = 0; i < totalChunks; i++)
                {
                    int offset = i * MaxChunkSize;
                    int count = Math.Min(MaxChunkSize, compressed.Length - offset);
                    byte[] chunkPkt = CoopProtocol.BuildWorldSaveMirrorChunk(mirrorId, slotIdx, i, totalChunks, compressed, offset, count);
                    _transport.Send(chunkPkt, true);
                }

                CupheadCoopBootstrapper.Log("Sent WorldSaveMirror #" + mirrorId + " (" + compressed.Length + " bytes compressed in " + totalChunks + " chunk(s)).");
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("SendWorldSaveMirror warning: " + ex.Message);
            }
        }

        private void HandleWorldSaveMirrorChunk(BinaryReader br)
        {
            ushort mirrorId = br.ReadUInt16();
            byte slotIdx = br.ReadByte();
            ushort chunkIdx = br.ReadUInt16();
            ushort totalChunks = br.ReadUInt16();
            ushort count = br.ReadUInt16();
            byte[] chunkBytes = br.ReadBytes(count);

            if (totalChunks == 0 || totalChunks > 256 || chunkIdx >= totalChunks)
                return;

            if (_receivingMirrorId != mirrorId || _receivingMirrorTotalChunks != totalChunks || _receivingMirrorChunks == null)
            {
                _receivingMirrorId = mirrorId;
                _receivingMirrorSlot = slotIdx;
                _receivingMirrorTotalChunks = totalChunks;
                _receivingMirrorChunks = new byte[totalChunks][];
                _receivingMirrorReceivedCount = 0;
            }

            if (_receivingMirrorChunks[chunkIdx] == null)
            {
                _receivingMirrorChunks[chunkIdx] = chunkBytes;
                _receivingMirrorReceivedCount++;
            }

            if (_receivingMirrorReceivedCount < totalChunks)
                return;

            // Reassemble and apply!
            int totalBytes = 0;
            for (int i = 0; i < totalChunks; i++)
                totalBytes += _receivingMirrorChunks[i].Length;

            byte[] fullCompressed = new byte[totalBytes];
            int pos = 0;
            for (int i = 0; i < totalChunks; i++)
            {
                Buffer.BlockCopy(_receivingMirrorChunks[i], 0, fullCompressed, pos, _receivingMirrorChunks[i].Length);
                pos += _receivingMirrorChunks[i].Length;
            }
            _receivingMirrorChunks = null;

            ApplyWorldSaveMirrorBytes(_receivingMirrorSlot, fullCompressed);
        }

        public void ApplyWorldSaveMirrorBytes(byte slotIdx, byte[] compressedBytes)
        {
            try
            {
                if (!PlayerData.Initialized || PlayerData.Data == null)
                    return;

                string slotJson;
                string dialoguerXml;
                using (MemoryStream ms = new MemoryStream(compressedBytes))
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Decompress))
                using (BinaryReader br = new BinaryReader(gz, Encoding.UTF8))
                {
                    slotJson = br.ReadString();
                    dialoguerXml = br.ReadString();
                }

                BackupClientOriginalSavesOnce();

                // Preserve PlayerTwo's currently equipped loadout before overwriting PlayerData in RAM
                PlayerData.PlayerLoadouts.PlayerLoadout p2Loadout = PlayerData.Data.Loadouts.GetPlayerLoadout(PlayerId.PlayerTwo);
                Weapon savedP2Primary = p2Loadout != null ? p2Loadout.primaryWeapon : Weapon.level_weapon_peashot;
                Weapon savedP2Secondary = p2Loadout != null ? p2Loadout.secondaryWeapon : Weapon.None;
                Super savedP2Super = p2Loadout != null ? p2Loadout.super : Super.None;
                Charm savedP2Charm = p2Loadout != null ? p2Loadout.charm : Charm.None;

                int clampedSlot = Mathf.Clamp((int)slotIdx, 0, 2);
                FieldInfo idxField = typeof(PlayerData).GetField("_CurrentSaveFileIndex", AllFlags);
                if (idxField != null)
                    idxField.SetValue(null, clampedSlot);

                PlayerData targetSlot = PlayerData.GetDataForSlot(clampedSlot);
                if (targetSlot != null && !string.IsNullOrEmpty(slotJson))
                {
                    JsonUtility.FromJsonOverwrite(slotJson, targetSlot);
                }

                PlayerData.inGame = true;
                NormalizeAllMapPlayerPositions();

                // Restore PlayerTwo's equipped loadout
                PlayerData.PlayerLoadouts.PlayerLoadout newP2Loadout = PlayerData.Data.Loadouts.GetPlayerLoadout(PlayerId.PlayerTwo);
                if (newP2Loadout != null && savedP2Primary != Weapon.None && (int)savedP2Primary != 0)
                {
                    newP2Loadout.primaryWeapon = savedP2Primary;
                    newP2Loadout.secondaryWeapon = savedP2Secondary;
                    newP2Loadout.super = savedP2Super;
                    newP2Loadout.charm = savedP2Charm;
                    newP2Loadout.HasEquippedSecondaryRegularWeapon = (savedP2Secondary != Weapon.None && (int)savedP2Secondary != 0);
                }

                if (!string.IsNullOrEmpty(dialoguerXml) && Dialoguer.ready)
                {
                    Dialoguer.SetGlobalVariablesState(dialoguerXml);
                }

                MirrorAndSyncPlayerInventories(false);
                if (Map.Current != null)
                {
                    EnforceMapPlayersAndCameraTether(true);
                    if (!BothPlayersHaveDlc)
                    {
                        DisableDlcBoatmanOnMap();
                    }
                    else
                    {
                        RefreshDlcBoatmanOnMapIfUnlocked();
                    }
                    bool winTransitionActive = Level.Won
                        || Map.Current.CurrentState != Map.State.Ready
                        || (AbstractMapLevelDependentEntity.RegisteredEntities != null && AbstractMapLevelDependentEntity.RegisteredEntities.Count > 0);
                    if (winTransitionActive)
                    {
                        _pendingMapObstacleRefresh = true;
                    }
                    else
                    {
                        RefreshMapObstacles();
                    }
                }
                _worldSaveMirrorsAppliedCount++;
                CupheadCoopBootstrapper.Log("Applied Host WorldSaveMirror in RAM (slot=" + clampedSlot + ", Client disk save protected).");
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ApplyWorldSaveMirrorBytes warning: " + ex.Message);
            }
        }

        /// <summary>
        /// On saves progressed in singleplayer, CurrentMapData.playerOnePosition is updated at the latest boss while
        /// CurrentMapData.playerTwoPosition remains back at the island entrance (or (0,0)). When loaded in Multiplayer,
        /// CupheadMapCamera centers at (P1 + P2) / 2, placing both players off-screen outside CupheadMapCamera.edgeCollider!
        /// This method ensures playerTwoPosition is always right next to playerOnePosition across all map data objects.
        /// </summary>
        public static bool NormalizeAllMapPlayerPositions()
        {
            if (!PlayerData.Initialized || PlayerData.Data == null)
                return false;

            bool normalizedAny = false;
            try
            {
                Scenes[] mapScenes = new Scenes[]
                {
                    Scenes.scene_map_world_1,
                    Scenes.scene_map_world_2,
                    Scenes.scene_map_world_3,
                    Scenes.scene_map_world_4,
                    Scenes.scene_map_world_DLC
                };

                for (int i = 0; i < mapScenes.Length; i++)
                {
                    PlayerData.MapData md = PlayerData.Data.GetMapData(mapScenes[i]);
                    if (md == null || !md.sessionStarted)
                        continue;

                    Vector3 p1 = md.playerOnePosition;
                    Vector3 p2 = md.playerTwoPosition;
                    if (p1 != Vector3.zero && (p2 == Vector3.zero || Vector2.Distance(new Vector2(p1.x, p1.y), new Vector2(p2.x, p2.y)) > 3.0f))
                    {
                        md.playerTwoPosition = new Vector3(p1.x + 0.6f, p1.y, p1.z);
                        normalizedAny = true;
                    }
                }
            }
            catch { }
            return normalizedAny;
        }

        public static void SyncMapReturnPositionsFromActivePlayers()
        {
            if (Map.Current == null || Map.Current.players == null || !PlayerData.Initialized || PlayerData.Data == null)
                return;

            try
            {
                PlayerData.MapData md = PlayerData.Data.CurrentMapData;
                if (md == null)
                    return;

                MapPlayerController p0 = Map.Current.players.Length > 0 ? Map.Current.players[0] : null;
                MapPlayerController p1 = Map.Current.players.Length > 1 ? Map.Current.players[1] : null;
                MapPlayerController anchor = p0 != null ? p0 : p1;
                if (anchor != null)
                {
                    Vector3 pos = anchor.transform.position;
                    md.sessionStarted = true;
                    md.playerOnePosition = pos;
                    md.playerTwoPosition = new Vector3(pos.x + 0.6f, pos.y, pos.z);
                }
            }
            catch { }
        }

        public bool EnforceMapPlayersAndCameraTether(bool snapToSavedDataPosition)
        {
            if (Map.Current == null || Map.Current.players == null || Map.Current.players.Length < 2)
                return false;

            bool adjusted = false;
            try
            {
                MapPlayerController p0 = Map.Current.players[0];
                MapPlayerController p1 = Map.Current.players[1];
                if (p0 == null || p1 == null)
                    return false;

                // Ensure sprite renderers are visible when not in a mid-join portal jump
                for (int i = 0; i < 2; i++)
                {
                    MapPlayerController mp = Map.Current.players[i];
                    if (mp != null && mp.animationController != null && mp.animationController.spriteRenderer != null && !mp.animationController.spriteRenderer.enabled)
                    {
                        mp.animationController.spriteRenderer.enabled = true;
                    }
                }

                if (snapToSavedDataPosition && PlayerData.Initialized && PlayerData.Data != null && PlayerData.Data.CurrentMapData != null)
                {
                    PlayerData.MapData md = PlayerData.Data.CurrentMapData;
                    if (md.sessionStarted && md.playerOnePosition != Vector3.zero)
                    {
                        Vector3 savedP1 = md.playerOnePosition;
                        if (Vector2.Distance(new Vector2(p0.transform.position.x, p0.transform.position.y), new Vector2(savedP1.x, savedP1.y)) > 3.5f)
                        {
                            p0.transform.position = new Vector3(savedP1.x, savedP1.y, p0.transform.position.z);
                            adjusted = true;
                        }
                        if (Vector2.Distance(new Vector2(p1.transform.position.x, p1.transform.position.y), new Vector2(savedP1.x, savedP1.y)) > 3.5f)
                        {
                            p1.transform.position = new Vector3(savedP1.x + 0.6f, savedP1.y, p1.transform.position.z);
                            adjusted = true;
                        }
                    }
                }

                // Continuous tether: CupheadMapCamera centers at (P1 + P2) / 2 and puts an edgeCollider around the screen.
                // If P1 and P2 ever get > 9.5 units apart on the Map, both players end up outside the camera viewport!
                float dist = Vector2.Distance(
                    new Vector2(p0.transform.position.x, p0.transform.position.y),
                    new Vector2(p1.transform.position.x, p1.transform.position.y));
                if (dist > 9.5f)
                {
                    Vector3 anchor = p0.transform.position;
                    p1.transform.position = new Vector3(anchor.x + 0.6f, anchor.y, p1.transform.position.z);
                    adjusted = true;
                }

                if (adjusted && CupheadMapCamera.Current != null)
                {
                    Vector3 center = (p0.transform.position + p1.transform.position) * 0.5f;
                    Vector3 camPos = CupheadMapCamera.Current.transform.position;
                    CupheadMapCamera.Current.transform.position = new Vector3(center.x, center.y, camPos.z);
                    MethodInfo updateCols = typeof(CupheadMapCamera).GetMethod("UpdateColliders", AllFlags);
                    if (updateCols != null)
                    {
                        updateCols.Invoke(CupheadMapCamera.Current, null);
                    }
                }

                if ((snapToSavedDataPosition || adjusted) && (Time.unscaledTime - _lastTetherLogTime) > 1.5f)
                {
                    _lastTetherLogTime = Time.unscaledTime;
                    CupheadCoopBootstrapper.Log(string.Format(
                        "[CoopDiag] MapPosition/Tether: P1=({0:F1},{1:F1}), P2=({2:F1},{3:F1}), dist={4:F1}, adjusted={5}",
                        p0.transform.position.x, p0.transform.position.y,
                        p1.transform.position.x, p1.transform.position.y,
                        dist, adjusted));
                }
            }
            catch { }
            return adjusted;
        }

        public static int RefreshDlcBoatmanOnMapIfUnlocked()
        {
            int enabledCount = 0;
            try
            {
                if (!PlayerData.Initialized || PlayerData.Data == null)
                    return 0;

                bool boatUnlocked = PlayerData.Data.hasUnlockedBoatman
                    || PlayerData.Data.hasUnlockedFirstSuper
                    || (PlayerData.Data.GetLevelData(Levels.Mausoleum) != null && PlayerData.Data.GetLevelData(Levels.Mausoleum).completed);
                if (!boatUnlocked)
                    return 0;

                BoatmanEnabler[] boatmen = UnityEngine.Object.FindObjectsOfType<BoatmanEnabler>();
                if (boatmen == null || boatmen.Length == 0)
                    return 0;

                MethodInfo onAlreadyMet = typeof(AbstractMapLevelDependentEntity).GetMethod("OnConditionAlreadyMet", AllFlags);
                FieldInfo toEnableField = typeof(MapLevelDependentObstacle).GetField("ToEnable", AllFlags);
                FieldInfo toDisableField = typeof(MapLevelDependentObstacle).GetField("ToDisable", AllFlags);

                for (int i = 0; i < boatmen.Length; i++)
                {
                    BoatmanEnabler b = boatmen[i];
                    if (b == null) continue;
                    GameObject enGo = toEnableField != null ? toEnableField.GetValue(b) as GameObject : null;
                    GameObject disGo = toDisableField != null ? toDisableField.GetValue(b) as GameObject : null;
                    if (enGo != null && !enGo.activeSelf)
                    {
                        if (onAlreadyMet != null)
                        {
                            try { onAlreadyMet.Invoke(b, null); } catch { }
                        }
                        enGo.SetActive(true);
                        if (disGo != null && disGo.activeSelf) disGo.SetActive(false);
                        enabledCount++;
                    }
                }
            }
            catch { }
            return enabledCount;
        }

        public static int DisableDlcBoatmanOnMap()
        {
            int disabledCount = 0;
            try
            {
                BoatmanEnabler[] boatmen = UnityEngine.Object.FindObjectsOfType<BoatmanEnabler>();
                if (boatmen == null || boatmen.Length == 0)
                    return 0;

                MethodInfo onNotMet = typeof(AbstractMapLevelDependentEntity).GetMethod("OnConditionNotMet", AllFlags);
                FieldInfo toEnableField = typeof(MapLevelDependentObstacle).GetField("ToEnable", AllFlags);
                FieldInfo toDisableField = typeof(MapLevelDependentObstacle).GetField("ToDisable", AllFlags);

                for (int i = 0; i < boatmen.Length; i++)
                {
                    BoatmanEnabler b = boatmen[i];
                    if (b == null) continue;
                    b.StopAllCoroutines();
                    if (onNotMet != null)
                    {
                        try { onNotMet.Invoke(b, null); } catch { }
                    }
                    GameObject enGo = toEnableField != null ? toEnableField.GetValue(b) as GameObject : null;
                    GameObject disGo = toDisableField != null ? toDisableField.GetValue(b) as GameObject : null;
                    if (enGo != null && enGo.activeSelf) enGo.SetActive(false);
                    if (disGo != null && !disGo.activeSelf) disGo.SetActive(true);
                    disabledCount++;
                }
            }
            catch { }
            return disabledCount;
        }

        private void BackupClientOriginalSavesOnce()
        {
            if (_clientOriginalSavesBackedUp || !PlayerData.Initialized)
                return;

            try
            {
                _clientOriginalSlotIndex = Mathf.Clamp(PlayerData.CurrentSaveFileIndex, 0, 2);
                for (int i = 0; i < 3; i++)
                {
                    PlayerData slotData = PlayerData.GetDataForSlot(i);
                    _clientOriginalSavesJson[i] = slotData != null ? JsonUtility.ToJson(slotData) : string.Empty;
                }
                if (Dialoguer.ready)
                {
                    _clientOriginalDialoguerXml = Dialoguer.GetGlobalVariablesState() ?? string.Empty;
                }
                _clientOriginalSavesBackedUp = true;
                CoopOnlineInterface.SuppressClientDiskSave = true;
                CupheadCoopBootstrapper.Log("Backed up Client's original 3 save slots in memory; SuppressClientDiskSave=true.");
            }
            catch { }
        }

        private void RestoreClientOriginalSavesIfBackedUp()
        {
            if (!_clientOriginalSavesBackedUp)
                return;

            try
            {
                for (int i = 0; i < 3; i++)
                {
                    PlayerData slotData = PlayerData.GetDataForSlot(i);
                    if (slotData != null && !string.IsNullOrEmpty(_clientOriginalSavesJson[i]))
                    {
                        JsonUtility.FromJsonOverwrite(_clientOriginalSavesJson[i], slotData);
                    }
                }
                FieldInfo idxField = typeof(PlayerData).GetField("_CurrentSaveFileIndex", AllFlags);
                if (idxField != null)
                    idxField.SetValue(null, _clientOriginalSlotIndex);

                if (Dialoguer.ready && !string.IsNullOrEmpty(_clientOriginalDialoguerXml))
                {
                    Dialoguer.SetGlobalVariablesState(_clientOriginalDialoguerXml);
                }
            }
            catch { }
            finally
            {
                _clientOriginalSavesBackedUp = false;
                CoopOnlineInterface.SuppressClientDiskSave = false;
                CupheadCoopBootstrapper.Log("Restored Client's original save slots in memory; SuppressClientDiskSave=false.");
            }
        }

        public void MonitorMapPostWinAndObstacles()
        {
            if (Map.Current == null)
            {
                _lastTrackedMapInstanceId = 0;
                return;
            }

            try
            {
                int mapInstId = Map.Current.GetInstanceID();
                if (mapInstId != _lastTrackedMapInstanceId)
                {
                    _lastTrackedMapInstanceId = mapInstId;
                    _mapSceneEnteredTime = Time.unscaledTime;
                }

                float elapsedOnMap = Time.unscaledTime - _mapSceneEnteredTime;
                bool hasRegisteredEntities = AbstractMapLevelDependentEntity.RegisteredEntities != null
                    && AbstractMapLevelDependentEntity.RegisteredEntities.Count > 0;

                if (_pendingMapObstacleRefresh && !Level.Won && !hasRegisteredEntities && Map.Current.CurrentState == Map.State.Ready && elapsedOnMap > 0.25f)
                {
                    _pendingMapObstacleRefresh = false;
                    RefreshMapObstacles();
                }

                if (_transport.IsConnected)
                {
                    EnforceMapPlayersAndCameraTether(false);
                    if (!BothPlayersHaveDlc)
                    {
                        DisableDlcBoatmanOnMap();
                    }
                    else if (_localTick % 30 == 0)
                    {
                        RefreshDlcBoatmanOnMapIfUnlocked();
                    }
                }

                // Issue #12 (v1.3.2): When entering or exiting a non-won location on the World Map (or joining),
                // ensure neither player gets stuck spinning in the "Jump" animation state!
                if (!Level.Won && Map.Current.players != null && elapsedOnMap > 0.2f)
                {
                    for (int i = 0; i < Map.Current.players.Length; i++)
                    {
                        MapPlayerController mp = Map.Current.players[i];
                        if (mp == null || mp.animator == null)
                            continue;
                        AnimatorStateInfo st = mp.animator.GetCurrentAnimatorStateInfo(0);
                        if (st.IsName("Jump") || st.IsName("WinChalice_Loop"))
                        {
                            if (mp.animator.GetInteger("X") == 0 && mp.animator.GetInteger("Y") == 0)
                            {
                                mp.animator.SetInteger("Y", -1);
                            }
                            mp.animator.SetTrigger("OnJumpComplete");
                            if (mp.state == MapPlayerController.State.Stationary && Map.Current.CurrentState == Map.State.Ready)
                            {
                                mp.Enable();
                            }
                        }
                    }
                }

                // Safety monitor: ensure players can NEVER get stuck in the Overworld victory dancing animation
                // ("Jump" / "WinChalice_Loop" with MapPlayerController.State.Stationary) after returning from a level.
                if (Map.Current.players != null && elapsedOnMap > 1.8f)
                {
                    bool notificationShowing = MapEventNotification.Current != null && MapEventNotification.Current.showing;
                    if (!notificationShowing)
                    {
                        bool anyStuckInWinDance = false;
                        for (int i = 0; i < Map.Current.players.Length; i++)
                        {
                            MapPlayerController mp = Map.Current.players[i];
                            if (mp == null || mp.state != MapPlayerController.State.Stationary)
                                continue;

                            bool isWinDanceAnim = true;
                            if (mp.animator != null)
                            {
                                AnimatorStateInfo animState = mp.animator.GetCurrentAnimatorStateInfo(0);
                                isWinDanceAnim = animState.IsName("Jump") || animState.IsName("WinChalice_Loop");
                            }

                            if (isWinDanceAnim)
                            {
                                anyStuckInWinDance = true;
                                break;
                            }
                        }

                        bool mapReadyButPlayerStillDancing = anyStuckInWinDance && Map.Current.CurrentState == Map.State.Ready && !hasRegisteredEntities;
                        bool winSequenceTimedOut = anyStuckInWinDance && elapsedOnMap > 5.0f;

                        if (mapReadyButPlayerStillDancing || winSequenceTimedOut)
                        {
                            ReleaseStuckMapWinAnimation(Map.Current);
                        }
                    }
                }
            }
            catch { }
        }

        public static void ReleaseStuckMapWinAnimation(Map map)
        {
            if (map == null)
                return;

            try
            {
                if (AbstractMapLevelDependentEntity.RegisteredEntities != null && AbstractMapLevelDependentEntity.RegisteredEntities.Count > 0)
                {
                    PropertyInfo curStateProp = typeof(AbstractMapLevelDependentEntity).GetProperty("CurrentState", AllFlags);
                    MethodInfo onAlreadyMet = typeof(AbstractMapLevelDependentEntity).GetMethod("OnConditionAlreadyMet", AllFlags);
                    for (int i = 0; i < AbstractMapLevelDependentEntity.RegisteredEntities.Count; i++)
                    {
                        AbstractMapLevelDependentEntity ent = AbstractMapLevelDependentEntity.RegisteredEntities[i];
                        if (ent == null) continue;
                        if (onAlreadyMet != null)
                        {
                            try { onAlreadyMet.Invoke(ent, null); } catch { }
                        }
                        if (curStateProp != null)
                        {
                            try { curStateProp.SetValue(ent, AbstractMapLevelDependentEntity.State.Complete, null); } catch { }
                        }
                    }
                    AbstractMapLevelDependentEntity.RegisteredEntities.Clear();
                }

                map.CurrentState = Map.State.Ready;
                Level.ResetPreviousLevelInfo();
                InterruptingPrompt.SetCanInterrupt(true);

                if (map.players != null)
                {
                    for (int i = 0; i < map.players.Length; i++)
                    {
                        MapPlayerController mp = map.players[i];
                        if (mp == null)
                            continue;

                        try { mp.OnWinComplete(); } catch { }
                        try { mp.Enable(); } catch { }
                        if (mp.animator != null)
                        {
                            try
                            {
                                mp.animator.SetTrigger("OnJumpComplete");
                                mp.animator.Play("Idle", 0, 0f);
                            }
                            catch { }
                        }
                    }
                }

                RefreshMapObstacles();
                CupheadCoopBootstrapper.Log("Released players from Overworld victory animation and restored Map.State.Ready.");
            }
            catch { }
        }

        public static void RefreshMapObstacles(bool allowWithoutMapForTest = false)
        {
            if ((!allowWithoutMapForTest && Map.Current == null) || !PlayerData.Initialized || PlayerData.Data == null)
                return;

            if (!allowWithoutMapForTest)
            {
                // Never interfere with Map.start_cr() while a post-level victory celebration / flagpole raise is running!
                if (Level.Won || (AbstractMapLevelDependentEntity.RegisteredEntities != null && AbstractMapLevelDependentEntity.RegisteredEntities.Count > 0))
                    return;
            }

            try
            {
                MethodInfo validateSuccess = typeof(AbstractMapLevelDependentEntity).GetMethod("ValidateSucess", AllFlags);
                MethodInfo onAlreadyMet = typeof(AbstractMapLevelDependentEntity).GetMethod("OnConditionAlreadyMet", AllFlags);
                MethodInfo onNotMet = typeof(AbstractMapLevelDependentEntity).GetMethod("OnConditionNotMet", AllFlags);
                FieldInfo levelsField = typeof(AbstractMapLevelDependentEntity).GetField("_levels", AllFlags);
                FieldInfo toEnableField = typeof(MapLevelDependentObstacle).GetField("ToEnable", AllFlags);
                FieldInfo toDisableField = typeof(MapLevelDependentObstacle).GetField("ToDisable", AllFlags);

                MapShmupTutorialBridgeActivator[] shmupBridges = UnityEngine.Object.FindObjectsOfType<MapShmupTutorialBridgeActivator>();
                HashSet<int> shmupManagedIds = new HashSet<int>();
                if (shmupBridges != null)
                {
                    FieldInfo bpField = typeof(MapShmupTutorialBridgeActivator).GetField("blueprintObstacle", AllFlags);
                    for (int i = 0; i < shmupBridges.Length; i++)
                    {
                        if (shmupBridges[i] == null || !shmupBridges[i].enabled || !shmupBridges[i].gameObject.activeInHierarchy || bpField == null)
                            continue;
                        MapLevelDependentObstacle bp = bpField.GetValue(shmupBridges[i]) as MapLevelDependentObstacle;
                        if (bp != null)
                        {
                            shmupManagedIds.Add(bp.GetInstanceID());
                            if (PlayerData.Data.IsFlyingTutorialCompleted)
                            {
                                if (onAlreadyMet != null) onAlreadyMet.Invoke(bp, null);
                            }
                            else
                            {
                                if (onNotMet != null) onNotMet.Invoke(bp, null);
                            }
                        }
                    }
                }

                // IMPORTANT: Only inspect MapLevelDependentObstacle (never MapFlagpole, whose Animator state machine
                // breaks if OnConditionAlreadyMet/OnConditionNotMet is invoked outside MapFlagpole.Start()!).
                MapLevelDependentObstacle[] obstacles = UnityEngine.Object.FindObjectsOfType<MapLevelDependentObstacle>();
                if (obstacles == null || validateSuccess == null || onAlreadyMet == null || onNotMet == null)
                    return;

                for (int i = 0; i < obstacles.Length; i++)
                {
                    MapLevelDependentObstacle ent = obstacles[i];
                    if (ent == null || ent is BoatmanEnabler || shmupManagedIds.Contains(ent.GetInstanceID()))
                        continue;

                    // CRITICAL FIX FOR WORLD 1 INITIAL BRIDGE ("Bridge 1"):
                    // In scene_map_world_1, Studio MDHR left a MapLevelDependentObstacle component on "Bridge 1"
                    // with _levels = [Platforming_Level_1_1] and MonoBehaviour.enabled = false (compEn = False)
                    // so that "Bridge 1" is always active from the very start of the game, whereas "Bridge 2"
                    // has MonoBehaviour.enabled = true. FindObjectsOfType returns disabled MonoBehaviours on active
                    // GameObjects, so we MUST skip any obstacle with !ent.enabled!
                    if (!ent.enabled || !ent.gameObject.activeInHierarchy || string.Equals(ent.name, "Bridge 1", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.Equals(ent.name, "Bridge 1", StringComparison.OrdinalIgnoreCase))
                        {
                            GameObject enGo = toEnableField != null ? toEnableField.GetValue(ent) as GameObject : null;
                            GameObject disGo = toDisableField != null ? toDisableField.GetValue(ent) as GameObject : null;
                            if (enGo != null && !enGo.activeSelf) enGo.SetActive(true);
                            if (disGo != null && disGo.activeSelf) disGo.SetActive(false);
                        }
                        continue;
                    }

                    if (levelsField != null)
                    {
                        Array lvls = levelsField.GetValue(ent) as Array;
                        if (lvls == null || lvls.Length == 0)
                            continue;
                    }

                    bool met = (bool)validateSuccess.Invoke(ent, null);
                    if (met)
                    {
                        onAlreadyMet.Invoke(ent, null);
                    }
                    else
                    {
                        onNotMet.Invoke(ent, null);
                    }
                }
            }
            catch { }
        }

        // =====================================================================
        // 5. DLC & VERSION VERIFICATION (Prevent Infinite DLC Loading / Crashes)
        // =====================================================================

        public static bool IsDlcSceneOrLevel(string sceneName, int levelId)
        {
            if (!string.IsNullOrEmpty(sceneName))
            {
                if (sceneName.IndexOf("_dlc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sceneName.IndexOf("saltbaker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sceneName.IndexOf("chalice_tutorial", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sceneName.IndexOf("chess_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sceneName.IndexOf("graveyard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sceneName.IndexOf("kitchen", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            if (levelId != 0 && Enum.IsDefined(typeof(Levels), levelId))
            {
                Levels lvl = (Levels)levelId;
                if (lvl == Levels.Kitchen || lvl == Levels.ChaliceTutorial || lvl == Levels.Graveyard || lvl == Levels.Saltbaker)
                    return true;
                if (Level.worldDLCBossLevelsWithSaltbaker != null && Array.IndexOf(Level.worldDLCBossLevelsWithSaltbaker, lvl) >= 0)
                    return true;
                if (Level.kingOfGamesLevelsWithCastle != null && Array.IndexOf(Level.kingOfGamesLevelsWithCastle, lvl) >= 0)
                    return true;
            }

            return false;
        }

        public static bool IsDlcWeapon(int weaponId)
        {
            if (weaponId == 0 || PlayerData.WeaponsDLC == null)
                return false;
            return Array.IndexOf(PlayerData.WeaponsDLC, (Weapon)weaponId) >= 0;
        }

        public static bool IsDlcCharm(int charmId)
        {
            if (charmId == 0 || PlayerData.CharmsDLC == null)
                return false;
            return Array.IndexOf(PlayerData.CharmsDLC, (Charm)charmId) >= 0;
        }

        public bool SanitizeLoadoutForDlc(ref int primaryWep, ref int secondaryWep, ref int superId, ref int charmId)
        {
            if (BothPlayersHaveDlc)
                return false;

            bool sanitized = false;
            if (IsDlcWeapon(primaryWep))
            {
                primaryWep = (int)Weapon.level_weapon_peashot;
                sanitized = true;
            }
            if (IsDlcWeapon(secondaryWep))
            {
                secondaryWep = (int)Weapon.None;
                sanitized = true;
            }
            if (IsDlcCharm(charmId))
            {
                charmId = (int)Charm.None;
                sanitized = true;
            }

            if (sanitized)
            {
                _lastDlcWarning = "AVISO: Arma/Amuleto da DLC substituido pois um dos jogadores nao possui a DLC.";
                CupheadCoopBootstrapper.Log(_lastDlcWarning);
            }
            return sanitized;
        }

        // =====================================================================
        // 8. BOSS HEALTH & KNOCKOUT GATE (LevelOutcomeSync)
        // =====================================================================

        private bool EnsureBossProperties()
        {
            Level current = Level.Current;
            if (current == null)
            {
                _cachedBossPropsLevel = null;
                _cachedBossProps = null;
                return false;
            }

            if (_cachedBossPropsLevel == current && _cachedBossProps != null)
                return true;

            _cachedBossPropsLevel = current;
            _cachedBossProps = null;

            try
            {
                FieldInfo propsField = GetFieldInHierarchy(current.GetType(), "properties");
                if (propsField == null)
                    return false;

                object propsObj = propsField.GetValue(current);
                if (propsObj == null)
                    return false;

                Type propsType = propsObj.GetType();
                _bossCurrentHealthProp = propsType.GetProperty("CurrentHealth", AllFlags);
                _bossCurrentHealthBackingField = GetFieldInHierarchy(propsType, "<CurrentHealth>k__BackingField");
                _bossTotalHealthField = GetFieldInHierarchy(propsType, "TotalHealth");
                _bossDealDamageMethod = propsType.GetMethod("DealDamage", AllFlags, null, new Type[] { typeof(float) }, null);
                _bossDealDamageToNextNamedStateMethod = propsType.GetMethod("DealDamageToNextNamedState", AllFlags);
                _bossGetNextStateHealthTriggerMethod = propsType.GetMethod("GetNextStateHealthTrigger", AllFlags);
                _bossWinInstantlyMethod = propsType.GetMethod("WinInstantly", AllFlags);
                _bossStateIndexField = GetFieldInHierarchy(propsType, "stateIndex");

                if (_bossCurrentHealthProp != null && _bossDealDamageMethod != null)
                {
                    _cachedBossProps = propsObj;
                    return true;
                }
            }
            catch { }

            return false;
        }

        private float GetBossPropertiesHealth()
        {
            if (!EnsureBossProperties())
                return -1f;
            try
            {
                return (float)_bossCurrentHealthProp.GetValue(_cachedBossProps, null);
            }
            catch
            {
                return -1f;
            }
        }

        public int GetBossPropertiesStateIndex()
        {
            if (!EnsureBossProperties() || _bossStateIndexField == null)
                return 0;
            try
            {
                return (int)_bossStateIndexField.GetValue(_cachedBossProps);
            }
            catch
            {
                return 0;
            }
        }

        private float GetBossNextPhaseTriggerHealth()
        {
            if (!EnsureBossProperties() || _bossGetNextStateHealthTriggerMethod == null || _bossTotalHealthField == null)
                return 0f;
            try
            {
                float triggerFraction = (float)_bossGetNextStateHealthTriggerMethod.Invoke(_cachedBossProps, null);
                float totalHp = (float)_bossTotalHealthField.GetValue(_cachedBossProps);
                if (triggerFraction <= 0f || totalHp <= 0f)
                    return 0f;
                return triggerFraction * totalHp;
            }
            catch
            {
                return 0f;
            }
        }

        public bool SyncClientBossPhaseIndex(int hostStateIdx, float hostHp)
        {
            if (hostStateIdx > _lastHostBossStateIndex)
            {
                _lastHostBossStateIndex = hostStateIdx;
            }

            if (!EnsureBossProperties() || _bossDealDamageToNextNamedStateMethod == null)
                return false;

            bool advanced = false;
            try
            {
                int localStateIdx = GetBossPropertiesStateIndex();
                int guard = 0;
                while (localStateIdx < hostStateIdx && guard++ < 8)
                {
                    _bossDealDamageToNextNamedStateMethod.Invoke(_cachedBossProps, null);
                    int nextIdx = GetBossPropertiesStateIndex();
                    if (nextIdx <= localStateIdx)
                        break;
                    localStateIdx = nextIdx;
                    advanced = true;
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("SyncClientBossPhaseIndex warning: " + ex.Message);
            }
            return advanced;
        }

        private void MonitorBossHealthAndKnockoutGate()
        {
            if (Level.Current == null)
                return;

            if (_transport.Role == CoopRole.Host)
            {
                float currentDmg = Level.Current.timeline != null ? Level.Current.timeline.damage : 0f;
                float bossHp = GetBossPropertiesHealth();
                int stateIdx = GetBossPropertiesStateIndex();

                bool dmgChanged = Mathf.Abs(currentDmg - _lastSentBossDamage) >= 0.5f;
                bool hpChanged = bossHp >= 0f && Mathf.Abs(bossHp - _lastSentBossPropertiesHp) >= 0.5f;

                if (dmgChanged || hpChanged || (_localTick % 30 == 0 && (currentDmg > 0f || bossHp > 0f)))
                {
                    _lastSentBossDamage = currentDmg;
                    _lastSentBossPropertiesHp = bossHp;
                    byte[] dmgPkt = CoopProtocol.BuildLevelEvent(
                        LevelEventType.BossDamage,
                        (byte)PlayerId.PlayerOne,
                        currentDmg,
                        bossHp,
                        stateIdx);
                    _transport.Send(dmgPkt, hpChanged && bossHp <= 0f);
                }
            }
            else if (_transport.Role == CoopRole.Client && !_hostConfirmedLevelWin && !Level.Current.Ending && !Level.Won)
            {
                // Client Knockout & Phase Gate (Issue #4): prevent unconfirmed local bullets from killing the boss
                // OR prematurely crossing a phase threshold before the Host confirms the phase transition!
                if (EnsureBossProperties() && _bossCurrentHealthBackingField != null)
                {
                    float localHp = GetBossPropertiesHealth();
                    int localStateIdx = GetBossPropertiesStateIndex();
                    float minAllowedHp = 1f;
                    if (localStateIdx >= _lastHostBossStateIndex)
                    {
                        float nextTriggerHp = GetBossNextPhaseTriggerHealth();
                        if (nextTriggerHp > 1f)
                        {
                            minAllowedHp = Mathf.Max(minAllowedHp, nextTriggerHp + 0.5f);
                        }
                    }

                    if (localHp >= 0f && localHp < minAllowedHp)
                    {
                        float safeHp = _lastHostBossCurrentHealth > minAllowedHp ? _lastHostBossCurrentHealth : minAllowedHp;
                        _bossCurrentHealthBackingField.SetValue(_cachedBossProps, safeHp);
                    }
                }
            }
        }

        public void ForceClientLevelWinKnockout()
        {
            _hostConfirmedLevelWin = true;
            if (Level.Current == null || Level.Current.Ending || Level.Won)
                return;

            try
            {
                // Issue #5 (v1.3.0 / v1.3.2): If inside MausoleumLevel, stop ghost wave spawning, clean up ghosts,
                // and ensure MausoleumLevel.OnPreWin() -> legendarychaliceappear_cr() triggers Ms. Chalice dialogue!
                MausoleumLevel mausoleum = Level.Current as MausoleumLevel;
                if (mausoleum != null)
                {
                    FieldInfo spawnCounterField = typeof(MausoleumLevel).GetField("SPAWNCOUNTER", AllFlags);
                    if (spawnCounterField != null)
                    {
                        spawnCounterField.SetValue(null, 9999);
                    }

                    MausoleumLevelGhostBase[] ghosts = UnityEngine.Object.FindObjectsOfType<MausoleumLevelGhostBase>();
                    if (ghosts != null)
                    {
                        for (int i = 0; i < ghosts.Length; i++)
                        {
                            if (ghosts[i] != null && !ghosts[i].isDead)
                            {
                                try { ghosts[i].OnBossDeath(); } catch { }
                            }
                        }
                    }

                    FieldInfo isOverField = typeof(MausoleumLevel).GetField("isLevelOver", AllFlags);
                    bool alreadyOver = isOverField != null && (bool)isOverField.GetValue(mausoleum);
                    if (!alreadyOver)
                    {
                        MethodInfo onPreWin = typeof(MausoleumLevel).GetMethod("OnPreWin", AllFlags);
                        if (onPreWin != null)
                        {
                            onPreWin.Invoke(mausoleum, null);
                        }
                    }
                }

                if (EnsureBossProperties() && _bossWinInstantlyMethod != null)
                {
                    _bossWinInstantlyMethod.Invoke(_cachedBossProps, null);
                }
                if (!Level.Current.Ending && !Level.Won)
                {
                    MethodInfo zHackWin = typeof(Level).GetMethod("zHack_OnWin", AllFlags);
                    if (zHackWin != null) zHackWin.Invoke(Level.Current, null);
                }
                CupheadCoopBootstrapper.Log("[CoopDiag] LevelOutcome: Triggered synchronized KNOCKOUT (LevelWin) on Client for " + Level.Current.CurrentLevel);
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ForceClientLevelWinKnockout warning: " + ex.Message);
            }
        }

        // =====================================================================
        // 9. NPC DIALOGUE AUTO-POSITIONING & INTERACTION SYNC (Issue #1 & #5)
        // =====================================================================

        /// <summary>
        /// In Cuphead (DialogueInteractionPoint.CutScene_cr and OnDrawGizmosSelected),
        /// playerOneDialoguePosition and playerTwoDialoguePosition are WORLD coordinates, NOT local offsets!
        /// Adding dip.transform.position.x shifted the target X off-position and stalled CutScene_cr.
        /// </summary>
        public static float ResolveDialogueWorldX(DialogueInteractionPoint dip, Vector2 dlgPos)
        {
            if (Mathf.Abs(dlgPos.x) > 0.001f || dip == null)
                return dlgPos.x;
            return dip.transform.position.x;
        }

        /// <summary>
        /// Snaps both the remote puppet and (after a short grace window) the local player to their exact
        /// world dialogue X coordinates (playerOneDialoguePosition.x / playerTwoDialoguePosition.x) with Grounded=true
        /// so DialogueInteractionPoint.move_cr finishes without stalling, and synchronizes dialogue start/end across PCs.
        /// </summary>
        public bool MonitorActiveDialoguesAndInteractions(bool scanAllEntities)
        {
            bool snappedRemote = false;
            try
            {
                DialogueInteractionPoint[] points = UnityEngine.Object.FindObjectsOfType<DialogueInteractionPoint>();
                if (points != null && points.Length > 0)
                {
                    FieldInfo convActiveField = typeof(DialogueInteractionPoint).GetField("conversationIsActive", AllFlags);
                    FieldInfo p1PosField = typeof(DialogueInteractionPoint).GetField("playerOneDialoguePosition", AllFlags);
                    FieldInfo p2PosField = typeof(DialogueInteractionPoint).GetField("playerTwoDialoguePosition", AllFlags);
                    FieldInfo groundedBf = typeof(LevelPlayerMotor).GetField("<Grounded>k__BackingField", AllFlags);
                    FieldInfo lockedBf = typeof(LevelPlayerMotor).GetField("<Locked>k__BackingField", AllFlags);

                    PlayerId localId = GetLocalPlayerId();
                    PlayerId remoteId = GetRemotePlayerId();
                    AbstractPlayerController localCtrl = PlayerManager.GetPlayer(localId);
                    AbstractPlayerController remoteCtrl = PlayerManager.GetPlayer(remoteId);

                    for (int i = 0; i < points.Length; i++)
                    {
                        DialogueInteractionPoint dip = points[i];
                        if (dip == null)
                            continue;

                        if (dip.interactor != AbstractLevelInteractiveEntity.Interactor.Either)
                        {
                            dip.interactor = AbstractLevelInteractiveEntity.Interactor.Either;
                        }

                        int dipId = dip.GetInstanceID();
                        bool isConvActive = convActiveField != null && (bool)convActiveField.GetValue(dip);
                        Vector2 p1Vec = p1PosField != null ? (Vector2)p1PosField.GetValue(dip) : Vector2.zero;
                        Vector2 p2Vec = p2PosField != null ? (Vector2)p2PosField.GetValue(dip) : Vector2.zero;
                        float p1TargetX = ResolveDialogueWorldX(dip, p1Vec);
                        float p2TargetX = ResolveDialogueWorldX(dip, p2Vec);

                        if (isConvActive)
                        {
                            if (_activeDialogueInstanceIds.Add(dipId))
                            {
                                _dialogueStartTimeByInstanceId[dipId] = Time.unscaledTime;
                                CupheadCoopBootstrapper.Log(string.Format(
                                    "[CoopDiag] Dialogue started: {0} (id={1}, P1TargetX={2:F1}, P2TargetX={3:F1})",
                                    dip.name, (int)dip.dialogueInteraction, p1TargetX, p2TargetX));

                                if (!_applyingRemoteDialogue && _transport.IsConnected)
                                {
                                    byte[] dlgStartPkt = CoopProtocol.BuildDialogueSync(
                                        1,
                                        (int)dip.dialogueInteraction,
                                        dip.transform.position.x,
                                        p1TargetX,
                                        p2TargetX);
                                    _transport.Send(dlgStartPkt, true);
                                }
                            }

                            if (remoteCtrl != null)
                            {
                                float remoteTargetX = (remoteId == PlayerId.PlayerTwo) ? p2TargetX : p1TargetX;
                                Vector3 curPos = remoteCtrl.transform.position;
                                if (Mathf.Abs(curPos.x - remoteTargetX) > 0.01f)
                                {
                                    remoteCtrl.transform.position = new Vector3(remoteTargetX, curPos.y, curPos.z);
                                }

                                LevelPlayerController remoteLvl = remoteCtrl as LevelPlayerController;
                                if (remoteLvl != null && remoteLvl.motor != null)
                                {
                                    if (groundedBf != null) groundedBf.SetValue(remoteLvl.motor, true);
                                    if (lockedBf != null) lockedBf.SetValue(remoteLvl.motor, false);
                                }
                                snappedRemote = true;
                            }

                            // Also ensure the local player reaches their dialogue target X within 0.2s (or immediately in Mausoleum)
                            // so LevelPlayerMotor.MoveToX_cr never hangs if blocked by stage geometry or camera bounds!
                            float startedAt;
                            bool shouldSnapLocal = (dip is MausoleumDialogueInteraction)
                                || (_dialogueStartTimeByInstanceId.TryGetValue(dipId, out startedAt) && (Time.unscaledTime - startedAt) >= 0.2f);
                            if (shouldSnapLocal && localCtrl != null)
                            {
                                float localTargetX = (localId == PlayerId.PlayerTwo) ? p2TargetX : p1TargetX;
                                Vector3 localPos = localCtrl.transform.position;
                                if (Mathf.Abs(localPos.x - localTargetX) > 2.0f)
                                {
                                    localCtrl.transform.position = new Vector3(localTargetX, localPos.y, localPos.z);
                                }
                                LevelPlayerController localLvl = localCtrl as LevelPlayerController;
                                if (localLvl != null && localLvl.motor != null && groundedBf != null)
                                {
                                    groundedBf.SetValue(localLvl.motor, true);
                                }
                            }
                        }
                        else if (_activeDialogueInstanceIds.Contains(dipId))
                        {
                            _activeDialogueInstanceIds.Remove(dipId);
                            _dialogueStartTimeByInstanceId.Remove(dipId);
                            CupheadCoopBootstrapper.Log("[CoopDiag] Dialogue ended: " + dip.name + " (id=" + (int)dip.dialogueInteraction + ")");

                            if (!_applyingRemoteDialogue && _transport.IsConnected)
                            {
                                byte[] dlgEndPkt = CoopProtocol.BuildDialogueSync(
                                    2,
                                    (int)dip.dialogueInteraction,
                                    dip.transform.position.x,
                                    0f,
                                    0f);
                                _transport.Send(dlgEndPkt, true);
                            }
                        }
                    }
                }

                if (scanAllEntities)
                {
                    if (Level.Current != null)
                    {
                        AbstractLevelInteractiveEntity[] lvlEntities = UnityEngine.Object.FindObjectsOfType<AbstractLevelInteractiveEntity>();
                        if (lvlEntities != null)
                        {
                            for (int i = 0; i < lvlEntities.Length; i++)
                            {
                                if (lvlEntities[i] != null && lvlEntities[i].interactor != AbstractLevelInteractiveEntity.Interactor.Either)
                                {
                                    lvlEntities[i].interactor = AbstractLevelInteractiveEntity.Interactor.Either;
                                }
                            }
                        }
                    }

                    if (Map.Current != null)
                    {
                        AbstractMapInteractiveEntity[] mapEntities = UnityEngine.Object.FindObjectsOfType<AbstractMapInteractiveEntity>();
                        if (mapEntities != null)
                        {
                            for (int i = 0; i < mapEntities.Length; i++)
                            {
                                if (mapEntities[i] != null && mapEntities[i].interactor != AbstractMapInteractiveEntity.Interactor.Either)
                                {
                                    mapEntities[i].interactor = AbstractMapInteractiveEntity.Interactor.Either;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return snappedRemote;
        }

        private bool TryGetActiveDialogueTargetX(PlayerId playerId, out float targetX)
        {
            targetX = 0f;
            if (Level.Current == null)
                return false;

            try
            {
                DialogueInteractionPoint[] points = UnityEngine.Object.FindObjectsOfType<DialogueInteractionPoint>();
                if (points == null || points.Length == 0)
                    return false;

                FieldInfo convActiveField = typeof(DialogueInteractionPoint).GetField("conversationIsActive", AllFlags);
                FieldInfo p1PosField = typeof(DialogueInteractionPoint).GetField("playerOneDialoguePosition", AllFlags);
                FieldInfo p2PosField = typeof(DialogueInteractionPoint).GetField("playerTwoDialoguePosition", AllFlags);
                if (convActiveField == null)
                    return false;

                for (int i = 0; i < points.Length; i++)
                {
                    DialogueInteractionPoint dip = points[i];
                    if (dip == null || !(bool)convActiveField.GetValue(dip))
                        continue;

                    Vector2 dlgPos = (playerId == PlayerId.PlayerTwo)
                        ? (p2PosField != null ? (Vector2)p2PosField.GetValue(dip) : Vector2.zero)
                        : (p1PosField != null ? (Vector2)p1PosField.GetValue(dip) : Vector2.zero);
                    targetX = ResolveDialogueWorldX(dip, dlgPos);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void HandleDialogueSync(BinaryReader br)
        {
            byte action = br.ReadByte();
            int dialogueId = br.ReadInt32();
            float pointX = br.ReadSingle();
            float p1TargetX = br.ReadSingle();
            float p2TargetX = br.ReadSingle();
            ApplyRemoteDialogueSync(action, dialogueId, pointX, p1TargetX, p2TargetX);
        }

        public void ApplyRemoteDialogueSync(byte action, int dialogueId, float pointX, float p1TargetX, float p2TargetX)
        {
            _applyingRemoteDialogue = true;
            try
            {
                DialogueInteractionPoint[] points = UnityEngine.Object.FindObjectsOfType<DialogueInteractionPoint>();
                DialogueInteractionPoint best = null;
                float bestDist = float.MaxValue;

                if (points != null)
                {
                    for (int i = 0; i < points.Length; i++)
                    {
                        DialogueInteractionPoint p = points[i];
                        if (p == null) continue;
                        if ((int)p.dialogueInteraction == dialogueId)
                        {
                            best = p;
                            break;
                        }
                        float d = Mathf.Abs(p.transform.position.x - pointX);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = p;
                        }
                    }
                }

                FieldInfo convActiveField = typeof(DialogueInteractionPoint).GetField("conversationIsActive", AllFlags);
                FieldInfo groundedBf = typeof(LevelPlayerMotor).GetField("<Grounded>k__BackingField", AllFlags);
                FieldInfo lockedBf = typeof(LevelPlayerMotor).GetField("<Locked>k__BackingField", AllFlags);

                if (action == 1)
                {
                    AbstractPlayerController p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne);
                    AbstractPlayerController p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
                    if (p1 != null)
                    {
                        p1.transform.position = new Vector3(p1TargetX, p1.transform.position.y, p1.transform.position.z);
                        LevelPlayerController l1 = p1 as LevelPlayerController;
                        if (l1 != null && l1.motor != null)
                        {
                            if (groundedBf != null) groundedBf.SetValue(l1.motor, true);
                            if (lockedBf != null) lockedBf.SetValue(l1.motor, false);
                        }
                    }
                    if (p2 != null)
                    {
                        p2.transform.position = new Vector3(p2TargetX, p2.transform.position.y, p2.transform.position.z);
                        LevelPlayerController l2 = p2 as LevelPlayerController;
                        if (l2 != null && l2.motor != null)
                        {
                            if (groundedBf != null) groundedBf.SetValue(l2.motor, true);
                            if (lockedBf != null) lockedBf.SetValue(l2.motor, false);
                        }
                    }

                    if (best != null)
                    {
                        int dipId = best.GetInstanceID();
                        _activeDialogueInstanceIds.Add(dipId);
                        _dialogueStartTimeByInstanceId[dipId] = Time.unscaledTime;
                        bool isConvActive = convActiveField != null && (bool)convActiveField.GetValue(best);
                        if (!isConvActive)
                        {
                            MausoleumDialogueInteraction mdi = best as MausoleumDialogueInteraction;
                            if (mdi != null)
                            {
                                mdi.BeginDialogue();
                            }
                            else
                            {
                                MethodInfo actMethod = typeof(DialogueInteractionPoint).GetMethod("Activate", AllFlags);
                                if (actMethod != null) actMethod.Invoke(best, null);
                            }
                        }
                    }
                    CupheadCoopBootstrapper.Log("[CoopDiag] Applied remote DialogueStart (id=" + dialogueId + ", P1X=" + p1TargetX + ", P2X=" + p2TargetX + ")");
                }
                else if (action == 2)
                {
                    if (best != null)
                    {
                        int dipId = best.GetInstanceID();
                        _activeDialogueInstanceIds.Remove(dipId);
                        _dialogueStartTimeByInstanceId.Remove(dipId);
                        bool isConvActive = convActiveField != null && (bool)convActiveField.GetValue(best);
                        if (isConvActive)
                        {
                            try { Dialoguer.EndDialogue(); } catch { }
                            MethodInfo endHandler = typeof(DialogueInteractionPoint).GetMethod("OnDialogueEndedHandler", AllFlags);
                            if (endHandler != null)
                            {
                                try { endHandler.Invoke(best, null); } catch { }
                            }
                        }
                    }
                    CupheadCoopBootstrapper.Log("[CoopDiag] Applied remote DialogueEnd (id=" + dialogueId + ")");
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ApplyRemoteDialogueSync warning: " + ex.Message);
            }
            finally
            {
                _applyingRemoteDialogue = false;
            }
        }

        // =====================================================================
        // 10. MAUSOLEUM GHOST, PARRY & AUTHORITATIVE URN SYNC (Issue #5)
        // =====================================================================

        private static byte GetMausoleumGhostTypeByte(MausoleumLevelGhostBase g)
        {
            if (g is MausoleumLevelBigGhost) return 1;
            if (g is MausoleumLevelCircleGhost) return 2;
            if (g is MausoleumLevelDelayGhost) return 3;
            if (g is MausoleumLevelSineGhost) return 4;
            return 0; // MausoleumLevelRegularGhost
        }

        public void MonitorMausoleumLevel()
        {
            MausoleumLevel mausoleum = Level.Current as MausoleumLevel;
            if (mausoleum == null)
                return;

            try
            {
                int instId = mausoleum.GetInstanceID();
                if (_hookedMausoleumInstanceId != instId)
                {
                    _hookedMausoleumInstanceId = instId;
                    _sentMausoleumLose = false;
                    _parriedMausoleumGhostIds.Clear();
                    _clientBoundMausoleumGhosts.Clear();

                    FieldInfo loseGameField = typeof(MausoleumLevel).GetField("LoseGame", AllFlags);
                    if (loseGameField != null)
                    {
                        if (_transport.Role == CoopRole.Client)
                        {
                            // Prevent unparried ghosts on Client from unilaterally triggering a fake Game Over!
                            // Only the Host's urn triggers LevelEventType.LevelLose.
                            loseGameField.SetValue(mausoleum, new Action(OnClientIgnoredMausoleumLose));
                        }
                        else if (_transport.Role == CoopRole.Host)
                        {
                            Action existing = loseGameField.GetValue(mausoleum) as Action;
                            loseGameField.SetValue(mausoleum, (Action)Delegate.Combine(existing, new Action(OnHostMausoleumFailure)));
                        }
                    }
                }

                // Keep Client's LoseGame delegate suppressed in case anything resets it
                if (_transport.Role == CoopRole.Client && !_hostConfirmedLevelWin)
                {
                    FieldInfo loseGameField = typeof(MausoleumLevel).GetField("LoseGame", AllFlags);
                    if (loseGameField != null)
                    {
                        Action cur = loseGameField.GetValue(mausoleum) as Action;
                        if (cur == null || cur.Method.Name != "OnClientIgnoredMausoleumLose")
                        {
                            loseGameField.SetValue(mausoleum, new Action(OnClientIgnoredMausoleumLose));
                        }
                    }
                }

                // Sync parried ghosts across both screens
                MausoleumLevelGhostBase[] ghosts = UnityEngine.Object.FindObjectsOfType<MausoleumLevelGhostBase>();
                if (ghosts != null && ghosts.Length > 0)
                {
                    for (int i = 0; i < ghosts.Length; i++)
                    {
                        MausoleumLevelGhostBase g = ghosts[i];
                        if (g == null || !g.isDead)
                            continue;

                        int gId = g.GetInstanceID();
                        if (_parriedMausoleumGhostIds.Add(gId) && _transport.IsConnected)
                        {
                            Vector3 gPos = g.transform.position;
                            int isBig = (g is MausoleumLevelBigGhost) ? 1 : 0;
                            byte[] pkt = CoopProtocol.BuildLevelEvent(
                                LevelEventType.MausoleumGhostParried,
                                (byte)GetLocalPlayerId(),
                                gPos.x,
                                gPos.y,
                                isBig);
                            _transport.Send(pkt, true);
                        }
                    }
                }

                // Issue #5 (v1.3.2): Host broadcasts authoritative alive ghost positions & angles every 5 ticks (~12 Hz)
                // so the Client's ghosts never drift out of sync even if Random.Range diverges mid-wave!
                if (_transport.Role == CoopRole.Host && _transport.IsConnected && !Level.Current.Ending && !Level.Won && (_localTick % 5 == 0))
                {
                    int aliveCount = 0;
                    if (ghosts != null)
                    {
                        for (int i = 0; i < ghosts.Length; i++)
                        {
                            if (ghosts[i] != null && !ghosts[i].isDead)
                                aliveCount++;
                        }
                    }

                    int sendCount = Math.Min(aliveCount, 24);
                    MausoleumGhostEntry[] entries = new MausoleumGhostEntry[sendCount];
                    int idx = 0;
                    if (ghosts != null)
                    {
                        for (int i = 0; i < ghosts.Length && idx < sendCount; i++)
                        {
                            MausoleumLevelGhostBase g = ghosts[i];
                            if (g == null || g.isDead) continue;
                            entries[idx++] = new MausoleumGhostEntry
                            {
                                GhostId = g.GetInstanceID(),
                                GhostType = GetMausoleumGhostTypeByte(g),
                                PosX = g.transform.position.x,
                                PosY = g.transform.position.y,
                                AngleZ = g.transform.eulerAngles.z,
                                Speed = 0f
                            };
                        }
                    }

                    byte[] ghostSyncPkt = CoopProtocol.BuildMausoleumGhostSync(entries, idx);
                    _transport.Send(ghostSyncPkt, false);
                }
            }
            catch { }
        }

        private void HandleMausoleumGhostSync(BinaryReader br)
        {
            ushort count = br.ReadUInt16();
            MausoleumGhostEntry[] entries = new MausoleumGhostEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i].GhostId = br.ReadInt32();
                entries[i].GhostType = br.ReadByte();
                entries[i].PosX = br.ReadSingle();
                entries[i].PosY = br.ReadSingle();
                entries[i].AngleZ = br.ReadSingle();
                entries[i].Speed = br.ReadSingle();
            }
            ApplyRemoteMausoleumGhostSync(entries);
        }

        public void ApplyRemoteMausoleumGhostSync(MausoleumGhostEntry[] entries)
        {
            if (_transport.Role != CoopRole.Client || !(Level.Current is MausoleumLevel) || Level.Current.Ending || Level.Won)
                return;

            try
            {
                MausoleumLevelGhostBase[] localGhosts = UnityEngine.Object.FindObjectsOfType<MausoleumLevelGhostBase>();
                if (localGhosts == null)
                    return;

                HashSet<int> usedLocalIds = new HashSet<int>();
                for (int i = 0; i < entries.Length; i++)
                {
                    MausoleumGhostEntry e = entries[i];
                    MausoleumLevelGhostBase matched = null;

                    if (_clientBoundMausoleumGhosts.TryGetValue(e.GhostId, out matched))
                    {
                        if (matched == null || matched.isDead)
                        {
                            matched = null;
                            _clientBoundMausoleumGhosts.Remove(e.GhostId);
                        }
                    }

                    if (matched == null)
                    {
                        float bestDist = float.MaxValue;
                        for (int j = 0; j < localGhosts.Length; j++)
                        {
                            MausoleumLevelGhostBase lg = localGhosts[j];
                            if (lg == null || lg.isDead || usedLocalIds.Contains(lg.GetInstanceID()))
                                continue;
                            if (GetMausoleumGhostTypeByte(lg) != e.GhostType)
                                continue;

                            float d = Vector2.Distance(new Vector2(lg.transform.position.x, lg.transform.position.y), new Vector2(e.PosX, e.PosY));
                            if (d < bestDist)
                            {
                                bestDist = d;
                                matched = lg;
                            }
                        }

                        if (matched != null)
                        {
                            _clientBoundMausoleumGhosts[e.GhostId] = matched;
                        }
                    }

                    if (matched != null)
                    {
                        usedLocalIds.Add(matched.GetInstanceID());
                        Vector3 targetPos = new Vector3(e.PosX, e.PosY, matched.transform.position.z);
                        if (Vector3.Distance(matched.transform.position, targetPos) > 120f)
                            matched.transform.position = targetPos;
                        else
                            matched.transform.position = Vector3.Lerp(matched.transform.position, targetPos, 0.7f);

                        matched.transform.eulerAngles = new Vector3(0f, 0f, e.AngleZ);
                    }
                }

                // If Host has 0 alive ghosts while Client still has leftover ghosts at wave end, clean them up
                if (entries.Length == 0 && localGhosts.Length > 0)
                {
                    for (int j = 0; j < localGhosts.Length; j++)
                    {
                        if (localGhosts[j] != null && !localGhosts[j].isDead)
                        {
                            try { localGhosts[j].OnBossDeath(); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        private void OnClientIgnoredMausoleumLose()
        {
            // Intentionally ignored on Client; Mausoleum urn failure is Host-authoritative via LevelEventType.LevelLose.
        }

        private void OnHostMausoleumFailure()
        {
            if (_sentMausoleumLose || !_transport.IsConnected || _transport.Role != CoopRole.Host)
                return;
            _sentMausoleumLose = true;
            CupheadCoopBootstrapper.Log("[CoopDiag] LevelOutcome: Host urn failed in Mausoleum; broadcasting LevelLose.");
            byte[] losePkt = CoopProtocol.BuildLevelEvent(LevelEventType.LevelLose, (byte)PlayerId.PlayerOne, 0f, 0f, (int)Levels.Mausoleum);
            _transport.Send(losePkt, true);
        }

        public void ApplyRemoteMausoleumGhostParried(Vector2 pos, bool isBigGhost)
        {
            if (Level.Current == null)
                return;

            try
            {
                MausoleumLevelGhostBase[] ghosts = UnityEngine.Object.FindObjectsOfType<MausoleumLevelGhostBase>();
                if (ghosts == null || ghosts.Length == 0)
                    return;

                MausoleumLevelGhostBase best = null;
                float bestDist = float.MaxValue;

                for (int i = 0; i < ghosts.Length; i++)
                {
                    MausoleumLevelGhostBase g = ghosts[i];
                    if (g == null || g.isDead)
                        continue;

                    bool gIsBig = g is MausoleumLevelBigGhost;
                    if (gIsBig != isBigGhost)
                        continue;

                    float d = Vector2.Distance(new Vector2(g.transform.position.x, g.transform.position.y), pos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = g;
                    }
                }

                if (best != null && bestDist <= 500f)
                {
                    _parriedMausoleumGhostIds.Add(best.GetInstanceID());
                    MethodInfo onParry = best.GetType().GetMethod("OnParry", AllFlags);
                    if (onParry != null)
                    {
                        onParry.Invoke(best, new object[] { PlayerManager.GetPlayer(GetRemotePlayerId()) });
                    }
                }
            }
            catch { }
        }

        public void ForceClientMausoleumLose()
        {
            MausoleumLevel mausoleum = Level.Current as MausoleumLevel;
            if (mausoleum == null || Level.Current.Ending || Level.Won)
                return;

            try
            {
                MethodInfo failMethod = typeof(MausoleumLevel).GetMethod("Failure", AllFlags);
                if (failMethod != null)
                {
                    failMethod.Invoke(mausoleum, null);
                }
                CupheadCoopBootstrapper.Log("[CoopDiag] LevelOutcome: Client applied authoritative MausoleumLose.");
            }
            catch { }
        }

        // =====================================================================
        // 11. MAP LEVEL BADGE & DIFFICULTY SELECTION SYNC (Issue #7)
        // =====================================================================

        private static bool IsMapStartUiActiveOrLoading(AbstractMapSceneStartUI ui)
        {
            if (ui == null)
                return false;
            return ui.CurrentState == AbstractMapSceneStartUI.State.Active
                || ui.CurrentState == AbstractMapSceneStartUI.State.Loading;
        }

        private static bool AreAllMapStartUisInactive()
        {
            if (MapDifficultySelectStartUI.Current != null && MapDifficultySelectStartUI.Current.CurrentState != AbstractMapSceneStartUI.State.Inactive)
                return false;
            if (MapConfirmStartUI.Current != null && MapConfirmStartUI.Current.CurrentState != AbstractMapSceneStartUI.State.Inactive)
                return false;
            if (MapBasicStartUI.Current != null && MapBasicStartUI.Current.CurrentState != AbstractMapSceneStartUI.State.Inactive)
                return false;
            return true;
        }

        private void MonitorMapLevelSelectSync()
        {
            if (Map.Current == null)
            {
                _wasLocalMapStartUiOpen = false;
                _isRemoteMapStartUiOpen = false;
                _remoteMapStartUiKind = 0;
                _lastSentMapStartUiKind = 0;
                return;
            }

            if (_applyingRemoteMapStartUi)
                return;

            try
            {
                // Issue #7 (v1.3.2): Only consider a UI open for local monitoring when CurrentState is Active or Loading!
                // Never treat State.Animating (which persists during Out() fade_cr) as open, preventing the infinite In()/Out() flash loop!
                byte activeKind = 0;
                AbstractMapSceneStartUI activeUi = null;

                if (IsMapStartUiActiveOrLoading(MapDifficultySelectStartUI.Current))
                {
                    activeKind = 1;
                    activeUi = MapDifficultySelectStartUI.Current;
                }
                else if (IsMapStartUiActiveOrLoading(MapConfirmStartUI.Current))
                {
                    activeKind = 2;
                    activeUi = MapConfirmStartUI.Current;
                }
                else if (IsMapStartUiActiveOrLoading(MapBasicStartUI.Current))
                {
                    activeKind = 3;
                    activeUi = MapBasicStartUI.Current;
                }

                if (_isRemoteMapStartUiOpen)
                {
                    if (AreAllMapStartUisInactive())
                    {
                        _isRemoteMapStartUiOpen = false;
                        _remoteMapStartUiKind = 0;
                    }
                    else if (activeUi != null && ReInput.isReady)
                    {
                        RouteMapStartUiInputToLocalPlayer(activeUi, ReInput.players.GetPlayer(1));
                    }
                    return;
                }

                if (Time.unscaledTime < _ignoreLocalMapStartUiOpenUntilTime)
                    return;

                if (activeKind != 0 && activeUi != null)
                {
                    string lvlName = activeUi.level ?? string.Empty;
                    int diffMode = (int)Level.CurrentMode;
                    int cursorIdx = GetMapDifficultyUiCursorIndex();

                    if (!_wasLocalMapStartUiOpen
                        || activeKind != _lastSentMapStartUiKind
                        || lvlName != _lastSentMapStartLevel
                        || diffMode != _lastSentMapStartDiff
                        || cursorIdx != _lastSentMapStartCursor)
                    {
                        _wasLocalMapStartUiOpen = true;
                        _lastSentMapStartUiKind = activeKind;
                        _lastSentMapStartLevel = lvlName;
                        _lastSentMapStartDiff = diffMode;
                        _lastSentMapStartCursor = cursorIdx;

                        byte[] pkt = CoopProtocol.BuildMapLevelSelectSync(activeKind, true, lvlName, diffMode, cursorIdx);
                        _transport.Send(pkt, true);
                    }
                }
                else if (_wasLocalMapStartUiOpen && AreAllMapStartUisInactive())
                {
                    _wasLocalMapStartUiOpen = false;
                    _ignoreLocalMapStartUiOpenUntilTime = Time.unscaledTime + 0.35f;
                    byte[] pkt = CoopProtocol.BuildMapLevelSelectSync(_lastSentMapStartUiKind, false, _lastSentMapStartLevel, _lastSentMapStartDiff, _lastSentMapStartCursor);
                    _transport.Send(pkt, true);
                    _lastSentMapStartUiKind = 0;
                }
            }
            catch { }
        }

        private static int GetMapDifficultyUiCursorIndex()
        {
            if (MapDifficultySelectStartUI.Current == null)
                return 0;
            try
            {
                FieldInfo idxField = typeof(MapDifficultySelectStartUI).GetField("index", AllFlags);
                if (idxField != null)
                    return (int)idxField.GetValue(MapDifficultySelectStartUI.Current);
            }
            catch { }
            return (int)Level.CurrentMode;
        }

        private void HandleMapLevelSelectSync(BinaryReader br)
        {
            byte uiKind = br.ReadByte();
            bool isOpen = br.ReadBoolean();
            string levelName = br.ReadString();
            int diffMode = br.ReadInt32();
            int cursorIndex = br.ReadInt32();

            ApplyRemoteMapLevelSelectSync(uiKind, isOpen, levelName, diffMode, cursorIndex);
        }

        public void ApplyRemoteMapLevelSelectSync(byte uiKind, bool isOpen, string levelName, int difficultyMode, int cursorIndex)
        {
            if (Map.Current == null)
                return;

            _applyingRemoteMapStartUi = true;
            try
            {
                AbstractMapSceneStartUI targetUi = null;
                if (uiKind == 1) targetUi = MapDifficultySelectStartUI.Current;
                else if (uiKind == 2) targetUi = MapConfirmStartUI.Current;
                else if (uiKind == 3) targetUi = MapBasicStartUI.Current;

                if (targetUi == null)
                    return;

                if (isOpen)
                {
                    _isRemoteMapStartUiOpen = true;
                    _wasLocalMapStartUiOpen = false;
                    _remoteMapStartUiKind = uiKind;
                    targetUi.level = levelName ?? string.Empty;

                    if (Enum.IsDefined(typeof(Level.Mode), difficultyMode))
                    {
                        Level.SetCurrentMode((Level.Mode)difficultyMode);
                    }

                    if (targetUi.CurrentState == AbstractMapSceneStartUI.State.Inactive)
                    {
                        MapPlayerController remoteMp = null;
                        PlayerId remoteId = GetRemotePlayerId();
                        if (Map.Current.players != null && Map.Current.players.Length > (int)remoteId)
                        {
                            remoteMp = Map.Current.players[(int)remoteId];
                        }
                        if (remoteMp == null && Map.Current.players != null && Map.Current.players.Length > 0)
                        {
                            remoteMp = Map.Current.players[0];
                        }

                        if (remoteMp != null)
                        {
                            targetUi.In(remoteMp);
                        }
                    }

                    if (ReInput.isReady)
                    {
                        RouteMapStartUiInputToLocalPlayer(targetUi, ReInput.players.GetPlayer(1));
                    }

                    if (uiKind == 1 && MapDifficultySelectStartUI.Current != null)
                    {
                        FieldInfo idxField = typeof(MapDifficultySelectStartUI).GetField("index", AllFlags);
                        FieldInfo optionsField = typeof(MapDifficultySelectStartUI).GetField("options", AllFlags);
                        MethodInfo updateCursorMethod = typeof(MapDifficultySelectStartUI).GetMethod("UpdateCursor", AllFlags);
                        FieldInfo diffTextsField = typeof(MapDifficultySelectStartUI).GetField("difficulyTexts", AllFlags);
                        FieldInfo selColorField = typeof(MapDifficultySelectStartUI).GetField("selectedColor", AllFlags);
                        FieldInfo unselColorField = typeof(MapDifficultySelectStartUI).GetField("unselectedColor", AllFlags);

                        if (idxField != null && optionsField != null)
                        {
                            Array opts = optionsField.GetValue(MapDifficultySelectStartUI.Current) as Array;
                            if (opts != null && opts.Length > 0)
                            {
                                int clampedIdx = Mathf.Clamp(cursorIndex, 0, opts.Length - 1);
                                idxField.SetValue(MapDifficultySelectStartUI.Current, clampedIdx);
                            }
                        }

                        if (updateCursorMethod != null)
                        {
                            updateCursorMethod.Invoke(MapDifficultySelectStartUI.Current, null);
                        }

                        if (diffTextsField != null && selColorField != null && unselColorField != null)
                        {
                            Array texts = diffTextsField.GetValue(MapDifficultySelectStartUI.Current) as Array;
                            Color selCol = (Color)selColorField.GetValue(MapDifficultySelectStartUI.Current);
                            Color unselCol = (Color)unselColorField.GetValue(MapDifficultySelectStartUI.Current);
                            if (texts != null)
                            {
                                for (int i = 0; i < texts.Length; i++)
                                {
                                    object txtObj = texts.GetValue(i);
                                    if (txtObj == null) continue;
                                    PropertyInfo colProp = txtObj.GetType().GetProperty("color", AllFlags);
                                    if (colProp != null)
                                    {
                                        colProp.SetValue(txtObj, (i == difficultyMode) ? selCol : unselCol, null);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    _isRemoteMapStartUiOpen = false;
                    _wasLocalMapStartUiOpen = false;
                    _remoteMapStartUiKind = 0;
                    _lastSentMapStartUiKind = 0;
                    _ignoreLocalMapStartUiOpenUntilTime = Time.unscaledTime + 0.5f;

                    if (targetUi.CurrentState != AbstractMapSceneStartUI.State.Inactive)
                    {
                        targetUi.Out();
                    }
                    FieldInfo hasPopupField = typeof(AbstractMapInteractiveEntity).GetField("HasPopupOpened", AllFlags);
                    if (hasPopupField != null)
                    {
                        hasPopupField.SetValue(null, false);
                    }
                    if (ReInput.isReady)
                    {
                        RouteMapStartUiInputToLocalPlayer(targetUi, ReInput.players.GetPlayer(0));
                    }
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ApplyRemoteMapLevelSelectSync warning: " + ex.Message);
            }
            finally
            {
                _applyingRemoteMapStartUi = false;
            }
        }

        // =====================================================================
        // CORE MULTIPLAYER CONTROLLERS & INPUT ROUTING
        // =====================================================================

        public void ForceMultiplayerPlayerTwoJoined()
        {
            try
            {
                PlayerManager.Multiplayer = true;

                // Prevent "Remove Player 2" in Pause Menu (which sets slot 1 joinState = Leaving = 4)
                // and keep PlayerTwo slot marked as Joined (3) + UsingController (1) with canJoin = false
                // so PlayerManager.Update() never triggers a duplicate "PLAYER TWO JOINED" spawn on button press.
                FieldInfo slotsField = typeof(PlayerManager).GetField("playerSlots", AllFlags);
                if (slotsField != null)
                {
                    Array slots = slotsField.GetValue(null) as Array;
                    if (slots != null && slots.Length > 1)
                    {
                        object slot1 = slots.GetValue(1);
                        if (slot1 != null)
                        {
                            Type slotType = slot1.GetType();
                            FieldInfo joinStateField = slotType.GetField("joinState", AllFlags);
                            FieldInfo ctrlStateField = slotType.GetField("controllerState", AllFlags);
                            FieldInfo canJoinField = slotType.GetField("canJoin", AllFlags);
                            FieldInfo promptField = slotType.GetField("promptBeforeJoin", AllFlags);

                            if (canJoinField != null)
                                canJoinField.SetValue(slot1, false);
                            if (promptField != null)
                                promptField.SetValue(slot1, false);
                            if (joinStateField != null)
                                joinStateField.SetValue(slot1, Enum.ToObject(joinStateField.FieldType, 3)); // Joined = 3
                            if (ctrlStateField != null)
                                ctrlStateField.SetValue(slot1, Enum.ToObject(ctrlStateField.FieldType, 1)); // UsingController = 1
                        }
                    }
                }

                // If in Map and PlayerTwo does not exist yet, spawn PlayerTwo on the Map
                if (Map.Current != null && Map.Current.players != null && Map.Current.players.Length > 1 && Map.Current.players[1] == null && Map.Current.players[0] != null)
                {
                    MethodInfo mapJoin = typeof(Map).GetMethod("OnPlayerJoined", AllFlags);
                    if (mapJoin != null)
                        mapJoin.Invoke(Map.Current, new object[] { PlayerId.PlayerTwo });
                }

                // IMPORTANT: Use PlayerManager.GetPlayer(...) == null instead of !PlayerManager.DoesPlayerExist(...)!
                // DoesPlayerExist() returns false whenever a player is dead (IsDead == true), which previously caused
                // Level.OnPlayerJoined(PlayerTwo) to spawn duplicate clones of PlayerTwo upon dying!
                if (Level.Current != null && Level.Current.PlayersCreated && !Level.Current.Ending &&
                    PlayerManager.GetPlayer(PlayerId.PlayerTwo) == null && PlayerManager.GetPlayer(PlayerId.PlayerOne) != null)
                {
                    MethodInfo lvlJoin = typeof(Level).GetMethod("OnPlayerJoined", AllFlags);
                    if (lvlJoin != null)
                        lvlJoin.Invoke(Level.Current, new object[] { PlayerId.PlayerTwo });
                }

                CleanupDuplicatePlayerControllers();
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ForceMultiplayerPlayerTwoJoined warning: " + ex.Message);
            }
        }

        public static int CleanupDuplicatePlayerControllers()
        {
            int removed = 0;
            try
            {
                AbstractPlayerController canonicalP1 = PlayerManager.GetPlayer(PlayerId.PlayerOne);
                AbstractPlayerController canonicalP2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
                if (canonicalP1 == null && canonicalP2 == null)
                    return 0;

                AbstractPlayerController[] allControllers = UnityEngine.Object.FindObjectsOfType<AbstractPlayerController>();
                if (allControllers == null || allControllers.Length <= 2)
                    return 0;

                for (int i = 0; i < allControllers.Length; i++)
                {
                    AbstractPlayerController ctrl = allControllers[i];
                    if (ctrl == null)
                        continue;

                    if (ctrl.id == PlayerId.PlayerOne && canonicalP1 != null && ctrl != canonicalP1)
                    {
                        UnityEngine.Object.Destroy(ctrl.gameObject);
                        removed++;
                    }
                    else if (ctrl.id == PlayerId.PlayerTwo && canonicalP2 != null && ctrl != canonicalP2)
                    {
                        UnityEngine.Object.Destroy(ctrl.gameObject);
                        removed++;
                    }
                }
            }
            catch { }
            return removed;
        }

        private static void SetPlayerInputActions(PlayerInput pInput, Player rewiredPlayer)
        {
            if (pInput == null)
                return;
            FieldInfo bf = typeof(PlayerInput).GetField("<actions>k__BackingField", AllFlags);
            if (bf != null)
                bf.SetValue(pInput, rewiredPlayer);
        }

        /// <summary>
        /// Routes inputs across Level, Plane, World Map, Map Start Prompts (MapDifficultySelectStartUI /
        /// MapConfirmStartUI / MapBasicStartUI), Equip Menu, and Porkrind's Shop so each player controls
        /// their own character/card/drawer with 0ms local latency.
        /// </summary>
        public void ConfigureLocalAndRemoteInputs()
        {
            try
            {
                if (!ReInput.isReady)
                    return;

                Player localRewiredPlayer = ReInput.players.GetPlayer(0);
                Player unassignedRewiredPlayer = ReInput.players.GetPlayer(1);

                PlayerId localId = GetLocalPlayerId();
                PlayerId remoteId = GetRemotePlayerId();

                // 1. Level / Plane Controllers
                AbstractPlayerController localCtrl = PlayerManager.GetPlayer(localId);
                AbstractPlayerController remoteCtrl = PlayerManager.GetPlayer(remoteId);

                if (localCtrl != null && localCtrl.input != null)
                {
                    SetPlayerInputActions(localCtrl.input, localRewiredPlayer);
                    HookLocalPlayerEvents(localCtrl);

                    if (localCtrl.damageReceiver != null && !localCtrl.damageReceiver.enabled)
                        localCtrl.damageReceiver.enabled = true;
                    if (localCtrl.stats != null)
                        SetPlayerHardInvincibility(localCtrl.stats, false);

                    LevelPlayerController localLvl = localCtrl as LevelPlayerController;
                    if (localLvl != null)
                    {
                        if (localLvl.motor != null && !localLvl.motor.enabled)
                            localLvl.motor.enabled = true;
                        if (localLvl.weaponManager != null)
                        {
                            FieldInfo allowField = typeof(LevelPlayerWeaponManager).GetField("allowInput", AllFlags);
                            if (allowField != null) allowField.SetValue(localLvl.weaponManager, true);
                        }
                    }

                    PlanePlayerController localPlane = localCtrl as PlanePlayerController;
                    if (localPlane != null)
                    {
                        if (localPlane.motor != null && !localPlane.motor.enabled)
                            localPlane.motor.enabled = true;
                        if (localPlane.weaponManager != null && !localPlane.weaponManager.enabled)
                            localPlane.weaponManager.enabled = true;
                    }
                }

                if (remoteCtrl != null && remoteCtrl.input != null)
                {
                    SetPlayerInputActions(remoteCtrl.input, unassignedRewiredPlayer);

                    // Prevent the remote puppet from taking fake local collision/pit damage and dying locally
                    // while the partner is still alive on their screen! Remote health & death are synced from the owner.
                    if (remoteCtrl.damageReceiver != null && remoteCtrl.damageReceiver.enabled)
                        remoteCtrl.damageReceiver.enabled = false;
                    if (remoteCtrl.stats != null)
                        SetPlayerHardInvincibility(remoteCtrl.stats, true);

                    LevelPlayerController remoteLvl = remoteCtrl as LevelPlayerController;
                    if (remoteLvl != null)
                    {
                        if (remoteLvl.motor != null)
                        {
                            remoteLvl.motor.DisableInput();
                            if (remoteLvl.motor.enabled)
                                remoteLvl.motor.enabled = false;
                        }
                        if (remoteLvl.weaponManager != null)
                        {
                            FieldInfo allowField = typeof(LevelPlayerWeaponManager).GetField("allowInput", AllFlags);
                            if (allowField != null) allowField.SetValue(remoteLvl.weaponManager, false);
                        }
                    }

                    PlanePlayerController remotePlane = remoteCtrl as PlanePlayerController;
                    if (remotePlane != null)
                    {
                        if (remotePlane.motor != null && remotePlane.motor.enabled)
                            remotePlane.motor.enabled = false;
                        if (remotePlane.weaponManager != null)
                        {
                            // Issue #9 (v1.3.2): Ensure PlanePlayerWeaponManager.Start() (which runs weapons.Init()
                            // and starts AbstractPlaneWeapon.fireWeapon_cr()) has initialized before disabling FixedUpdate!
                            EnsurePlaneWeaponsInitialized(remotePlane.weaponManager);
                            if (remotePlane.weaponManager.enabled)
                                remotePlane.weaponManager.enabled = false;
                        }
                    }
                }

                // 2. World Map Controllers & Map Level Start Prompts (so Client P2 can enter levels too!)
                if (Map.Current != null && Map.Current.players != null && Map.Current.players.Length > 1)
                {
                    MapPlayerController mapLocal = Map.Current.players[(int)localId];
                    MapPlayerController mapRemote = Map.Current.players[(int)remoteId];

                    if (mapLocal != null)
                    {
                        if (mapLocal.input != null)
                            SetPlayerInputActions(mapLocal.input, localRewiredPlayer);
                        if (mapLocal.motor != null && !mapLocal.motor.enabled)
                            mapLocal.motor.enabled = true;
                        if (mapLocal.animationController != null && !mapLocal.animationController.enabled)
                            mapLocal.animationController.enabled = true;
                    }

                    if (mapRemote != null)
                    {
                        if (mapRemote.input != null)
                            SetPlayerInputActions(mapRemote.input, unassignedRewiredPlayer);
                        if (mapRemote.motor != null && mapRemote.motor.enabled)
                            mapRemote.motor.enabled = false;
                        if (mapRemote.animationController != null && mapRemote.animationController.enabled)
                            mapRemote.animationController.enabled = false;
                    }

                    if (_isRemoteMapStartUiOpen)
                    {
                        RouteMapStartUiInputToLocalPlayer(MapDifficultySelectStartUI.Current, unassignedRewiredPlayer);
                        RouteMapStartUiInputToLocalPlayer(MapConfirmStartUI.Current, unassignedRewiredPlayer);
                        RouteMapStartUiInputToLocalPlayer(MapBasicStartUI.Current, unassignedRewiredPlayer);
                    }
                    else if (_transport.Role == CoopRole.Client)
                    {
                        RouteMapStartUiInputToLocalPlayer(MapDifficultySelectStartUI.Current, localRewiredPlayer);
                        RouteMapStartUiInputToLocalPlayer(MapConfirmStartUI.Current, localRewiredPlayer);
                        RouteMapStartUiInputToLocalPlayer(MapBasicStartUI.Current, localRewiredPlayer);
                    }
                }

                // 3. Equip Menu (MapEquipUI on World Map AND LevelEquipUI on Game Over screen!)
                // Issue #8 (v1.3.2): Do NOT force ReadyAndWaiting=true on remoteCard; let MonitorEquipMenuSync
                // synchronize both cards' selections, rotations, and ReadyAndWaiting states!
                if (AbstractEquipUI.Current != null)
                {
                    FieldInfo p1CardField = typeof(AbstractEquipUI).GetField("playerOne", AllFlags);
                    FieldInfo p2CardField = typeof(AbstractEquipUI).GetField("playerTwo", AllFlags);
                    if (p1CardField != null && p2CardField != null)
                    {
                        MapEquipUICard cardP1 = p1CardField.GetValue(AbstractEquipUI.Current) as MapEquipUICard;
                        MapEquipUICard cardP2 = p2CardField.GetValue(AbstractEquipUI.Current) as MapEquipUICard;

                        MapEquipUICard localCard = (_transport.Role == CoopRole.Client) ? cardP2 : cardP1;
                        MapEquipUICard remoteCard = (_transport.Role == CoopRole.Client) ? cardP1 : cardP2;

                        FieldInfo cardInputField = typeof(MapEquipUICard).GetField("playerInput", AllFlags);
                        FieldInfo inputEnabledField = typeof(MapEquipUICard).GetField("inputEnabled", AllFlags);

                        if (localCard != null)
                        {
                            if (cardInputField != null) cardInputField.SetValue(localCard, localRewiredPlayer);
                            if (inputEnabledField != null) inputEnabledField.SetValue(localCard, true);
                        }

                        if (remoteCard != null)
                        {
                            if (cardInputField != null) cardInputField.SetValue(remoteCard, unassignedRewiredPlayer);
                            if (inputEnabledField != null) inputEnabledField.SetValue(remoteCard, false);
                        }
                    }
                }

                // 4. Porkrind's Emporium (ShopScene)
                // Issue #11 (v1.3.2): Do NOT force remoteShop to Exited when localShop exits!
                // Vanilla ShopScene.OnExit() waits until BOTH players have exited; MonitorShopSceneSync syncs each player's state.
                if (ShopScene.Current != null)
                {
                    FieldInfo s1Field = typeof(ShopScene).GetField("playerOne", AllFlags);
                    FieldInfo s2Field = typeof(ShopScene).GetField("playerTwo", AllFlags);
                    if (s1Field != null && s2Field != null)
                    {
                        ShopScenePlayer shopP1 = s1Field.GetValue(ShopScene.Current) as ShopScenePlayer;
                        ShopScenePlayer shopP2 = s2Field.GetValue(ShopScene.Current) as ShopScenePlayer;

                        ShopScenePlayer localShop = (_transport.Role == CoopRole.Client) ? shopP2 : shopP1;
                        ShopScenePlayer remoteShop = (_transport.Role == CoopRole.Client) ? shopP1 : shopP2;

                        FieldInfo shopInputField = typeof(ShopScenePlayer).GetField("input", AllFlags);

                        if (localShop != null && shopInputField != null)
                        {
                            shopInputField.SetValue(localShop, localRewiredPlayer);
                        }

                        if (remoteShop != null && shopInputField != null)
                        {
                            shopInputField.SetValue(remoteShop, unassignedRewiredPlayer);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ConfigureLocalAndRemoteInputs warning: " + ex.Message);
            }
        }

        public static void EnsurePlaneWeaponsInitialized(PlanePlayerWeaponManager wm)
        {
            if (wm == null)
                return;

            try
            {
                PropertyInfo statesProp = typeof(PlanePlayerWeaponManager).GetProperty("states", AllFlags);
                if (statesProp != null && statesProp.GetValue(wm, null) == null)
                {
                    MethodInfo startMethod = typeof(PlanePlayerWeaponManager).GetMethod("Start", AllFlags);
                    if (startMethod != null)
                    {
                        startMethod.Invoke(wm, null);
                    }
                }

                FieldInfo cwField = typeof(PlanePlayerWeaponManager).GetField("currentWeapon", AllFlags);
                if (cwField != null)
                {
                    Weapon curWep = (Weapon)cwField.GetValue(wm);
                    if (curWep == Weapon.None || (int)curWep == 0)
                    {
                        PlanePlayerController p = wm.GetComponent<PlanePlayerController>();
                        bool isChalice = (p != null && p.stats != null && p.stats.isChalice);
                        Weapon defaultPlaneWep = isChalice ? Weapon.plane_chalice_weapon_3way : Weapon.plane_weapon_peashot;
                        cwField.SetValue(wm, defaultPlaneWep);
                    }
                }

                PropertyInfo stateProp = typeof(PlanePlayerWeaponManager).GetProperty("state", AllFlags);
                if (stateProp != null)
                {
                    int curState = Convert.ToInt32(stateProp.GetValue(wm, null));
                    if (curState == 0) // Inactive = 0 -> Ready = 1
                    {
                        stateProp.SetValue(wm, Enum.ToObject(stateProp.PropertyType, 1), null);
                    }
                }
            }
            catch { }
        }

        // =====================================================================
        // 12. MAP EQUIP MENU & SHOP CO-WAIT SYNC (Issues #8 & #11)
        // =====================================================================

        private void MonitorEquipMenuSync()
        {
            if (AbstractEquipUI.Current == null)
            {
                _wasLocalEquipMenuOpen = false;
                _isRemoteEquipMenuOpen = false;
                _lastSentEquipSide = 255;
                return;
            }

            if (_applyingRemoteEquipMenu)
                return;

            try
            {
                if (AbstractEquipUI.Current.state == AbstractPauseGUI.State.Animating)
                    return;

                bool isOpen = (AbstractEquipUI.Current.CurrentState == AbstractEquipUI.ActiveState.Active
                    || AbstractEquipUI.Current.state == AbstractPauseGUI.State.Paused);

                FieldInfo p1CardField = typeof(AbstractEquipUI).GetField("playerOne", AllFlags);
                FieldInfo p2CardField = typeof(AbstractEquipUI).GetField("playerTwo", AllFlags);
                if (p1CardField == null || p2CardField == null)
                    return;

                MapEquipUICard cardP1 = p1CardField.GetValue(AbstractEquipUI.Current) as MapEquipUICard;
                MapEquipUICard cardP2 = p2CardField.GetValue(AbstractEquipUI.Current) as MapEquipUICard;
                MapEquipUICard localCard = (_transport.Role == CoopRole.Client) ? cardP2 : cardP1;

                if (isOpen && localCard != null)
                {
                    if (Time.unscaledTime < _ignoreLocalEquipMenuUntilTime && !_wasLocalEquipMenuOpen && !_isRemoteEquipMenuOpen)
                        return;

                    FieldInfo sideField = typeof(MapEquipUICard).GetField("side", AllFlags);
                    FieldInfo backField = typeof(MapEquipUICard).GetField("back", AllFlags);
                    FieldInfo frontCardField = typeof(MapEquipUICard).GetField("front", AllFlags);
                    FieldInfo backSelectCardField = typeof(MapEquipUICard).GetField("backSelect", AllFlags);
                    FieldInfo frontIdxField = typeof(MapEquipUICardFront).GetField("index", AllFlags);
                    FieldInfo backIdxField = typeof(MapEquipUICardBackSelect).GetField("index", AllFlags);

                    MapEquipUICardFront localFront = frontCardField != null ? frontCardField.GetValue(localCard) as MapEquipUICardFront : null;
                    MapEquipUICardBackSelect localBackSelect = backSelectCardField != null ? backSelectCardField.GetValue(localCard) as MapEquipUICardBackSelect : null;

                    byte side = sideField != null ? (byte)Convert.ToInt32(sideField.GetValue(localCard)) : (byte)0;
                    byte backMode = backField != null ? (byte)Convert.ToInt32(backField.GetValue(localCard)) : (byte)0;
                    byte slot = (localFront != null) ? (byte)Convert.ToInt32(localFront.Slot) : (byte)0;
                    int frontIdx = (localFront != null && frontIdxField != null) ? (int)frontIdxField.GetValue(localFront) : 0;
                    int backIdx = (localBackSelect != null && backIdxField != null) ? (int)backIdxField.GetValue(localBackSelect) : 0;
                    bool ready = localCard.ReadyAndWaiting;

                    PlayerId localId = GetLocalPlayerId();
                    int pWep = 0, sWep = 0, sup = 0, chm = 0;
                    if (PlayerData.Initialized && PlayerData.Data != null)
                    {
                        PlayerData.PlayerLoadouts.PlayerLoadout ld = PlayerData.Data.Loadouts.GetPlayerLoadout(localId);
                        if (ld != null)
                        {
                            pWep = (int)ld.primaryWeapon;
                            sWep = (int)ld.secondaryWeapon;
                            sup = (int)ld.super;
                            chm = (int)ld.charm;
                            SanitizeLoadoutForDlc(ref pWep, ref sWep, ref sup, ref chm);
                        }
                    }

                    if (!_wasLocalEquipMenuOpen
                        || side != _lastSentEquipSide
                        || backMode != _lastSentEquipBack
                        || slot != _lastSentEquipSlot
                        || frontIdx != _lastSentEquipFrontIdx
                        || backIdx != _lastSentEquipBackIdx
                        || ready != _lastSentEquipReady
                        || pWep != _lastSentPrimaryWep
                        || sWep != _lastSentSecondaryWep
                        || sup != _lastSentSuper
                        || chm != _lastSentCharm)
                    {
                        if (!_wasLocalEquipMenuOpen)
                        {
                            CupheadCoopBootstrapper.Log("[CoopDiag] EquipUI opened locally; syncing to partner.");
                        }

                        _wasLocalEquipMenuOpen = true;
                        _lastSentEquipSide = side;
                        _lastSentEquipBack = backMode;
                        _lastSentEquipSlot = slot;
                        _lastSentEquipFrontIdx = frontIdx;
                        _lastSentEquipBackIdx = backIdx;
                        _lastSentEquipReady = ready;

                        byte[] pkt = CoopProtocol.BuildEquipMenuSync(
                            true,
                            (byte)localId,
                            side,
                            backMode,
                            slot,
                            frontIdx,
                            backIdx,
                            ready,
                            pWep,
                            sWep,
                            sup,
                            chm);
                        _transport.Send(pkt, true);
                    }
                }
                else if (!isOpen && (_wasLocalEquipMenuOpen || _isRemoteEquipMenuOpen))
                {
                    _wasLocalEquipMenuOpen = false;
                    _isRemoteEquipMenuOpen = false;
                    _lastSentEquipSide = 255;
                    _ignoreLocalEquipMenuUntilTime = Time.unscaledTime + 0.4f;

                    CupheadCoopBootstrapper.Log("[CoopDiag] EquipUI closed locally; syncing close and loadout to partner.");
                    byte[] pkt = CoopProtocol.BuildEquipMenuSync(
                        false,
                        (byte)GetLocalPlayerId(),
                        0, 0, 0, 0, 0, false, 0, 0, 0, 0);
                    _transport.Send(pkt, true);
                    SendLocalLoadout(true);

                    if (Map.Current != null)
                    {
                        if (_transport.Role == CoopRole.Host)
                        {
                            SendWorldSaveMirror();
                        }
                        if (BothPlayersHaveDlc)
                        {
                            RefreshDlcBoatmanOnMapIfUnlocked();
                        }
                    }
                }
            }
            catch { }
        }

        private void HandleEquipMenuSync(BinaryReader br)
        {
            bool isOpen = br.ReadBoolean();
            byte playerId = br.ReadByte();
            byte side = br.ReadByte();
            byte backMode = br.ReadByte();
            byte slot = br.ReadByte();
            int frontIdx = br.ReadInt32();
            int backIdx = br.ReadInt32();
            bool ready = br.ReadBoolean();
            int pWep = br.ReadInt32();
            int sWep = br.ReadInt32();
            int sup = br.ReadInt32();
            int chm = br.ReadInt32();

            ApplyRemoteEquipMenuSync(isOpen, playerId, side, backMode, slot, frontIdx, backIdx, ready, pWep, sWep, sup, chm);
        }

        public void ApplyRemoteEquipMenuSync(
            bool isOpen,
            byte playerId,
            byte side,
            byte backMode,
            byte slot,
            int frontIndex,
            int backIndex,
            bool readyAndWaiting,
            int primaryWep,
            int secondaryWep,
            int superId,
            int charmId)
        {
            if (AbstractEquipUI.Current == null)
                return;

            _applyingRemoteEquipMenu = true;
            try
            {
                FieldInfo p1CardField = typeof(AbstractEquipUI).GetField("playerOne", AllFlags);
                FieldInfo p2CardField = typeof(AbstractEquipUI).GetField("playerTwo", AllFlags);
                FieldInfo stateBf = typeof(AbstractPauseGUI).GetField("<state>k__BackingField", AllFlags);
                PropertyInfo curStateProp = typeof(AbstractEquipUI).GetProperty("CurrentState", AllFlags);
                MethodInfo showImm = typeof(AbstractPauseGUI).GetMethod("ShowImmediate", AllFlags);
                MethodInfo hideImm = typeof(AbstractPauseGUI).GetMethod("HideImmediate", AllFlags);

                MapEquipUICard cardP1 = p1CardField != null ? p1CardField.GetValue(AbstractEquipUI.Current) as MapEquipUICard : null;
                MapEquipUICard cardP2 = p2CardField != null ? p2CardField.GetValue(AbstractEquipUI.Current) as MapEquipUICard : null;
                MapEquipUICard localCard = (_transport.Role == CoopRole.Client) ? cardP2 : cardP1;
                MapEquipUICard remoteCard = (_transport.Role == CoopRole.Client) ? cardP1 : cardP2;

                if (isOpen)
                {
                    _isRemoteEquipMenuOpen = true;
                    _wasLocalEquipMenuOpen = true;

                    SanitizeLoadoutForDlc(ref primaryWep, ref secondaryWep, ref superId, ref charmId);
                    ApplyRemoteLoadout(GetRemotePlayerId(), primaryWep, secondaryWep, superId, charmId);

                    if (AbstractEquipUI.Current.CurrentState != AbstractEquipUI.ActiveState.Active
                        && AbstractEquipUI.Current.state != AbstractPauseGUI.State.Paused)
                    {
                        PauseManager.state = PauseManager.State.Unpaused;
                        MethodInfo pauseMethod = typeof(AbstractPauseGUI).GetMethod("Pause", AllFlags);
                        if (pauseMethod != null) pauseMethod.Invoke(AbstractEquipUI.Current, null);

                        if (AbstractEquipUI.Current.state == AbstractPauseGUI.State.Unpaused)
                        {
                            if (stateBf != null) stateBf.SetValue(AbstractEquipUI.Current, AbstractPauseGUI.State.Paused);
                            if (curStateProp != null) curStateProp.SetValue(AbstractEquipUI.Current, AbstractEquipUI.ActiveState.Active, null);
                            MethodInfo onPause = typeof(AbstractEquipUI).GetMethod("OnPause", AllFlags);
                            if (onPause != null) onPause.Invoke(AbstractEquipUI.Current, null);
                            if (showImm != null) showImm.Invoke(AbstractEquipUI.Current, null);
                        }
                        if (PauseManager.state != PauseManager.State.Paused)
                        {
                            PauseManager.Pause();
                        }
                        CupheadCoopBootstrapper.Log("[CoopDiag] Opened EquipUI via remote EquipMenuSync.");
                    }

                    FieldInfo frontCardField = typeof(MapEquipUICard).GetField("front", AllFlags);
                    FieldInfo backSelectCardField = typeof(MapEquipUICard).GetField("backSelect", AllFlags);
                    FieldInfo checkListCardField = typeof(MapEquipUICard).GetField("checkList", AllFlags);

                    MapEquipUICardFront p1Front = (cardP1 != null && frontCardField != null) ? frontCardField.GetValue(cardP1) as MapEquipUICardFront : null;
                    MapEquipUICardFront p2Front = (cardP2 != null && frontCardField != null) ? frontCardField.GetValue(cardP2) as MapEquipUICardFront : null;
                    if (p1Front != null) p1Front.Refresh();
                    if (p2Front != null) p2Front.Refresh();

                    if (remoteCard != null)
                    {
                        remoteCard.CanRotate = true;
                        FieldInfo sideField = typeof(MapEquipUICard).GetField("side", AllFlags);
                        FieldInfo backField = typeof(MapEquipUICard).GetField("back", AllFlags);
                        FieldInfo readyBf = typeof(MapEquipUICard).GetField("<ReadyAndWaiting>k__BackingField", AllFlags);

                        MapEquipUICardFront remFront = frontCardField != null ? frontCardField.GetValue(remoteCard) as MapEquipUICardFront : null;
                        MapEquipUICardBackSelect remBackSelect = backSelectCardField != null ? backSelectCardField.GetValue(remoteCard) as MapEquipUICardBackSelect : null;
                        MapEquipUIChecklist remCheckList = checkListCardField != null ? checkListCardField.GetValue(remoteCard) as MapEquipUIChecklist : null;

                        if (remFront != null)
                        {
                            MethodInfo setFrontCursor = typeof(MapEquipUICardFront).GetMethod("SetCursorPosition", AllFlags);
                            if (setFrontCursor != null)
                            {
                                setFrontCursor.Invoke(remFront, new object[] { frontIndex });
                            }
                            remFront.Refresh();
                        }

                        byte curSide = sideField != null ? (byte)Convert.ToInt32(sideField.GetValue(remoteCard)) : (byte)0;
                        byte curBack = backField != null ? (byte)Convert.ToInt32(backField.GetValue(remoteCard)) : (byte)0;

                        if (side == 0)
                        {
                            if (curSide != 0 || remoteCard.ReadyAndWaiting)
                            {
                                MethodInfo rotFront = typeof(MapEquipUICard).GetMethod("RotateToFront", AllFlags);
                                if (rotFront != null) rotFront.Invoke(remoteCard, null);
                            }
                        }
                        else
                        {
                            if (backMode == 0) // Select
                            {
                                MethodInfo rotBackSelect = typeof(MapEquipUICard).GetMethod("RotateToBackSelect", AllFlags);
                                if (rotBackSelect != null)
                                {
                                    object slotEnum = Enum.ToObject(typeof(MapEquipUICard.Slot), slot);
                                    rotBackSelect.Invoke(remoteCard, new object[] { slotEnum });
                                }
                                if (remBackSelect != null)
                                {
                                    FieldInfo backIdxField = typeof(MapEquipUICardBackSelect).GetField("index", AllFlags);
                                    MethodInfo setBackCursor = typeof(MapEquipUICardBackSelect).GetMethod("SetCursorPosition", AllFlags);
                                    MethodInfo updateText = typeof(MapEquipUICardBackSelect).GetMethod("UpdateText", AllFlags);
                                    if (backIdxField != null) backIdxField.SetValue(remBackSelect, backIndex);
                                    if (setBackCursor != null) setBackCursor.Invoke(remBackSelect, new object[] { backIndex });
                                    if (updateText != null) updateText.Invoke(remBackSelect, null);
                                }
                            }
                            else if (backMode == 1) // Ready
                            {
                                if (curSide != 1 || curBack != 1)
                                {
                                    MethodInfo rotReady = typeof(MapEquipUICard).GetMethod("RotateToBackReady", AllFlags);
                                    if (rotReady != null) rotReady.Invoke(remoteCard, null);
                                }
                            }
                            else if (backMode == 2) // Checklist
                            {
                                if (curSide != 1 || curBack != 2)
                                {
                                    MethodInfo rotCheck = typeof(MapEquipUICard).GetMethod("RotateToCheckList", AllFlags);
                                    if (rotCheck != null) rotCheck.Invoke(remoteCard, null);
                                }
                                if (remCheckList != null)
                                {
                                    remCheckList.SetCursorPosition(backIndex, false);
                                }
                            }
                        }

                        if (readyBf != null)
                        {
                            readyBf.SetValue(remoteCard, readyAndWaiting);
                        }

                        if (readyAndWaiting && localCard != null && localCard.ReadyAndWaiting)
                        {
                            MethodInfo closeMethod = typeof(AbstractEquipUI).GetMethod("Close", AllFlags);
                            if (closeMethod != null) closeMethod.Invoke(AbstractEquipUI.Current, null);
                        }
                    }
                }
                else
                {
                    _isRemoteEquipMenuOpen = false;
                    _wasLocalEquipMenuOpen = false;
                    _lastSentEquipSide = 255;
                    _ignoreLocalEquipMenuUntilTime = Time.unscaledTime + 0.45f;

                    FieldInfo readyBf = typeof(MapEquipUICard).GetField("<ReadyAndWaiting>k__BackingField", AllFlags);
                    if (readyBf != null)
                    {
                        if (cardP1 != null) readyBf.SetValue(cardP1, true);
                        if (cardP2 != null) readyBf.SetValue(cardP2, true);
                    }

                    MethodInfo closeMethod = typeof(AbstractEquipUI).GetMethod("Close", AllFlags);
                    if (closeMethod != null) closeMethod.Invoke(AbstractEquipUI.Current, null);

                    if (AbstractEquipUI.Current.state != AbstractPauseGUI.State.Unpaused)
                    {
                        if (stateBf != null) stateBf.SetValue(AbstractEquipUI.Current, AbstractPauseGUI.State.Unpaused);
                        if (curStateProp != null) curStateProp.SetValue(AbstractEquipUI.Current, AbstractEquipUI.ActiveState.Inactive, null);
                        if (hideImm != null) hideImm.Invoke(AbstractEquipUI.Current, null);
                    }
                    if (PauseManager.state == PauseManager.State.Paused && !_waitingAtLevelReadyGate && !_isPausedByPauseSync)
                    {
                        PauseManager.Unpause();
                    }

                    if (Map.Current != null && BothPlayersHaveDlc)
                    {
                        RefreshDlcBoatmanOnMapIfUnlocked();
                    }
                    CupheadCoopBootstrapper.Log("[CoopDiag] Closed EquipUI via remote EquipMenuSync.");
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ApplyRemoteEquipMenuSync warning: " + ex.Message);
            }
            finally
            {
                _applyingRemoteEquipMenu = false;
            }
        }

        private void MonitorShopSceneSync()
        {
            if (ShopScene.Current == null)
            {
                _lastSentShopState = 255;
                _lastSentShopItemIdx = -1;
                _lastSentShopExiting = false;
                return;
            }

            if (_applyingRemoteShopState)
                return;

            try
            {
                FieldInfo s1Field = typeof(ShopScene).GetField("playerOne", AllFlags);
                FieldInfo s2Field = typeof(ShopScene).GetField("playerTwo", AllFlags);
                if (s1Field == null || s2Field == null)
                    return;

                ShopScenePlayer shopP1 = s1Field.GetValue(ShopScene.Current) as ShopScenePlayer;
                ShopScenePlayer shopP2 = s2Field.GetValue(ShopScene.Current) as ShopScenePlayer;
                ShopScenePlayer localShop = (_transport.Role == CoopRole.Client) ? shopP2 : shopP1;
                if (localShop == null)
                    return;

                FieldInfo stateField = typeof(ShopScenePlayer).GetField("state", AllFlags);
                FieldInfo idxField = typeof(ShopScenePlayer).GetField("index", AllFlags);
                FieldInfo exitingField = typeof(ShopScenePlayer).GetField("exitingShop", AllFlags);
                if (stateField == null)
                    return;

                byte curState = (byte)Convert.ToInt32(stateField.GetValue(localShop));
                int curIdx = idxField != null ? (int)idxField.GetValue(localShop) : 0;
                bool curExiting = exitingField != null && (bool)exitingField.GetValue(localShop);

                if (curState != _lastSentShopState || curIdx != _lastSentShopItemIdx || curExiting != _lastSentShopExiting)
                {
                    _lastSentShopState = curState;
                    _lastSentShopItemIdx = curIdx;
                    _lastSentShopExiting = curExiting;

                    byte[] pkt = CoopProtocol.BuildShopStateSync((byte)GetLocalPlayerId(), curState, curIdx, curExiting);
                    _transport.Send(pkt, true);
                }
            }
            catch { }
        }

        private void HandleShopStateSync(BinaryReader br)
        {
            byte playerId = br.ReadByte();
            byte shopState = br.ReadByte();
            int itemIndex = br.ReadInt32();
            bool exitingShop = br.ReadBoolean();

            ApplyRemoteShopStateSync(playerId, shopState, itemIndex, exitingShop);
        }

        public void ApplyRemoteShopStateSync(byte playerId, byte shopState, int itemIndex, bool exitingShop)
        {
            if (ShopScene.Current == null)
                return;

            _applyingRemoteShopState = true;
            try
            {
                FieldInfo s1Field = typeof(ShopScene).GetField("playerOne", AllFlags);
                FieldInfo s2Field = typeof(ShopScene).GetField("playerTwo", AllFlags);
                if (s1Field == null || s2Field == null)
                    return;

                ShopScenePlayer shopP1 = s1Field.GetValue(ShopScene.Current) as ShopScenePlayer;
                ShopScenePlayer shopP2 = s2Field.GetValue(ShopScene.Current) as ShopScenePlayer;
                ShopScenePlayer remoteShop = (_transport.Role == CoopRole.Client) ? shopP1 : shopP2;
                if (remoteShop == null)
                    return;

                FieldInfo stateField = typeof(ShopScenePlayer).GetField("state", AllFlags);
                FieldInfo idxField = typeof(ShopScenePlayer).GetField("index", AllFlags);
                FieldInfo itemsField = typeof(ShopScenePlayer).GetField("items", AllFlags);
                FieldInfo exitingField = typeof(ShopScenePlayer).GetField("exitingShop", AllFlags);
                MethodInfo updateSelMethod = typeof(ShopScenePlayer).GetMethod("UpdateSelection", AllFlags);
                MethodInfo exitMethod = typeof(ShopScenePlayer).GetMethod("Exit", AllFlags);
                MethodInfo onStartMethod = typeof(ShopScenePlayer).GetMethod("OnStart", AllFlags);
                MethodInfo shopOnExitMethod = typeof(ShopScene).GetMethod("OnExit", AllFlags);

                if (exitingField != null)
                {
                    exitingField.SetValue(remoteShop, exitingShop);
                }

                if (idxField != null && itemsField != null)
                {
                    IList itemsList = itemsField.GetValue(remoteShop) as IList;
                    if (itemsList != null && itemsList.Count > 0)
                    {
                        int clampedIdx = Mathf.Clamp(itemIndex, 0, itemsList.Count - 1);
                        idxField.SetValue(remoteShop, clampedIdx);
                        if ((shopState == 1 || shopState == 2 || shopState == 3) && updateSelMethod != null)
                        {
                            updateSelMethod.Invoke(remoteShop, null);
                        }
                    }
                }

                int curRemoteState = stateField != null ? Convert.ToInt32(stateField.GetValue(remoteShop)) : 0;
                if (shopState == 4 || shopState == 5) // Exiting = 4, Exited = 5
                {
                    if (curRemoteState != 4 && curRemoteState != 5 && exitMethod != null)
                    {
                        exitMethod.Invoke(remoteShop, null);
                    }
                    if (shopState == 5 && stateField != null)
                    {
                        stateField.SetValue(remoteShop, Enum.ToObject(stateField.FieldType, 5));
                        if (shopOnExitMethod != null)
                        {
                            // Vanilla ShopScene.OnExit() checks BOTH playerOne.state and playerTwo.state
                            // and only starts exit_cr() if BOTH players have exited!
                            shopOnExitMethod.Invoke(ShopScene.Current, null);
                        }
                    }
                    CupheadCoopBootstrapper.Log("[CoopDiag] Shop: Partner state=" + shopState + " (waiting for both players to press Exit).");
                }
                else if (shopState == 1 && (curRemoteState == 4 || curRemoteState == 5))
                {
                    if (stateField != null)
                    {
                        stateField.SetValue(remoteShop, Enum.ToObject(stateField.FieldType, 1));
                    }
                    if (onStartMethod != null)
                    {
                        onStartMethod.Invoke(remoteShop, null);
                    }
                    CupheadCoopBootstrapper.Log("[CoopDiag] Shop: Partner canceled Exit and returned to browsing.");
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ApplyRemoteShopStateSync warning: " + ex.Message);
            }
            finally
            {
                _applyingRemoteShopState = false;
            }
        }

        private static void RouteMapStartUiInputToLocalPlayer(AbstractMapSceneStartUI startUi, Player localRewiredPlayer)
        {
            if (startUi == null || localRewiredPlayer == null)
                return;
            FieldInfo playerField = typeof(AbstractMapSceneStartUI).GetField("player", AllFlags);
            if (playerField != null)
            {
                playerField.SetValue(startUi, localRewiredPlayer);
            }
        }

        public static void StopAllResidualPlayerMovement()
        {
            try
            {
                PlayerId[] ids = new PlayerId[] { PlayerId.PlayerOne, PlayerId.PlayerTwo };
                for (int i = 0; i < ids.Length; i++)
                {
                    AbstractPlayerController ctrl = PlayerManager.GetPlayer(ids[i]);
                    if (ctrl == null) continue;

                    Rigidbody2D rb = ctrl.GetComponent<Rigidbody2D>();
                    if (rb != null) rb.velocity = Vector2.zero;

                    LevelPlayerController lvl = ctrl as LevelPlayerController;
                    if (lvl != null)
                    {
                        if (lvl.motor != null)
                        {
                            lvl.motor.DisableInput();
                            lvl.motor.EnableInput();
                        }
                        if (lvl.weaponManager != null && lvl.weaponManager.IsShooting)
                        {
                            lvl.weaponManager.IsShooting = false;
                            MethodInfo endBasic = typeof(LevelPlayerWeaponManager).GetMethod("EndBasic", AllFlags);
                            if (endBasic != null) endBasic.Invoke(lvl.weaponManager, null);
                        }
                    }

                    PlanePlayerController pl = ctrl as PlanePlayerController;
                    if (pl != null && pl.weaponManager != null && pl.weaponManager.IsShooting)
                    {
                        pl.weaponManager.IsShooting = false;
                        MethodInfo endBasic = typeof(PlanePlayerWeaponManager).GetMethod("EndBasic", AllFlags);
                        if (endBasic != null) endBasic.Invoke(pl.weaponManager, null);
                    }
                }

                if (Map.Current != null && Map.Current.players != null)
                {
                    FieldInfo axisField = typeof(MapPlayerMotor).GetField("axis", AllFlags);
                    PropertyInfo velProp = typeof(MapPlayerMotor).GetProperty("velocity", AllFlags);
                    for (int i = 0; i < Map.Current.players.Length; i++)
                    {
                        MapPlayerController mp = Map.Current.players[i];
                        if (mp == null) continue;
                        Rigidbody2D mrb = mp.GetComponent<Rigidbody2D>();
                        if (mrb != null) mrb.velocity = Vector2.zero;
                        if (mp.motor != null)
                        {
                            if (axisField != null) axisField.SetValue(mp.motor, Vector2.zero);
                            if (velProp != null) velProp.SetValue(mp.motor, Vector2.zero, null);
                        }
                        if (mp.animator != null)
                        {
                            mp.animator.SetInteger("Speed", 0);
                            if (mp.animator.GetInteger("X") == 0 && mp.animator.GetInteger("Y") == 0)
                            {
                                mp.animator.SetInteger("Y", -1);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void RestoreDefaultInputs()
        {
            try
            {
                if (!ReInput.isReady)
                    return;

                StopAllResidualPlayerMovement();

                AbstractPlayerController p1 = PlayerManager.GetPlayer(PlayerId.PlayerOne);
                if (p1 != null)
                {
                    if (p1.input != null)
                        SetPlayerInputActions(p1.input, ReInput.players.GetPlayer(0));
                    if (p1.damageReceiver != null)
                        p1.damageReceiver.enabled = true;
                    if (p1.stats != null)
                        SetPlayerHardInvincibility(p1.stats, false);

                    LevelPlayerController l1 = p1 as LevelPlayerController;
                    if (l1 != null)
                    {
                        if (l1.motor != null)
                        {
                            l1.motor.enabled = true;
                            l1.motor.EnableInput();
                        }
                        if (l1.weaponManager != null)
                        {
                            l1.weaponManager.enabled = true;
                            FieldInfo allowField = typeof(LevelPlayerWeaponManager).GetField("allowInput", AllFlags);
                            if (allowField != null) allowField.SetValue(l1.weaponManager, true);
                        }
                    }

                    PlanePlayerController pl1 = p1 as PlanePlayerController;
                    if (pl1 != null)
                    {
                        if (pl1.motor != null) pl1.motor.enabled = true;
                        if (pl1.weaponManager != null) pl1.weaponManager.enabled = true;
                    }
                }

                AbstractPlayerController p2 = PlayerManager.GetPlayer(PlayerId.PlayerTwo);
                if (p2 != null)
                {
                    if (p2.input != null)
                        SetPlayerInputActions(p2.input, ReInput.players.GetPlayer(1));
                    if (p2.damageReceiver != null)
                        p2.damageReceiver.enabled = true;
                    if (p2.stats != null)
                        SetPlayerHardInvincibility(p2.stats, false);
                }

                // Issue #12 (v1.3.2): Also restore World Map PlayerOne input, motor, and animationController!
                if (Map.Current != null && Map.Current.players != null && Map.Current.players.Length > 0)
                {
                    MapPlayerController mapP1 = Map.Current.players[0];
                    if (mapP1 != null)
                    {
                        if (mapP1.input != null)
                            SetPlayerInputActions(mapP1.input, ReInput.players.GetPlayer(0));
                        if (mapP1.motor != null)
                            mapP1.motor.enabled = true;
                        if (mapP1.animationController != null)
                            mapP1.animationController.enabled = true;
                    }
                }
            }
            catch { }
        }

        public static void SetPlayerHardInvincibility(PlayerStatsManager stats, bool invincible)
        {
            if (stats == null)
                return;
            FieldInfo fi = typeof(PlayerStatsManager).GetField("hardInvincibility", AllFlags);
            if (fi != null)
                fi.SetValue(stats, invincible);
        }

        public static bool GetPlayerHardInvincibility(PlayerStatsManager stats)
        {
            if (stats == null)
                return false;
            FieldInfo fi = typeof(PlayerStatsManager).GetField("hardInvincibility", AllFlags);
            return fi != null && (bool)fi.GetValue(stats);
        }

        public void SendLocalLoadout(bool force)
        {
            try
            {
                if (!_transport.IsConnected || !PlayerData.Initialized || PlayerData.Data == null)
                    return;

                PlayerId localId = GetLocalPlayerId();
                PlayerData.PlayerLoadouts.PlayerLoadout loadout = PlayerData.Data.Loadouts.GetPlayerLoadout(localId);
                if (loadout == null)
                    return;

                int pWep = (int)loadout.primaryWeapon;
                int sWep = (int)loadout.secondaryWeapon;
                int sup = (int)loadout.super;
                int chm = (int)loadout.charm;

                if (SanitizeLoadoutForDlc(ref pWep, ref sWep, ref sup, ref chm))
                {
                    loadout.primaryWeapon = (Weapon)pWep;
                    loadout.secondaryWeapon = (Weapon)sWep;
                    loadout.super = (Super)sup;
                    loadout.charm = (Charm)chm;
                    loadout.HasEquippedSecondaryRegularWeapon = (sWep != (int)Weapon.None && sWep != 0);
                }

                if (!force &&
                    pWep == _lastSentPrimaryWep &&
                    sWep == _lastSentSecondaryWep &&
                    sup == _lastSentSuper &&
                    chm == _lastSentCharm)
                {
                    return;
                }

                _lastSentPrimaryWep = pWep;
                _lastSentSecondaryWep = sWep;
                _lastSentSuper = sup;
                _lastSentCharm = chm;

                byte[] pkt = CoopProtocol.BuildLoadoutSync((byte)localId, pWep, sWep, sup, chm);
                _transport.Send(pkt, true);
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("SendLocalLoadout warning: " + ex.Message);
            }
        }

        public static void SetPlayerCurrencyExact(PlayerId player, int targetCoins)
        {
            if (!PlayerData.Initialized || PlayerData.Data == null)
                return;
            int clamped = Math.Max(0, targetCoins);
            int current = PlayerData.Data.GetCurrency(player);
            if (current != clamped)
            {
                PlayerData.Data.AddCurrency(player, clamped - current);
            }
        }

        public void MirrorAndSyncPlayerInventories(bool forceSend)
        {
            try
            {
                if (!PlayerData.Initialized || PlayerData.Data == null)
                    return;

                FieldInfo invsField = typeof(PlayerData).GetField("inventories", AllFlags);
                if (invsField == null)
                    return;

                object inventories = invsField.GetValue(PlayerData.Data);
                if (inventories == null)
                    return;

                Type invsType = inventories.GetType();
                FieldInfo p1Field = invsType.GetField("playerOne", AllFlags);
                FieldInfo p2Field = invsType.GetField("playerTwo", AllFlags);
                if (p1Field == null || p2Field == null)
                    return;

                object invP1 = p1Field.GetValue(inventories);
                object invP2 = p2Field.GetValue(inventories);
                if (invP1 == null || invP2 == null)
                    return;

                Type invType = invP1.GetType();
                FieldInfo wepsField = invType.GetField("_weapons", AllFlags);
                FieldInfo supsField = invType.GetField("_supers", AllFlags);
                FieldInfo chmsField = invType.GetField("_charms", AllFlags);
                if (wepsField == null || supsField == null || chmsField == null)
                    return;

                IList w1 = wepsField.GetValue(invP1) as IList;
                IList w2 = wepsField.GetValue(invP2) as IList;
                IList s1 = supsField.GetValue(invP1) as IList;
                IList s2 = supsField.GetValue(invP2) as IList;
                IList c1 = chmsField.GetValue(invP1) as IList;
                IList c2 = chmsField.GetValue(invP2) as IList;

                int p1ItemsBefore = (w1 != null ? w1.Count : 0) + (s1 != null ? s1.Count : 0) + (c1 != null ? c1.Count : 0);

                MirrorList(w1, w2);
                MirrorList(s1, s2);
                MirrorList(c1, c2);

                int p1ItemsAfter = (w1 != null ? w1.Count : 0) + (s1 != null ? s1.Count : 0) + (c1 != null ? c1.Count : 0);
                int coinsP1 = PlayerData.Data.GetCurrency(PlayerId.PlayerOne);
                int coinsP2 = PlayerData.Data.GetCurrency(PlayerId.PlayerTwo);

                // In singleplayer Cuphead, every collected coin adds +1 to BOTH PlayerOne and PlayerTwo, while only
                // PlayerOne spends coins in Porkrind's Shop — leaving PlayerTwo with up to 33+ unspent coins!
                // Because we mirror all unlocked weapons/supers/charms from PlayerOne to PlayerTwo, PlayerOne's coin
                // balance is authoritative unless PlayerTwo just bought a new item inside ShopScene.
                bool p2BoughtInShop = (ShopScene.Current != null && p1ItemsAfter > p1ItemsBefore && coinsP2 < coinsP1);
                int authoritativeCoins = p2BoughtInShop ? coinsP2 : coinsP1;
                SetPlayerCurrencyExact(PlayerId.PlayerOne, authoritativeCoins);
                SetPlayerCurrencyExact(PlayerId.PlayerTwo, authoritativeCoins);

                int totalCount = p1ItemsAfter + authoritativeCoins * 2;

                // CRITICAL: Only the Host (or a Client who just purchased an item in Porkrind's Shop) may broadcast
                // InventorySync! Otherwise a Client with 33 coins on their local save slot before WorldSaveMirror arrives
                // would overwrite the Host's fresh save with 33 coins!
                bool canBroadcastInventory = (_transport.Role == CoopRole.Host) || p2BoughtInShop;
                if (_transport.IsConnected && canBroadcastInventory && (forceSend || totalCount != _lastSyncedInventoryCount))
                {
                    _lastSyncedInventoryCount = totalCount;
                    int[] weps = ListToIntArray(w1);
                    int[] sups = ListToIntArray(s1);
                    int[] chms = ListToIntArray(c1);
                    byte[] pkt = CoopProtocol.BuildInventorySync(
                        weps,
                        sups,
                        chms,
                        authoritativeCoins,
                        authoritativeCoins);
                    _transport.Send(pkt, true);
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("MirrorAndSyncPlayerInventories warning: " + ex.Message);
            }
        }

        private static void MirrorList(IList a, IList b)
        {
            if (a == null || b == null) return;
            for (int i = 0; i < a.Count; i++)
            {
                object item = a[i];
                if (!b.Contains(item)) b.Add(item);
            }
            for (int i = 0; i < b.Count; i++)
            {
                object item = b[i];
                if (!a.Contains(item)) a.Add(item);
            }
        }

        private static int[] ListToIntArray(IList list)
        {
            if (list == null) return new int[0];
            int[] arr = new int[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                arr[i] = Convert.ToInt32(list[i]);
            }
            return arr;
        }

        private void HookLocalPlayerEvents(AbstractPlayerController localCtrl)
        {
            if (_hookedLocalPlayer == localCtrl || localCtrl == null)
                return;

            _hookedLocalPlayer = localCtrl;

            if (localCtrl.stats != null)
            {
                localCtrl.stats.OnPlayerDeathEvent += OnLocalPlayerDeath;
                localCtrl.stats.OnPlayerReviveEvent += OnLocalPlayerRevive;
            }

            LevelPlayerController lvlCtrl = localCtrl as LevelPlayerController;
            if (lvlCtrl != null && lvlCtrl.weaponManager != null)
            {
                HookComponentActionField(lvlCtrl.weaponManager, "OnExStart", new Action(OnLocalExStarted));
                HookComponentActionField(lvlCtrl.weaponManager, "OnSuperStart", new Action(OnLocalSuperStarted));
            }

            PlanePlayerController planeCtrl = localCtrl as PlanePlayerController;
            if (planeCtrl != null && planeCtrl.weaponManager != null)
            {
                HookComponentActionField(planeCtrl.weaponManager, "OnExStartEvent", new Action(OnLocalExStarted));
                HookComponentActionField(planeCtrl.weaponManager, "OnSuperStartEvent", new Action(OnLocalSuperStarted));
            }
        }

        private static void HookComponentActionField(object target, string fieldName, Action handler)
        {
            if (target == null) return;
            FieldInfo fi = target.GetType().GetField(fieldName, AllFlags);
            if (fi == null) return;
            Action current = fi.GetValue(target) as Action;
            fi.SetValue(target, (Action)Delegate.Combine(current, handler));
        }

        private void OnLocalExStarted()
        {
            if (!_transport.IsConnected || _applyingRemoteWeaponAction)
                return;
            PlayerId localId = GetLocalPlayerId();
            AbstractPlayerController ctrl = PlayerManager.GetPlayer(localId);
            LevelPlayerController lvl = ctrl as LevelPlayerController;
            float ax = lvl != null && lvl.motor != null ? lvl.motor.LookDirection.x.Value : 1f;
            float ay = lvl != null && lvl.motor != null ? lvl.motor.LookDirection.y.Value : 0f;
            byte[] pkt = CoopProtocol.BuildWeaponAction((byte)localId, WeaponActionType.ExStart, 0, ax, ay);
            _transport.Send(pkt, true);
        }

        private void OnLocalSuperStarted()
        {
            if (!_transport.IsConnected || _applyingRemoteWeaponAction)
                return;
            PlayerId localId = GetLocalPlayerId();
            byte[] pkt = CoopProtocol.BuildWeaponAction((byte)localId, WeaponActionType.SuperStart, 0, 1f, 0f);
            _transport.Send(pkt, true);
        }

        private void OnLocalPlayerDeath(PlayerId playerId)
        {
            if (!_transport.IsConnected || _applyingRemoteDeath)
                return;
            AbstractPlayerController p = PlayerManager.GetPlayer(playerId);
            Vector3 pos = p != null ? p.transform.position : Vector3.zero;
            byte[] pkt = CoopProtocol.BuildLevelEvent(LevelEventType.PlayerDeath, (byte)playerId, pos.x, pos.y, 0);
            _transport.Send(pkt, true);
        }

        private void OnLocalPlayerRevive(PlayerId playerId)
        {
            if (!_transport.IsConnected)
                return;
            AbstractPlayerController p = PlayerManager.GetPlayer(playerId);
            Vector3 pos = p != null ? p.transform.position : Vector3.zero;
            byte[] pkt = CoopProtocol.BuildLevelEvent(LevelEventType.PlayerRevive, (byte)playerId, pos.x, pos.y, 0);
            _transport.Send(pkt, true);
        }

        private void MonitorDeathEffectsAndRevives()
        {
            if (Level.Current == null)
                return;

            try
            {
                PlayerDeathEffect[] effects = UnityEngine.Object.FindObjectsOfType<PlayerDeathEffect>();
                if (effects == null || effects.Length == 0)
                    return;

                FieldInfo exitingField = typeof(PlayerDeathEffect).GetField("exiting", AllFlags);
                FieldInfo playerIdField = typeof(PlayerDeathEffect).GetField("playerId", AllFlags);
                FieldInfo parrySwitchField = typeof(PlayerDeathEffect).GetField("parrySwitch", AllFlags);
                if (exitingField == null || playerIdField == null)
                    return;

                for (int i = 0; i < effects.Length; i++)
                {
                    PlayerDeathEffect eff = effects[i];
                    if (eff == null) continue;

                    int instId = eff.GetInstanceID();
                    bool isExiting = (bool)exitingField.GetValue(eff);
                    if (!isExiting || _processedDeathEffects.Contains(instId))
                        continue;

                    _processedDeathEffects.Add(instId);
                    PlayerId deadPlayerId = (PlayerId)playerIdField.GetValue(eff);
                    Component parrySwitch = parrySwitchField != null ? parrySwitchField.GetValue(eff) as Component : null;
                    bool wasParried = parrySwitch != null && !parrySwitch.gameObject.activeSelf;

                    Vector3 pos = eff.transform.position;
                    LevelEventType evtType = wasParried ? LevelEventType.PlayerRevive : LevelEventType.PlayerStealHealthRevive;
                    byte[] pkt = CoopProtocol.BuildLevelEvent(evtType, (byte)deadPlayerId, pos.x, pos.y, 1);
                    _transport.Send(pkt, true);
                }
            }
            catch { }
        }

        private void MonitorKingDiceBoard()
        {
            if (Level.Current == null || !Level.IsDicePalaceMain)
                return;

            try
            {
                DicePalaceMainLevel diceLvl = Level.Current as DicePalaceMainLevel;
                if (diceLvl == null)
                    return;

                FieldInfo gmField = typeof(DicePalaceMainLevel).GetField("gameManager", AllFlags);
                if (gmField == null)
                    return;

                DicePalaceMainLevelGameManager gm = gmField.GetValue(diceLvl) as DicePalaceMainLevelGameManager;
                if (gm == null)
                    return;

                if (_transport.Role == CoopRole.Host && !_sentKingDiceBoardInit)
                {
                    _sentKingDiceBoardInit = true;
                    int[] hearts = DicePalaceMainLevelGameInfo.HEART_INDEXES;
                    int packedHearts = 0;
                    if (hearts != null && hearts.Length >= 3)
                    {
                        packedHearts = (hearts[0] & 0xFF) | ((hearts[1] & 0xFF) << 8) | ((hearts[2] & 0xFF) << 16);
                    }
                    byte[] boardPkt = CoopProtocol.BuildLevelEvent(
                        LevelEventType.KingDiceBoardSync,
                        0,
                        DicePalaceMainLevelGameInfo.PLAYER_SPACES_MOVED,
                        DicePalaceMainLevelGameInfo.TURN_COUNTER,
                        packedHearts);
                    _transport.Send(boardPkt, true);
                }

                FieldInfo diceField = typeof(DicePalaceMainLevelGameManager).GetField("dice", AllFlags);
                if (diceField == null)
                    return;

                DicePalaceMainLevelDice dice = diceField.GetValue(gm) as DicePalaceMainLevelDice;
                if (dice == null)
                    return;

                if (!dice.waitingToRoll && dice.GetInstanceID() != _lastSyncedDiceInstanceId)
                {
                    _lastSyncedDiceInstanceId = dice.GetInstanceID();
                    int rolledVal = (int)dice.roll;
                    byte[] rollPkt = CoopProtocol.BuildLevelEvent(
                        LevelEventType.KingDiceRoll,
                        (byte)GetLocalPlayerId(),
                        DicePalaceMainLevelGameInfo.PLAYER_SPACES_MOVED,
                        0f,
                        rolledVal);
                    _transport.Send(rollPkt, true);
                }
            }
            catch { }
        }

        private void MonitorLevelCoins()
        {
            if (Level.Current == null)
                return;

            try
            {
                LevelCoin[] coins = UnityEngine.Object.FindObjectsOfType<LevelCoin>();
                if (coins == null || coins.Length == 0)
                    return;

                FieldInfo collectedField = typeof(LevelCoin).GetField("_collected", AllFlags);
                if (collectedField == null)
                    return;

                for (int i = 0; i < coins.Length; i++)
                {
                    LevelCoin coin = coins[i];
                    if (coin == null) continue;
                    bool isCollected = (bool)collectedField.GetValue(coin);
                    if (!isCollected) continue;

                    int hash = GetStableStringHash(coin.GlobalID);
                    if (_syncedCoinHashes.Add(hash))
                    {
                        byte[] pkt = CoopProtocol.BuildLevelEvent(
                            LevelEventType.CoinCollected,
                            (byte)GetLocalPlayerId(),
                            0f,
                            0f,
                            hash);
                        _transport.Send(pkt, true);
                    }
                }
            }
            catch { }
        }

        private void MonitorMapDjimmiWish()
        {
            if (Map.Current == null || !PlayerData.Initialized || PlayerData.Data == null)
                return;

            try
            {
                bool active = PlayerData.Data.DjimmiActivatedCurrentRegion();
                if (active && !_lastDjimmiState)
                {
                    _lastDjimmiState = true;
                    byte[] pkt = CoopProtocol.BuildLevelEvent(LevelEventType.DjimmiActivated, (byte)GetLocalPlayerId(), 0f, 0f, 1);
                    _transport.Send(pkt, true);
                }
                else if (!active)
                {
                    _lastDjimmiState = false;
                }
            }
            catch { }
        }

        private static int GetStableStringHash(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0;
            unchecked
            {
                int hash = 23;
                for (int i = 0; i < str.Length; i++)
                {
                    hash = hash * 31 + str[i];
                }
                return hash;
            }
        }

        // =====================================================================
        // 13. ENEMY, SPAWNER & MINION SYNC (Issue #10 - Run-n-Gun, Plane, Carrots)
        // =====================================================================

        private static int GetSpawnerStableHash(PlatformingLevelEnemySpawner spawner)
        {
            if (spawner == null) return 0;
            int posHash = ((int)Mathf.Round(spawner.transform.position.x * 0.5f) * 73856093)
                ^ ((int)Mathf.Round(spawner.transform.position.y * 0.5f) * 19349663);
            return GetStableStringHash(spawner.GetType().Name) ^ posHash;
        }

        private void MonitorEnemiesAndSpawners()
        {
            if (Level.Current == null || Level.Current.Ending || Level.Won || Level.Current is MausoleumLevel)
                return;

            try
            {
                // 1. PlatformingLevelEnemySpawner trigger synchronization
                PlatformingLevelEnemySpawner[] spawners = UnityEngine.Object.FindObjectsOfType<PlatformingLevelEnemySpawner>();
                if (spawners != null && spawners.Length > 0)
                {
                    FieldInfo startedField = typeof(PlatformingLevelEnemySpawner).GetField("started", AllFlags);
                    FieldInfo endedField = typeof(PlatformingLevelEnemySpawner).GetField("ended", AllFlags);
                    if (startedField != null && endedField != null)
                    {
                        for (int i = 0; i < spawners.Length; i++)
                        {
                            PlatformingLevelEnemySpawner sp = spawners[i];
                            if (sp == null) continue;
                            int spHash = GetSpawnerStableHash(sp);

                            if (_transport.Role == CoopRole.Host)
                            {
                                bool isStarted = (bool)startedField.GetValue(sp);
                                bool isEnded = (bool)endedField.GetValue(sp);

                                if (isStarted && _startedSpawnerHashes.Add(spHash))
                                {
                                    byte[] pkt = CoopProtocol.BuildLevelEvent(
                                        LevelEventType.SpawnerTriggerSync,
                                        (byte)PlayerId.PlayerOne,
                                        1f,
                                        0f,
                                        spHash);
                                    _transport.Send(pkt, true);
                                }
                                if (isEnded && _endedSpawnerHashes.Add(spHash))
                                {
                                    byte[] pkt = CoopProtocol.BuildLevelEvent(
                                        LevelEventType.SpawnerTriggerSync,
                                        (byte)PlayerId.PlayerOne,
                                        2f,
                                        0f,
                                        spHash);
                                    _transport.Send(pkt, true);
                                }
                            }
                        }
                    }
                }

                // 2. Active enemies & boss summons (AbstractPlatformingLevelEnemy + VeggiesLevelCarrotHomingProjectile)
                if (_localTick % 5 != 0)
                    return;

                AbstractPlatformingLevelEnemy[] platEnemies = UnityEngine.Object.FindObjectsOfType<AbstractPlatformingLevelEnemy>();
                VeggiesLevelCarrotHomingProjectile[] carrotProjectiles = UnityEngine.Object.FindObjectsOfType<VeggiesLevelCarrotHomingProjectile>();

                int platLen = platEnemies != null ? platEnemies.Length : 0;
                int carrotLen = carrotProjectiles != null ? carrotProjectiles.Length : 0;
                if (platLen == 0 && carrotLen == 0 && _hostEnemyLastKnownHp.Count == 0)
                    return;

                FieldInfo carrotHpField = typeof(VeggiesLevelCarrotHomingProjectile).GetField("health", AllFlags);
                FieldInfo platStartedBf = typeof(AbstractPlatformingLevelEnemy).GetField("<_started>k__BackingField", AllFlags);

                if (_transport.Role == CoopRole.Host)
                {
                    List<EnemySyncEntry> entries = new List<EnemySyncEntry>(24);
                    HashSet<int> currentAliveIds = new HashSet<int>();

                    for (int i = 0; i < platLen && entries.Count < 22; i++)
                    {
                        AbstractPlatformingLevelEnemy e = platEnemies[i];
                        if (e == null) continue;
                        int id = e.GetInstanceID();
                        int typeHash = GetStableStringHash(e.GetType().Name);

                        if (e.Dead || e.Health <= 0f)
                        {
                            if (_hostEnemyLastKnownHp.ContainsKey(id) && _killedEnemyKeys.Add(id))
                            {
                                entries.Add(new EnemySyncEntry
                                {
                                    EnemyKey = id,
                                    TypeHash = typeHash,
                                    PosX = e.transform.position.x,
                                    PosY = e.transform.position.y,
                                    Health = 0f,
                                    ScaleX = (sbyte)(e.transform.localScale.x < 0f ? -1 : 1),
                                    StateFlags = 2 // Dead
                                });
                                _hostEnemyLastKnownHp.Remove(id);
                                _hostEnemyLastKnownPos.Remove(id);
                                _hostEnemyLastKnownType.Remove(id);
                            }
                            continue;
                        }

                        bool isEnemyStarted = platStartedBf == null || (bool)platStartedBf.GetValue(e);
                        if (!isEnemyStarted)
                            continue;

                        currentAliveIds.Add(id);
                        _hostEnemyLastKnownHp[id] = e.Health;
                        _hostEnemyLastKnownPos[id] = new Vector2(e.transform.position.x, e.transform.position.y);
                        _hostEnemyLastKnownType[id] = typeHash;

                        entries.Add(new EnemySyncEntry
                        {
                            EnemyKey = id,
                            TypeHash = typeHash,
                            PosX = e.transform.position.x,
                            PosY = e.transform.position.y,
                            Health = e.Health,
                            ScaleX = (sbyte)(e.transform.localScale.x < 0f ? -1 : 1),
                            StateFlags = 1 // Alive
                        });
                    }

                    int carrotTypeHash = GetStableStringHash("VeggiesLevelCarrotHomingProjectile");
                    for (int i = 0; i < carrotLen && entries.Count < 24; i++)
                    {
                        VeggiesLevelCarrotHomingProjectile c = carrotProjectiles[i];
                        if (c == null) continue;
                        int id = c.GetInstanceID();
                        float hp = carrotHpField != null ? (float)carrotHpField.GetValue(c) : 10f;
                        if (hp <= 0f)
                            continue;

                        currentAliveIds.Add(id);
                        _hostEnemyLastKnownHp[id] = hp;
                        _hostEnemyLastKnownPos[id] = new Vector2(c.transform.position.x, c.transform.position.y);
                        _hostEnemyLastKnownType[id] = carrotTypeHash;

                        entries.Add(new EnemySyncEntry
                        {
                            EnemyKey = id,
                            TypeHash = carrotTypeHash,
                            PosX = c.transform.position.x,
                            PosY = c.transform.position.y,
                            Health = hp,
                            ScaleX = 1,
                            StateFlags = 1 // Alive
                        });
                    }

                    // Emit Death notices for any previously tracked enemy that was Destroyed since last snapshot
                    if (_hostEnemyLastKnownHp.Count > currentAliveIds.Count)
                    {
                        List<int> vanishedIds = new List<int>();
                        foreach (KeyValuePair<int, float> kvp in _hostEnemyLastKnownHp)
                        {
                            if (!currentAliveIds.Contains(kvp.Key))
                                vanishedIds.Add(kvp.Key);
                        }
                        for (int i = 0; i < vanishedIds.Count; i++)
                        {
                            int vid = vanishedIds[i];
                            Vector2 lastPos = _hostEnemyLastKnownPos.ContainsKey(vid) ? _hostEnemyLastKnownPos[vid] : Vector2.zero;
                            int tHash = _hostEnemyLastKnownType.ContainsKey(vid) ? _hostEnemyLastKnownType[vid] : 0;
                            if (entries.Count < 28 && _killedEnemyKeys.Add(vid))
                            {
                                entries.Add(new EnemySyncEntry
                                {
                                    EnemyKey = vid,
                                    TypeHash = tHash,
                                    PosX = lastPos.x,
                                    PosY = lastPos.y,
                                    Health = 0f,
                                    ScaleX = 1,
                                    StateFlags = 2 // Dead
                                });
                            }
                            _hostEnemyLastKnownHp.Remove(vid);
                            _hostEnemyLastKnownPos.Remove(vid);
                            _hostEnemyLastKnownType.Remove(vid);
                        }
                    }

                    if (entries.Count > 0)
                    {
                        byte[] pkt = CoopProtocol.BuildEnemySync(true, entries.ToArray(), entries.Count);
                        _transport.Send(pkt, false);
                    }
                }
                else if (_transport.Role == CoopRole.Client)
                {
                    // Report local Client damage dealt to enemies back to Host
                    List<EnemySyncEntry> dmgReports = null;
                    for (int i = 0; i < platLen; i++)
                    {
                        AbstractPlatformingLevelEnemy e = platEnemies[i];
                        if (e == null) continue;
                        bool isEnemyStarted = platStartedBf == null || (bool)platStartedBf.GetValue(e);
                        if (!isEnemyStarted) continue;
                        int id = e.GetInstanceID();
                        float curHp = e.Dead ? 0f : e.Health;
                        float prevHp;
                        if (!_clientEnemyLastReportedHp.TryGetValue(id, out prevHp))
                        {
                            _clientEnemyLastReportedHp[id] = curHp;
                            continue;
                        }
                        if (curHp < prevHp - 0.1f)
                        {
                            float dmgDealt = prevHp - Mathf.Max(0f, curHp);
                            _clientEnemyLastReportedHp[id] = curHp;
                            if (dmgReports == null) dmgReports = new List<EnemySyncEntry>(4);
                            dmgReports.Add(new EnemySyncEntry
                            {
                                EnemyKey = id,
                                TypeHash = GetStableStringHash(e.GetType().Name),
                                PosX = e.transform.position.x,
                                PosY = e.transform.position.y,
                                Health = dmgDealt,
                                ScaleX = 1,
                                StateFlags = (byte)(e.Dead || curHp <= 0f ? 6 : 4) // 4=Damage, 2=Dead
                            });
                        }
                    }

                    int carrotTypeHash = GetStableStringHash("VeggiesLevelCarrotHomingProjectile");
                    for (int i = 0; i < carrotLen; i++)
                    {
                        VeggiesLevelCarrotHomingProjectile c = carrotProjectiles[i];
                        if (c == null || carrotHpField == null) continue;
                        int id = c.GetInstanceID();
                        float curHp = (float)carrotHpField.GetValue(c);
                        float prevHp;
                        if (!_clientEnemyLastReportedHp.TryGetValue(id, out prevHp))
                        {
                            _clientEnemyLastReportedHp[id] = curHp;
                            continue;
                        }
                        if (curHp < prevHp - 0.1f)
                        {
                            float dmgDealt = prevHp - Mathf.Max(0f, curHp);
                            _clientEnemyLastReportedHp[id] = curHp;
                            if (dmgReports == null) dmgReports = new List<EnemySyncEntry>(4);
                            dmgReports.Add(new EnemySyncEntry
                            {
                                EnemyKey = id,
                                TypeHash = carrotTypeHash,
                                PosX = c.transform.position.x,
                                PosY = c.transform.position.y,
                                Health = dmgDealt,
                                ScaleX = 1,
                                StateFlags = (byte)(curHp <= 0f ? 6 : 4)
                            });
                        }
                    }

                    if (dmgReports != null && dmgReports.Count > 0)
                    {
                        byte[] pkt = CoopProtocol.BuildEnemySync(false, dmgReports.ToArray(), dmgReports.Count);
                        _transport.Send(pkt, true);
                    }
                }
            }
            catch { }
        }

        private void HandleEnemySync(BinaryReader br)
        {
            bool isHostSnapshot = br.ReadBoolean();
            ushort count = br.ReadUInt16();
            EnemySyncEntry[] entries = new EnemySyncEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i].EnemyKey = br.ReadInt32();
                entries[i].TypeHash = br.ReadInt32();
                entries[i].PosX = br.ReadSingle();
                entries[i].PosY = br.ReadSingle();
                entries[i].Health = br.ReadSingle();
                entries[i].ScaleX = br.ReadSByte();
                entries[i].StateFlags = br.ReadByte();
            }
            ApplyRemoteEnemySync(isHostSnapshot, entries);
        }

        public void ApplyRemoteEnemySync(bool isHostSnapshot, EnemySyncEntry[] entries)
        {
            if (Level.Current == null || Level.Current.Ending || Level.Won || entries == null)
                return;

            try
            {
                AbstractPlatformingLevelEnemy[] platEnemies = UnityEngine.Object.FindObjectsOfType<AbstractPlatformingLevelEnemy>();
                VeggiesLevelCarrotHomingProjectile[] carrotProjectiles = UnityEngine.Object.FindObjectsOfType<VeggiesLevelCarrotHomingProjectile>();
                FieldInfo platHpBf = typeof(AbstractPlatformingLevelEnemy).GetField("<Health>k__BackingField", AllFlags);
                FieldInfo platStartedBf = typeof(AbstractPlatformingLevelEnemy).GetField("<_started>k__BackingField", AllFlags);
                MethodInfo platDieMethod = typeof(AbstractPlatformingLevelEnemy).GetMethod("Die", AllFlags);
                MethodInfo platOnStartMethod = typeof(AbstractPlatformingLevelEnemy).GetMethod("OnStart", AllFlags);
                FieldInfo carrotHpField = typeof(VeggiesLevelCarrotHomingProjectile).GetField("health", AllFlags);
                MethodInfo carrotDieMethod = typeof(VeggiesLevelCarrotHomingProjectile).GetMethod("Die", AllFlags);
                int carrotTypeHash = GetStableStringHash("VeggiesLevelCarrotHomingProjectile");

                if (isHostSnapshot && _transport.Role == CoopRole.Client)
                {
                    HashSet<int> matchedLocalIds = new HashSet<int>();

                    for (int i = 0; i < entries.Length; i++)
                    {
                        EnemySyncEntry e = entries[i];
                        bool isDeadEntry = (e.StateFlags & 2) != 0;

                        if (e.TypeHash == carrotTypeHash)
                        {
                            if (carrotProjectiles == null) continue;
                            VeggiesLevelCarrotHomingProjectile bestCarrot = null;
                            float bestDist = float.MaxValue;
                            for (int j = 0; j < carrotProjectiles.Length; j++)
                            {
                                VeggiesLevelCarrotHomingProjectile c = carrotProjectiles[j];
                                if (c == null || matchedLocalIds.Contains(c.GetInstanceID())) continue;
                                float d = Vector2.Distance(new Vector2(c.transform.position.x, c.transform.position.y), new Vector2(e.PosX, e.PosY));
                                if (d < bestDist)
                                {
                                    bestDist = d;
                                    bestCarrot = c;
                                }
                            }

                            if (bestCarrot != null && bestDist <= 650f)
                            {
                                int cid = bestCarrot.GetInstanceID();
                                matchedLocalIds.Add(cid);
                                _clientEnemyLastHostSeenTime[cid] = Time.unscaledTime;
                                if (isDeadEntry || e.Health <= 0f)
                                {
                                    if (carrotDieMethod != null) carrotDieMethod.Invoke(bestCarrot, null);
                                }
                                else
                                {
                                    Vector3 targetPos = new Vector3(e.PosX, e.PosY, bestCarrot.transform.position.z);
                                    if (bestDist > 160f)
                                        bestCarrot.transform.position = targetPos;
                                    else
                                        bestCarrot.transform.position = Vector3.Lerp(bestCarrot.transform.position, targetPos, 0.65f);

                                    if (carrotHpField != null)
                                    {
                                        carrotHpField.SetValue(bestCarrot, e.Health);
                                        _clientEnemyLastReportedHp[cid] = e.Health;
                                    }
                                }
                            }
                            continue;
                        }

                        if (platEnemies == null) continue;
                        AbstractPlatformingLevelEnemy bestEnemy = null;
                        float bestEnemyDist = float.MaxValue;
                        for (int j = 0; j < platEnemies.Length; j++)
                        {
                            AbstractPlatformingLevelEnemy pe = platEnemies[j];
                            if (pe == null || pe.Dead || matchedLocalIds.Contains(pe.GetInstanceID()))
                                continue;
                            if (GetStableStringHash(pe.GetType().Name) != e.TypeHash)
                                continue;

                            float d = Vector2.Distance(new Vector2(pe.transform.position.x, pe.transform.position.y), new Vector2(e.PosX, e.PosY));
                            if (d < bestEnemyDist)
                            {
                                bestEnemyDist = d;
                                bestEnemy = pe;
                            }
                        }

                        if (bestEnemy != null && bestEnemyDist <= 650f)
                        {
                            int pid = bestEnemy.GetInstanceID();
                            matchedLocalIds.Add(pid);
                            _clientEnemyLastHostSeenTime[pid] = Time.unscaledTime;

                            if (isDeadEntry || e.Health <= 0f)
                            {
                                if (platDieMethod != null) platDieMethod.Invoke(bestEnemy, null);
                            }
                            else
                            {
                                bool isEnemyStarted = platStartedBf != null && (bool)platStartedBf.GetValue(bestEnemy);
                                if (!isEnemyStarted && platOnStartMethod != null)
                                {
                                    try { platOnStartMethod.Invoke(bestEnemy, null); } catch { }
                                }
                                Vector3 targetPos = new Vector3(e.PosX, e.PosY, bestEnemy.transform.position.z);
                                if (bestEnemyDist > 180f)
                                    bestEnemy.transform.position = targetPos;
                                else
                                    bestEnemy.transform.position = Vector3.Lerp(bestEnemy.transform.position, targetPos, 0.65f);

                                if (platHpBf != null)
                                {
                                    platHpBf.SetValue(bestEnemy, e.Health);
                                    _clientEnemyLastReportedHp[pid] = e.Health;
                                }
                            }
                        }
                    }

                    // Clean up phantom Client-only spawned enemies that were never seen on Host after 0.85s
                    if (platEnemies != null)
                    {
                        for (int j = 0; j < platEnemies.Length; j++)
                        {
                            AbstractPlatformingLevelEnemy pe = platEnemies[j];
                            if (pe == null || pe.Dead) continue;
                            bool isEnemyStarted = platStartedBf == null || (bool)platStartedBf.GetValue(pe);
                            if (!isEnemyStarted) continue;
                            int pid = pe.GetInstanceID();
                            if (matchedLocalIds.Contains(pid)) continue;

                            float firstSeen;
                            if (!_clientEnemySpawnGraceTime.TryGetValue(pid, out firstSeen))
                            {
                                _clientEnemySpawnGraceTime[pid] = Time.unscaledTime;
                                continue;
                            }

                            float lastHostSeen;
                            bool seenOnHostRecently = _clientEnemyLastHostSeenTime.TryGetValue(pid, out lastHostSeen)
                                && (Time.unscaledTime - lastHostSeen) < 0.85f;
                            if (!seenOnHostRecently && (Time.unscaledTime - firstSeen) > 0.85f)
                            {
                                UnityEngine.Object.Destroy(pe.gameObject);
                            }
                        }
                    }
                }
                else if (!isHostSnapshot && _transport.Role == CoopRole.Host)
                {
                    // Apply Client damage reports onto Host authoritative enemies
                    for (int i = 0; i < entries.Length; i++)
                    {
                        EnemySyncEntry e = entries[i];
                        bool clientKilled = (e.StateFlags & 2) != 0;
                        float dmg = Mathf.Max(0f, e.Health);

                        if (e.TypeHash == carrotTypeHash)
                        {
                            if (carrotProjectiles == null) continue;
                            VeggiesLevelCarrotHomingProjectile bestCarrot = null;
                            float bestDist = float.MaxValue;
                            for (int j = 0; j < carrotProjectiles.Length; j++)
                            {
                                VeggiesLevelCarrotHomingProjectile c = carrotProjectiles[j];
                                if (c == null) continue;
                                float d = Vector2.Distance(new Vector2(c.transform.position.x, c.transform.position.y), new Vector2(e.PosX, e.PosY));
                                if (d < bestDist) { bestDist = d; bestCarrot = c; }
                            }
                            if (bestCarrot != null && bestDist <= 500f && carrotHpField != null)
                            {
                                float curHp = (float)carrotHpField.GetValue(bestCarrot) - dmg;
                                carrotHpField.SetValue(bestCarrot, curHp);
                                if ((clientKilled || curHp <= 0f) && carrotDieMethod != null)
                                {
                                    carrotDieMethod.Invoke(bestCarrot, null);
                                }
                            }
                            continue;
                        }

                        if (platEnemies == null) continue;
                        AbstractPlatformingLevelEnemy bestEnemy = null;
                        float bestEnemyDist = float.MaxValue;
                        for (int j = 0; j < platEnemies.Length; j++)
                        {
                            AbstractPlatformingLevelEnemy pe = platEnemies[j];
                            if (pe == null || pe.Dead || GetStableStringHash(pe.GetType().Name) != e.TypeHash)
                                continue;
                            float d = Vector2.Distance(new Vector2(pe.transform.position.x, pe.transform.position.y), new Vector2(e.PosX, e.PosY));
                            if (d < bestEnemyDist) { bestEnemyDist = d; bestEnemy = pe; }
                        }
                        if (bestEnemy != null && bestEnemyDist <= 500f && platHpBf != null)
                        {
                            float curHp = bestEnemy.Health - dmg;
                            platHpBf.SetValue(bestEnemy, curHp);
                            if ((clientKilled || curHp <= 0f) && platDieMethod != null)
                            {
                                platDieMethod.Invoke(bestEnemy, null);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public void ApplyRemoteSpawnerTriggerSync(int spawnerHash, bool isStart)
        {
            if (Level.Current == null)
                return;

            try
            {
                PlatformingLevelEnemySpawner[] spawners = UnityEngine.Object.FindObjectsOfType<PlatformingLevelEnemySpawner>();
                if (spawners == null) return;

                FieldInfo startedField = typeof(PlatformingLevelEnemySpawner).GetField("started", AllFlags);
                FieldInfo endedField = typeof(PlatformingLevelEnemySpawner).GetField("ended", AllFlags);
                MethodInfo startSpawningMethod = typeof(PlatformingLevelEnemySpawner).GetMethod("StartSpawning", AllFlags);
                MethodInfo endSpawningMethod = typeof(PlatformingLevelEnemySpawner).GetMethod("EndSpawning", AllFlags);

                for (int i = 0; i < spawners.Length; i++)
                {
                    PlatformingLevelEnemySpawner sp = spawners[i];
                    if (sp == null || GetSpawnerStableHash(sp) != spawnerHash)
                        continue;

                    if (isStart)
                    {
                        bool alreadyStarted = startedField != null && (bool)startedField.GetValue(sp);
                        if (!alreadyStarted)
                        {
                            if (startedField != null) startedField.SetValue(sp, true);
                            if (startSpawningMethod != null) startSpawningMethod.Invoke(sp, null);
                        }
                    }
                    else
                    {
                        bool alreadyEnded = endedField != null && (bool)endedField.GetValue(sp);
                        if (!alreadyEnded)
                        {
                            if (endedField != null) endedField.SetValue(sp, true);
                            if (endSpawningMethod != null) endSpawningMethod.Invoke(sp, null);
                        }
                    }
                    break;
                }
            }
            catch { }
        }

        private void EnsureLevelHooks()
        {
            if (Level.Current == null || _hookedLevel == Level.Current)
                return;

            int newInstanceId = Level.Current.GetInstanceID();
            bool isSameSceneReload = (_hookedLevel != null && _lastHookedLevelInstanceId != 0 && _lastHookedLevelInstanceId != newInstanceId);

            _hookedLevel = Level.Current;
            _lastHookedLevelInstanceId = newInstanceId;
            _lastSentBossDamage = 0f;
            _lastSentBossPropertiesHp = -1f;
            _lastHostBossCurrentHealth = -1f;
            _lastHostBossStateIndex = 0;
            _hostConfirmedLevelWin = false;
            _wasLocalShooting = false;
            _wasRemoteParrying = false;
            _wasRemotePlaneShrunk = false;
            _wasRemotePlaneShooting = false;
            _lastSentPlaneWeaponId = 0;
            _processedDeathEffects.Clear();
            _syncedCoinHashes.Clear();
            _parriedMausoleumGhostIds.Clear();
            _clientBoundMausoleumGhosts.Clear();
            _startedSpawnerHashes.Clear();
            _endedSpawnerHashes.Clear();
            _clientEnemyLastHostSeenTime.Clear();
            _clientEnemySpawnGraceTime.Clear();
            _hostEnemyLastKnownHp.Clear();
            _hostEnemyLastKnownPos.Clear();
            _hostEnemyLastKnownType.Clear();
            _killedEnemyKeys.Clear();
            _clientEnemyLastReportedHp.Clear();
            _hookedMausoleumInstanceId = 0;
            _sentMausoleumLose = false;
            SetIgnoreRemoteDeathWindow(2.5f);

            try
            {
                HookLevelActionField(_hookedLevel, "OnPreWinEvent", new Action(OnLocalLevelPreWin));
                HookLevelActionField(_hookedLevel, "OnWinEvent", new Action(OnLocalLevelWin));
                HookLevelActionField(_hookedLevel, "OnStateChangedEvent", new Action(OnLocalBossPhaseChanged));

                if (isSameSceneReload && !_applyingRemoteSceneLoad)
                {
                    _remoteReadyForLevelId = -1;
                    BeginLevelReadyGate((int)Level.Current.CurrentLevel);

                    if (_transport.Role == CoopRole.Host)
                    {
                        _sceneLoadSequence++;
                        _currentRngSeed = Environment.TickCount ^ (_sceneLoadSequence * 7919);
                        SynchronizeBossPropertiesRng(_currentRngSeed);
                        byte[] reloadPkt = CoopProtocol.BuildLevelEvent(
                            LevelEventType.LevelReload,
                            0,
                            (int)Level.Current.CurrentLevel,
                            (int)Level.Current.mode,
                            _currentRngSeed);
                        _transport.Send(reloadPkt, true);
                    }
                    else if (_transport.Role == CoopRole.Client)
                    {
                        _clientInitiatedReload = true;
                        _clientInitiatedReloadTime = Time.unscaledTime;
                        byte[] reqPkt = CoopProtocol.BuildSceneRequest(
                            SceneRequestKind.ReloadLevel,
                            SceneManager.GetActiveScene().name,
                            (int)Level.Current.CurrentLevel,
                            (int)Level.Current.mode);
                        _transport.Send(reqPkt, true);
                    }
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("EnsureLevelHooks warning: " + ex.Message);
            }
        }

        private static void HookLevelActionField(Level level, string fieldName, Action handler)
        {
            FieldInfo fi = typeof(Level).GetField(fieldName, AllFlags);
            if (fi == null)
                return;
            Action current = fi.GetValue(level) as Action;
            fi.SetValue(level, (Action)Delegate.Combine(current, handler));
        }

        private void OnLocalBossPhaseChanged()
        {
            if (!_transport.IsConnected || _transport.Role != CoopRole.Host || Level.Current == null)
                return;

            int phaseSeed = _currentRngSeed ^ (((int)_localTick + 1) * 0x45D9F);
            _currentRngSeed = phaseSeed;
            UnityEngine.Random.InitState(_currentRngSeed);
            float dmg = Level.Current.timeline != null ? Level.Current.timeline.damage : 0f;
            float bossHp = GetBossPropertiesHealth();
            byte stateIdxByte = (byte)Mathf.Clamp(GetBossPropertiesStateIndex(), 0, 255);
            byte[] pkt = CoopProtocol.BuildLevelEvent(LevelEventType.BossPhaseChange, stateIdxByte, dmg, bossHp, _currentRngSeed);
            _transport.Send(pkt, true);
        }

        private void OnLocalLevelPreWin()
        {
            if (!_transport.IsConnected || _transport.Role != CoopRole.Host || Level.Current == null)
                return;

            // Immediately notify Client to trigger synchronized KNOCKOUT!
            byte[] winPkt = CoopProtocol.BuildLevelEvent(
                LevelEventType.LevelWin,
                (byte)PlayerId.PlayerOne,
                Level.Current.timeline != null ? Level.Current.timeline.health : 0f,
                0f,
                (int)Level.Current.CurrentLevel);
            _transport.Send(winPkt, true);
        }

        private void OnLocalLevelWin()
        {
            MirrorAndSyncPlayerInventories(true);

            if (_onlineWrapper != null)
            {
                _onlineWrapper.AuditCoopPostWinAchievements();
            }

            if (!_transport.IsConnected || _transport.Role != CoopRole.Host)
                return;

            OnLocalLevelPreWin();
            SendSaveProgressForCurrentLevel();
            SendWorldSaveMirror();
        }

        public void SendSaveProgressForCurrentLevel()
        {
            try
            {
                if (!PlayerData.Initialized || PlayerData.Data == null || Level.Current == null)
                    return;

                Levels lvl = Level.Current.CurrentLevel;
                PlayerData.PlayerLevelDataObject lvlData = PlayerData.Data.GetLevelData(lvl);
                if (lvlData == null)
                    return;

                byte[] pkt = CoopProtocol.BuildSaveProgressSync(
                    (int)lvl,
                    lvlData.completed,
                    (int)lvlData.grade,
                    (int)lvlData.difficultyBeaten,
                    lvlData.bestTime,
                    lvlData.completedAsChaliceP1,
                    lvlData.completedAsChaliceP2,
                    lvlData.curseCharmP1,
                    lvlData.curseCharmP2,
                    PlayerData.Data.IsHardModeAvailable,
                    PlayerData.Data.IsHardModeAvailableDLC,
                    PlayerData.Data.GetCurrency(PlayerId.PlayerOne),
                    PlayerData.Data.GetCurrency(PlayerId.PlayerTwo));

                _transport.Send(pkt, true);
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("SendSaveProgressForCurrentLevel error: " + ex.Message);
            }
        }

        private void SendLocalPlayerState()
        {
            PlayerId localId = GetLocalPlayerId();

            if (Map.Current != null && Map.Current.players != null && Map.Current.players.Length > (int)localId)
            {
                MapPlayerController mapCtrl = Map.Current.players[(int)localId];
                if (mapCtrl != null)
                {
                    sbyte mx = 0, my = 0;
                    PlayerStateFlags mapFlags = PlayerStateFlags.MapScene;
                    bool isStationaryOrTransition = (mapCtrl.state != MapPlayerController.State.Walking)
                        || (Map.Current.CurrentState != Map.State.Ready);
                    if (isStationaryOrTransition)
                    {
                        mapFlags |= PlayerStateFlags.MapStationary;
                    }
                    else if (mapCtrl.input != null)
                    {
                        mx = (sbyte)mapCtrl.input.GetAxisInt(PlayerInput.Axis.X, false, false);
                        my = (sbyte)mapCtrl.input.GetAxisInt(PlayerInput.Axis.Y, false, false);
                    }

                    PlayerStatePacket mapPkt = new PlayerStatePacket
                    {
                        Tick = _localTick,
                        PlayerIdByte = (byte)localId,
                        PosX = mapCtrl.transform.position.x,
                        PosY = mapCtrl.transform.position.y,
                        VelX = mapCtrl.Velocity.x,
                        VelY = mapCtrl.Velocity.y,
                        MoveX = mx,
                        MoveY = my,
                        Flags = mapFlags,
                        Health = 3,
                        HealthMax = 3
                    };
                    _transport.Send(mapPkt.Serialize(), false);
                    return;
                }
            }

            AbstractPlayerController ctrl = PlayerManager.GetPlayer(localId);
            if (ctrl == null)
                return;

            PlayerStatePacket pkt = new PlayerStatePacket
            {
                Tick = _localTick,
                PlayerIdByte = (byte)localId,
                PosX = ctrl.transform.position.x,
                PosY = ctrl.transform.position.y,
                Health = (byte)(ctrl.stats != null ? Mathf.Clamp(ctrl.stats.Health, 0, 255) : 3),
                HealthMax = (byte)(ctrl.stats != null ? Mathf.Clamp(ctrl.stats.HealthMax, 0, 255) : 3),
                SuperMeter = ctrl.stats != null ? ctrl.stats.SuperMeter : 0f
            };

            PlayerStateFlags flags = PlayerStateFlags.None;
            if (ctrl.IsDead && !IsRemoteDeathCurrentlyIgnored && (Level.Current == null || !Level.Current.Ending))
                flags |= PlayerStateFlags.IsDead;
            if (ctrl.stats != null && ctrl.stats.isChalice) flags |= PlayerStateFlags.IsChalice;

            LevelPlayerController lvlCtrl = ctrl as LevelPlayerController;
            if (lvlCtrl != null)
            {
                if (lvlCtrl.motor != null)
                {
                    pkt.LookX = (sbyte)lvlCtrl.motor.LookDirection.x.Value;
                    pkt.LookY = (sbyte)lvlCtrl.motor.LookDirection.y.Value;
                    int trueLookX = lvlCtrl.motor.TrueLookDirection.x.Value;
                    pkt.MoveX = (sbyte)(trueLookX != 0 ? trueLookX : 1);
                    pkt.MoveY = (sbyte)lvlCtrl.motor.MoveDirection.y.Value;

                    if (lvlCtrl.motor.Grounded) flags |= PlayerStateFlags.Grounded;
                    if (lvlCtrl.motor.Ducking) flags |= PlayerStateFlags.Ducking;
                    if (lvlCtrl.motor.Dashing) flags |= PlayerStateFlags.Dashing;
                    if (lvlCtrl.motor.Parrying) flags |= PlayerStateFlags.Parrying;
                    if (lvlCtrl.motor.Locked) flags |= PlayerStateFlags.Locked;
                    if (lvlCtrl.motor.IsUsingSuperOrEx) flags |= PlayerStateFlags.UsingSuperOrEx;
                }

                if (lvlCtrl.weaponManager != null)
                {
                    bool isShooting = lvlCtrl.weaponManager.IsShooting;
                    if (isShooting) flags |= PlayerStateFlags.Shooting;

                    FieldInfo cwField = typeof(LevelPlayerWeaponManager).GetField("currentWeapon", AllFlags);
                    if (cwField != null)
                    {
                        pkt.CurrentWeaponId = (int)cwField.GetValue(lvlCtrl.weaponManager);
                    }

                    if (isShooting != _wasLocalShooting)
                    {
                        _wasLocalShooting = isShooting;
                        byte[] wPkt = CoopProtocol.BuildWeaponAction(
                            (byte)localId,
                            isShooting ? WeaponActionType.BasicStart : WeaponActionType.BasicEnd,
                            pkt.CurrentWeaponId,
                            pkt.LookX,
                            pkt.LookY);
                        _transport.Send(wPkt, true);
                    }
                }
            }
            else
            {
                PlanePlayerController planeCtrl = ctrl as PlanePlayerController;
                if (planeCtrl != null)
                {
                    flags |= PlayerStateFlags.PlaneScene;
                    if (planeCtrl.Shrunk) flags |= PlayerStateFlags.PlaneShrunk;
                    if (planeCtrl.Parrying) flags |= PlayerStateFlags.Parrying;
                    if (planeCtrl.motor != null)
                    {
                        pkt.VelX = planeCtrl.motor.Velocity.x;
                        pkt.VelY = planeCtrl.motor.Velocity.y;
                        pkt.MoveX = (sbyte)planeCtrl.motor.MoveDirection.x.Value;
                        pkt.MoveY = (sbyte)planeCtrl.motor.MoveDirection.y.Value;
                    }
                    if (planeCtrl.weaponManager != null)
                    {
                        bool isShooting = planeCtrl.weaponManager.IsShooting;
                        if (isShooting) flags |= PlayerStateFlags.Shooting;
                        FieldInfo cwField = typeof(PlanePlayerWeaponManager).GetField("currentWeapon", AllFlags);
                        if (cwField != null)
                        {
                            pkt.CurrentWeaponId = (int)cwField.GetValue(planeCtrl.weaponManager);
                        }

                        // Issue #9 (v1.3.2): Broadcast reliable WeaponAction packets for PlanePlayerController
                        // weapon switches and basic start/end just like LevelPlayerController!
                        if (pkt.CurrentWeaponId != 0 && pkt.CurrentWeaponId != _lastSentPlaneWeaponId)
                        {
                            _lastSentPlaneWeaponId = pkt.CurrentWeaponId;
                            byte[] swPkt = CoopProtocol.BuildWeaponAction(
                                (byte)localId,
                                WeaponActionType.SwitchWeapon,
                                pkt.CurrentWeaponId,
                                1f,
                                0f);
                            _transport.Send(swPkt, true);
                        }

                        if (isShooting != _wasLocalShooting)
                        {
                            _wasLocalShooting = isShooting;
                            byte[] wPkt = CoopProtocol.BuildWeaponAction(
                                (byte)localId,
                                isShooting ? WeaponActionType.BasicStart : WeaponActionType.BasicEnd,
                                pkt.CurrentWeaponId,
                                1f,
                                0f);
                            _transport.Send(wPkt, true);
                        }
                    }
                }
            }

            pkt.Flags = flags;
            _transport.Send(pkt.Serialize(), false);
        }

        private void OnPacketReceived(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader br = new BinaryReader(ms, Encoding.UTF8))
                {
                    PacketType type = (PacketType)br.ReadByte();
                    switch (type)
                    {
                        case PacketType.Handshake:
                            HandleHandshake(br);
                            break;
                        case PacketType.HandshakeReject:
                            HandleHandshakeReject(br);
                            break;
                        case PacketType.PlayerState:
                            HandlePlayerState(PlayerStatePacket.Deserialize(br));
                            break;
                        case PacketType.WeaponAction:
                            HandleWeaponAction(br);
                            break;
                        case PacketType.LevelEvent:
                            HandleLevelEvent(br);
                            break;
                        case PacketType.SceneSync:
                            HandleSceneSync(br);
                            break;
                        case PacketType.SceneRequest:
                            HandleSceneRequest(br);
                            break;
                        case PacketType.WorldSaveMirrorChunk:
                            HandleWorldSaveMirrorChunk(br);
                            break;
                        case PacketType.AchievementSync:
                            HandleAchievementSync(br);
                            break;
                        case PacketType.SaveProgressSync:
                            HandleSaveProgressSync(br);
                            break;
                        case PacketType.LoadoutSync:
                            HandleLoadoutSync(br);
                            break;
                        case PacketType.InventorySync:
                            HandleInventorySync(br);
                            break;
                        case PacketType.MapLevelSelectSync:
                            HandleMapLevelSelectSync(br);
                            break;
                        case PacketType.DialogueSync:
                            HandleDialogueSync(br);
                            break;
                        case PacketType.EquipMenuSync:
                            HandleEquipMenuSync(br);
                            break;
                        case PacketType.ShopStateSync:
                            HandleShopStateSync(br);
                            break;
                        case PacketType.MausoleumGhostSync:
                            HandleMausoleumGhostSync(br);
                            break;
                        case PacketType.EnemySync:
                            HandleEnemySync(br);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("OnPacketReceived parse error: " + ex.Message);
            }
        }

        private void HandleHandshake(BinaryReader br)
        {
            uint protoVer = br.ReadUInt32();
            uint epoch = br.ReadUInt32();
            int rngSeed = br.ReadInt32();
            int primaryWep = br.ReadInt32();
            int secondaryWep = br.ReadInt32();
            int superId = br.ReadInt32();
            int charmId = br.ReadInt32();

            bool remoteDlc = true;
            HandshakeStage stage = HandshakeStage.Hello;
            string remoteVer = "1.0.0";

            if (br.BaseStream.Position < br.BaseStream.Length)
                remoteDlc = br.ReadBoolean();
            if (br.BaseStream.Position < br.BaseStream.Length)
                stage = (HandshakeStage)br.ReadByte();
            if (br.BaseStream.Position < br.BaseStream.Length)
                remoteVer = br.ReadString();

            if (protoVer != CoopProtocol.ProtocolVersion)
            {
                string rejectReason = string.Format(
                    "Versao incompativel! Voce tem v{0} (proto {1}) e o parceiro tem v{2} (proto {3}). Atualizem o mod!",
                    CoopProtocol.ModVersion, CoopProtocol.ProtocolVersion, remoteVer, protoVer);
                _lastVersionMismatchError = rejectReason;
                byte[] rejectPkt = CoopProtocol.BuildHandshakeReject(CoopProtocol.ProtocolVersion, CoopProtocol.ModVersion, rejectReason);
                _transport.Send(rejectPkt, true);
                _transport.RejectIncompatiblePeer(rejectReason);
                return;
            }

            _remoteHasDlc = remoteDlc;
            _remoteModVersion = remoteVer;
            _handshakeCompleted = true;

            if (_transport.Role == CoopRole.Client)
            {
                _sessionEpoch = epoch;
                _currentRngSeed = rngSeed;
                SynchronizeBossPropertiesRng(_currentRngSeed);
            }

            SanitizeLoadoutForDlc(ref primaryWep, ref secondaryWep, ref superId, ref charmId);
            ApplyRemoteLoadout(GetRemotePlayerId(), primaryWep, secondaryWep, superId, charmId);

            if (stage == HandshakeStage.Hello)
            {
                SendHandshakeStage(HandshakeStage.Welcome);
                if (_transport.Role == CoopRole.Host)
                {
                    SendWorldSaveMirror();
                    string activeScene = SceneManager.GetActiveScene().name;
                    if (!string.IsNullOrEmpty(activeScene)
                        && activeScene != "scene_title"
                        && activeScene != "scene_start"
                        && activeScene != "scene_slot_select"
                        && activeScene != "scene_load_helper")
                    {
                        int lvlId = (int)SceneLoader.CurrentLevel;
                        int diff = Level.Current != null ? (int)Level.Current.mode : (int)Level.CurrentMode;
                        if (!IsDlcSceneOrLevel(activeScene, lvlId) || BothPlayersHaveDlc)
                        {
                            _transport.Send(CoopProtocol.BuildSceneSync(activeScene, lvlId, diff, _currentRngSeed, _sceneLoadSequence), true);
                        }
                    }
                }
            }
            else if (stage == HandshakeStage.Welcome)
            {
                SendHandshakeStage(HandshakeStage.Ready);
            }

            if (Map.Current != null && !BothPlayersHaveDlc)
            {
                DisableDlcBoatmanOnMap();
            }

            CupheadCoopBootstrapper.Log(string.Format(
                "Handshake stage={0} ok (proto={1}, ver={2}, remoteDlc={3}, bothHaveDlc={4}, seed={5}).",
                stage, protoVer, remoteVer, remoteDlc, BothPlayersHaveDlc, rngSeed));
        }

        private void HandleHandshakeReject(BinaryReader br)
        {
            uint remoteProto = br.ReadUInt32();
            string remoteVer = br.ReadString();
            string reason = br.ReadString();
            _lastVersionMismatchError = reason;
            _transport.RejectIncompatiblePeer(reason);
            CupheadCoopBootstrapper.Log("Handshake rejected by peer (ver=" + remoteVer + ", proto=" + remoteProto + "): " + reason);
        }

        private void HandleLoadoutSync(BinaryReader br)
        {
            byte playerIdByte = br.ReadByte();
            int primaryWep = br.ReadInt32();
            int secondaryWep = br.ReadInt32();
            int superId = br.ReadInt32();
            int charmId = br.ReadInt32();

            SanitizeLoadoutForDlc(ref primaryWep, ref secondaryWep, ref superId, ref charmId);

            PlayerId remoteId = GetRemotePlayerId();
            if (playerIdByte == (byte)remoteId)
            {
                ApplyRemoteLoadout(remoteId, primaryWep, secondaryWep, superId, charmId);
            }
        }

        private static void ApplyRemoteLoadout(PlayerId targetId, int primaryWep, int secondaryWep, int superId, int charmId)
        {
            if (!PlayerData.Initialized || PlayerData.Data == null || primaryWep == 0)
                return;

            PlayerData.PlayerLoadouts.PlayerLoadout remoteLoadout = PlayerData.Data.Loadouts.GetPlayerLoadout(targetId);
            if (remoteLoadout != null)
            {
                remoteLoadout.primaryWeapon = (Weapon)primaryWep;
                remoteLoadout.secondaryWeapon = (Weapon)secondaryWep;
                remoteLoadout.super = (Super)superId;
                remoteLoadout.charm = (Charm)charmId;
                remoteLoadout.HasEquippedSecondaryRegularWeapon = (secondaryWep != (int)Weapon.None && secondaryWep != 0);
            }

            // Issue #8 (v1.3.2): Immediately refresh the Equip Menu card front icons whenever a remote loadout arrives!
            if (AbstractEquipUI.Current != null)
            {
                try
                {
                    FieldInfo cardField = typeof(AbstractEquipUI).GetField(targetId == PlayerId.PlayerTwo ? "playerTwo" : "playerOne", AllFlags);
                    FieldInfo frontCardField = typeof(MapEquipUICard).GetField("front", AllFlags);
                    if (cardField != null && frontCardField != null)
                    {
                        MapEquipUICard card = cardField.GetValue(AbstractEquipUI.Current) as MapEquipUICard;
                        if (card != null)
                        {
                            MapEquipUICardFront cardFront = frontCardField.GetValue(card) as MapEquipUICardFront;
                            if (cardFront != null)
                            {
                                cardFront.Refresh();
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private void HandleInventorySync(BinaryReader br)
        {
            if (!PlayerData.Initialized || PlayerData.Data == null)
                return;

            // Host save is authoritative; only accept InventorySync on Host if we are inside Porkrind's Shop
            // (where Player 2 can purchase an item) or running automated self-tests.
            if (_transport.Role == CoopRole.Host && ShopScene.Current == null && !CoopOnlineInterface.TestModeNoSteamWrite)
                return;

            int coinsP1 = br.ReadInt32();
            int coinsP2 = br.ReadInt32();

            ushort wLen = br.ReadUInt16();
            for (int i = 0; i < wLen; i++)
            {
                Weapon w = (Weapon)br.ReadInt32();
                if (!BothPlayersHaveDlc && IsDlcWeapon((int)w))
                    continue;
                if (!PlayerData.Data.IsUnlocked(PlayerId.PlayerOne, w))
                    PlayerData.Data.Gift(PlayerId.PlayerOne, w);
                if (!PlayerData.Data.IsUnlocked(PlayerId.PlayerTwo, w))
                    PlayerData.Data.Gift(PlayerId.PlayerTwo, w);
            }

            ushort sLen = br.ReadUInt16();
            for (int i = 0; i < sLen; i++)
            {
                Super s = (Super)br.ReadInt32();
                if (!PlayerData.Data.IsUnlocked(PlayerId.PlayerOne, s))
                    PlayerData.Data.Gift(PlayerId.PlayerOne, s);
                if (!PlayerData.Data.IsUnlocked(PlayerId.PlayerTwo, s))
                    PlayerData.Data.Gift(PlayerId.PlayerTwo, s);
            }

            ushort cLen = br.ReadUInt16();
            for (int i = 0; i < cLen; i++)
            {
                Charm c = (Charm)br.ReadInt32();
                if (!BothPlayersHaveDlc && IsDlcCharm((int)c))
                    continue;
                if (!PlayerData.Data.IsUnlocked(PlayerId.PlayerOne, c))
                    PlayerData.Data.Gift(PlayerId.PlayerOne, c);
                if (!PlayerData.Data.IsUnlocked(PlayerId.PlayerTwo, c))
                    PlayerData.Data.Gift(PlayerId.PlayerTwo, c);
            }

            int authoritativeCoins = Math.Max(0, coinsP1);
            SetPlayerCurrencyExact(PlayerId.PlayerOne, authoritativeCoins);
            SetPlayerCurrencyExact(PlayerId.PlayerTwo, authoritativeCoins);

            if (ShopScene.Current != null)
            {
                MethodInfo checkAll = typeof(ShopScene).GetMethod("HasBoughtEverythingForAchievement", AllFlags);
                if (checkAll != null)
                {
                    bool boughtAll = (bool)checkAll.Invoke(ShopScene.Current, new object[] { PlayerId.PlayerOne });
                    if (boughtAll && OnlineManager.Instance != null && OnlineManager.Instance.Interface != null)
                    {
                        OnlineManager.Instance.Interface.UnlockAchievement(PlayerId.Any, "BoughtAllItems");
                    }
                }
            }
        }

        private void HandlePlayerState(PlayerStatePacket pkt)
        {
            PlayerId remoteId = GetRemotePlayerId();

            // 1. World Map Puppet Sync (Position + Animator X/Y/Speed)
            if ((pkt.Flags & PlayerStateFlags.MapScene) != 0)
            {
                if (Map.Current != null && Map.Current.players != null && Map.Current.players.Length > (int)remoteId)
                {
                    MapPlayerController mapRemote = Map.Current.players[(int)remoteId];
                    if (mapRemote != null)
                    {
                        Vector3 targetPos = new Vector3(pkt.PosX, pkt.PosY, mapRemote.transform.position.z);
                        if (Vector3.Distance(mapRemote.transform.position, targetPos) > 10f)
                            mapRemote.transform.position = targetPos;
                        else
                            mapRemote.transform.position = Vector3.Lerp(mapRemote.transform.position, targetPos, 0.7f);

                        if (mapRemote.animator != null)
                        {
                            // Issue #12 (v1.3.2): Never leave the remote puppet stuck spinning in the "Jump" state
                            // when entering or exiting a non-won location on the World Map, and never set (X=0, Y=0)
                            // on the 8-way directional Animator (preserve last facing direction, defaulting to (0, -1))!
                            if (!Level.Won)
                            {
                                AnimatorStateInfo st = mapRemote.animator.GetCurrentAnimatorStateInfo(0);
                                if (st.IsName("Jump") || st.IsName("WinChalice_Loop"))
                                {
                                    mapRemote.animator.SetTrigger("OnJumpComplete");
                                }
                            }

                            bool isStationary = (pkt.Flags & PlayerStateFlags.MapStationary) != 0
                                || mapRemote.state != MapPlayerController.State.Walking;
                            int mx = isStationary ? 0 : pkt.MoveX;
                            int my = isStationary ? 0 : pkt.MoveY;
                            int speed = (mx != 0 || my != 0) ? 1 : 0;

                            if (mx != 0 || my != 0)
                            {
                                _lastRemoteMapFacingX = (sbyte)mx;
                                _lastRemoteMapFacingY = (sbyte)my;
                            }
                            else if (_lastRemoteMapFacingX == 0 && _lastRemoteMapFacingY == 0)
                            {
                                _lastRemoteMapFacingY = -1;
                            }

                            mapRemote.animator.SetInteger("X", _lastRemoteMapFacingX);
                            mapRemote.animator.SetInteger("Y", _lastRemoteMapFacingY);
                            mapRemote.animator.SetInteger("Speed", speed);

                            if (mx != 0 && mapRemote.animationController != null && mapRemote.animationController.spriteRenderer != null)
                            {
                                Vector3 sc = mapRemote.animationController.spriteRenderer.transform.localScale;
                                sc.x = mx < 0 ? -Mathf.Abs(sc.x) : Mathf.Abs(sc.x);
                                mapRemote.animationController.spriteRenderer.transform.localScale = sc;
                            }
                        }
                    }
                }
                return;
            }

            // 2. Level / Plane Puppet Sync
            AbstractPlayerController remoteCtrl = PlayerManager.GetPlayer(remoteId);
            if (remoteCtrl == null)
                return;

            // Issue #1 & #5 (v1.3.0): While an NPC / Ms. Chalice dialogue is active, keep the remote puppet at
            // its dialogue target X and Grounded so DialogueInteractionPoint.move_cr finishes without waiting!
            float dlgTargetX;
            if (TryGetActiveDialogueTargetX(remoteId, out dlgTargetX))
            {
                pkt.PosX = dlgTargetX;
                pkt.Flags |= PlayerStateFlags.Grounded;
                pkt.Flags &= ~PlayerStateFlags.Locked;
            }

            Vector3 newPos = new Vector3(pkt.PosX, pkt.PosY, remoteCtrl.transform.position.z);
            if (Vector3.Distance(remoteCtrl.transform.position, newPos) > 150f)
                remoteCtrl.transform.position = newPos;
            else
                remoteCtrl.transform.position = Vector3.Lerp(remoteCtrl.transform.position, newPos, 0.75f);

            if (remoteCtrl.stats != null && pkt.Health > 0 && remoteCtrl.stats.Health != pkt.Health)
            {
                remoteCtrl.stats.SetHealth(pkt.Health);
            }

            if (remoteCtrl.stats != null && pkt.SuperMeter >= 0f && Mathf.Abs(remoteCtrl.stats.SuperMeter - pkt.SuperMeter) >= 0.25f)
            {
                try
                {
                    FieldInfo smField = typeof(PlayerStatsManager).GetField("<SuperMeter>k__BackingField", AllFlags);
                    if (smField != null)
                        smField.SetValue(remoteCtrl.stats, pkt.SuperMeter);
                    MethodInfo onSuperChanged = typeof(PlayerStatsManager).GetMethod("OnSuperChanged", AllFlags);
                    if (onSuperChanged != null)
                        onSuperChanged.Invoke(remoteCtrl.stats, new object[] { true });
                }
                catch { }
            }

            // Issue #3 (v1.3.0): Discard stale death packets after Retry / during LevelReadyGate,
            // and self-heal if the partner is alive on their screen while our puppet was marked dead!
            if ((pkt.Flags & PlayerStateFlags.IsDead) != 0)
            {
                _consecutiveRemoteAlivePacketsWhileDead = 0;
                if (!remoteCtrl.IsDead && !IsRemoteDeathCurrentlyIgnored)
                {
                    ApplyRemoteDeathEvent(remoteId, newPos);
                }
                return;
            }
            else if (pkt.Health > 0)
            {
                if (remoteCtrl.IsDead)
                {
                    _consecutiveRemoteAlivePacketsWhileDead++;
                    if (IsRemoteDeathCurrentlyIgnored || _consecutiveRemoteAlivePacketsWhileDead >= 8)
                    {
                        _consecutiveRemoteAlivePacketsWhileDead = 0;
                        ForceRestoreAliveRemotePuppet(remoteCtrl, pkt.Health, newPos);
                    }
                }
                else
                {
                    _consecutiveRemoteAlivePacketsWhileDead = 0;
                }
            }

            bool isParrying = (pkt.Flags & PlayerStateFlags.Parrying) != 0;
            bool parryStarted = isParrying && !_wasRemoteParrying;
            _wasRemoteParrying = isParrying;

            LevelPlayerController lvlRemote = remoteCtrl as LevelPlayerController;
            if (lvlRemote != null)
            {
                if (lvlRemote.motor != null)
                {
                    bool isGrounded = (pkt.Flags & PlayerStateFlags.Grounded) != 0;
                    bool isLocked = (pkt.Flags & PlayerStateFlags.Locked) != 0;
                    bool isDashing = (pkt.Flags & PlayerStateFlags.Dashing) != 0;
                    int facingX = pkt.MoveX != 0 ? pkt.MoveX : 1;

                    FieldInfo groundedBf = typeof(LevelPlayerMotor).GetField("<Grounded>k__BackingField", AllFlags);
                    FieldInfo lockedBf = typeof(LevelPlayerMotor).GetField("<Locked>k__BackingField", AllFlags);
                    FieldInfo parryingBf = typeof(LevelPlayerMotor).GetField("<Parrying>k__BackingField", AllFlags);
                    FieldInfo lookBf = typeof(LevelPlayerMotor).GetField("<LookDirection>k__BackingField", AllFlags);
                    FieldInfo trueLookBf = typeof(LevelPlayerMotor).GetField("<TrueLookDirection>k__BackingField", AllFlags);
                    FieldInfo moveBf = typeof(LevelPlayerMotor).GetField("<MoveDirection>k__BackingField", AllFlags);
                    FieldInfo dashMgrField = typeof(LevelPlayerMotor).GetField("dashManager", AllFlags);

                    if (groundedBf != null) groundedBf.SetValue(lvlRemote.motor, isGrounded);
                    if (lockedBf != null) lockedBf.SetValue(lvlRemote.motor, isLocked);
                    if (parryingBf != null) parryingBf.SetValue(lvlRemote.motor, isParrying);
                    if (lookBf != null) lookBf.SetValue(lvlRemote.motor, new Trilean2(pkt.LookX, pkt.LookY));
                    if (trueLookBf != null) trueLookBf.SetValue(lvlRemote.motor, new Trilean2(facingX, pkt.LookY));
                    if (moveBf != null) moveBf.SetValue(lvlRemote.motor, new Trilean2(pkt.LookX, pkt.MoveY));

                    if (dashMgrField != null)
                    {
                        object dm = dashMgrField.GetValue(lvlRemote.motor);
                        if (dm != null)
                        {
                            FieldInfo dmState = dm.GetType().GetField("state", AllFlags);
                            FieldInfo dmDir = dm.GetType().GetField("direction", AllFlags);
                            if (dmDir != null) dmDir.SetValue(dm, facingX);
                            if (dmState != null) dmState.SetValue(dm, Enum.ToObject(dmState.FieldType, isDashing ? 2 : 0)); // Dashing=2, Ready=0
                        }
                    }
                }
                if (lvlRemote.weaponManager != null)
                {
                    MethodInfo updateAim = typeof(LevelPlayerWeaponManager).GetMethod("UpdateAim", AllFlags);
                    if (updateAim != null) updateAim.Invoke(lvlRemote.weaponManager, null);
                }
                if (parryStarted && lvlRemote.parryController != null)
                {
                    MethodInfo sp = typeof(LevelPlayerParryController).GetMethod("StartParry", AllFlags);
                    if (sp != null) sp.Invoke(lvlRemote.parryController, null);
                }
            }
            else
            {
                PlanePlayerController planeRemote = remoteCtrl as PlanePlayerController;
                if (planeRemote != null)
                {
                    if (parryStarted && planeRemote.parryController != null)
                    {
                        MethodInfo sp = typeof(PlanePlayerParryController).GetMethod("StartParry", AllFlags);
                        if (sp != null) sp.Invoke(planeRemote.parryController, null);
                    }

                    bool isShrunk = (pkt.Flags & PlayerStateFlags.PlaneShrunk) != 0;
                    if (isShrunk != _wasRemotePlaneShrunk && planeRemote.animationController != null)
                    {
                        _wasRemotePlaneShrunk = isShrunk;
                        FieldInfo shrinkStateField = typeof(PlanePlayerAnimationController).GetField("<ShrinkState>k__BackingField", AllFlags);
                        if (shrinkStateField != null)
                        {
                            shrinkStateField.SetValue(planeRemote.animationController, Enum.ToObject(shrinkStateField.FieldType, isShrunk ? 1 : 0));
                        }
                        if (planeRemote.animator != null)
                        {
                            planeRemote.animator.SetLayerWeight(1, isShrunk ? 1f : 0f);
                            planeRemote.animator.Play(isShrunk ? "Shrink_In" : "Shrink_Out");
                        }
                    }

                    bool isPlaneShooting = (pkt.Flags & PlayerStateFlags.Shooting) != 0;
                    if (planeRemote.weaponManager != null)
                    {
                        EnsurePlaneWeaponsInitialized(planeRemote.weaponManager);

                        if (pkt.CurrentWeaponId != 0)
                        {
                            FieldInfo cwField = typeof(PlanePlayerWeaponManager).GetField("currentWeapon", AllFlags);
                            if (cwField != null)
                            {
                                Weapon cur = (Weapon)cwField.GetValue(planeRemote.weaponManager);
                                if (cur != (Weapon)pkt.CurrentWeaponId)
                                {
                                    MethodInfo swMethod = typeof(PlanePlayerWeaponManager).GetMethod("SwitchWeapon", AllFlags);
                                    if (swMethod != null)
                                        swMethod.Invoke(planeRemote.weaponManager, new object[] { (Weapon)pkt.CurrentWeaponId });
                                    else
                                        cwField.SetValue(planeRemote.weaponManager, (Weapon)pkt.CurrentWeaponId);
                                }
                            }
                        }

                        // Issue #9 (v1.3.2): Self-heal plane shooting state if edge was missed across Retry!
                        if (isPlaneShooting != _wasRemotePlaneShooting || (isPlaneShooting && !planeRemote.weaponManager.IsShooting))
                        {
                            _wasRemotePlaneShooting = isPlaneShooting;
                            planeRemote.weaponManager.IsShooting = isPlaneShooting;
                            MethodInfo m = typeof(PlanePlayerWeaponManager).GetMethod(isPlaneShooting ? "StartBasic" : "EndBasic", AllFlags);
                            if (m != null) m.Invoke(planeRemote.weaponManager, null);
                        }
                    }
                }
            }
        }

        private void HandleWeaponAction(BinaryReader br)
        {
            byte playerIdByte = br.ReadByte();
            WeaponActionType action = (WeaponActionType)br.ReadByte();
            int weaponId = br.ReadInt32();
            float aimX = br.ReadSingle();
            float aimY = br.ReadSingle();

            if (!BothPlayersHaveDlc && IsDlcWeapon(weaponId))
            {
                weaponId = (int)Weapon.level_weapon_peashot;
            }

            PlayerId remoteId = GetRemotePlayerId();
            if (playerIdByte != (byte)remoteId)
                return;

            AbstractPlayerController remoteCtrl = PlayerManager.GetPlayer(remoteId);
            if (remoteCtrl == null)
                return;

            _applyingRemoteWeaponAction = true;
            try
            {
                LevelPlayerController lvlRemote = remoteCtrl as LevelPlayerController;
                if (lvlRemote != null && lvlRemote.weaponManager != null)
                {
                    if (lvlRemote.motor != null)
                    {
                        lvlRemote.motor.ForceLooking(new Trilean2((int)aimX, (int)aimY));
                    }

                    FieldInfo cwField = typeof(LevelPlayerWeaponManager).GetField("currentWeapon", AllFlags);
                    if (cwField != null && weaponId != 0)
                    {
                        Weapon cur = (Weapon)cwField.GetValue(lvlRemote.weaponManager);
                        if (cur != (Weapon)weaponId)
                        {
                            MethodInfo switchMethod = typeof(LevelPlayerWeaponManager).GetMethod("SwitchWeapon", AllFlags);
                            if (switchMethod != null)
                                switchMethod.Invoke(lvlRemote.weaponManager, new object[] { (Weapon)weaponId });
                        }
                    }

                    switch (action)
                    {
                        case WeaponActionType.BasicStart:
                            lvlRemote.weaponManager.IsShooting = true;
                            MethodInfo startBasic = typeof(LevelPlayerWeaponManager).GetMethod("StartBasic", AllFlags);
                            if (startBasic != null) startBasic.Invoke(lvlRemote.weaponManager, null);
                            break;
                        case WeaponActionType.BasicEnd:
                            lvlRemote.weaponManager.IsShooting = false;
                            MethodInfo endBasic = typeof(LevelPlayerWeaponManager).GetMethod("EndBasic", AllFlags);
                            if (endBasic != null) endBasic.Invoke(lvlRemote.weaponManager, null);
                            break;
                        case WeaponActionType.ExStart:
                            MethodInfo startEx = typeof(LevelPlayerWeaponManager).GetMethod("StartEx", AllFlags);
                            if (startEx != null) startEx.Invoke(lvlRemote.weaponManager, null);
                            break;
                        case WeaponActionType.SuperStart:
                            MethodInfo startSuper = typeof(LevelPlayerWeaponManager).GetMethod("StartSuper", AllFlags);
                            if (startSuper != null) startSuper.Invoke(lvlRemote.weaponManager, null);
                            break;
                    }
                    return;
                }

                PlanePlayerController planeRemote = remoteCtrl as PlanePlayerController;
                if (planeRemote != null && planeRemote.weaponManager != null)
                {
                    EnsurePlaneWeaponsInitialized(planeRemote.weaponManager);

                    FieldInfo cwField = typeof(PlanePlayerWeaponManager).GetField("currentWeapon", AllFlags);
                    if (cwField != null && weaponId != 0)
                    {
                        Weapon cur = (Weapon)cwField.GetValue(planeRemote.weaponManager);
                        if (cur != (Weapon)weaponId)
                        {
                            MethodInfo switchMethod = typeof(PlanePlayerWeaponManager).GetMethod("SwitchWeapon", AllFlags);
                            if (switchMethod != null)
                                switchMethod.Invoke(planeRemote.weaponManager, new object[] { (Weapon)weaponId });
                            else
                                cwField.SetValue(planeRemote.weaponManager, (Weapon)weaponId);
                        }
                    }

                    switch (action)
                    {
                        case WeaponActionType.BasicStart:
                            _wasRemotePlaneShooting = true;
                            planeRemote.weaponManager.IsShooting = true;
                            MethodInfo startPlaneBasic = typeof(PlanePlayerWeaponManager).GetMethod("StartBasic", AllFlags);
                            if (startPlaneBasic != null) startPlaneBasic.Invoke(planeRemote.weaponManager, null);
                            break;
                        case WeaponActionType.BasicEnd:
                            _wasRemotePlaneShooting = false;
                            planeRemote.weaponManager.IsShooting = false;
                            MethodInfo endPlaneBasic = typeof(PlanePlayerWeaponManager).GetMethod("EndBasic", AllFlags);
                            if (endPlaneBasic != null) endPlaneBasic.Invoke(planeRemote.weaponManager, null);
                            break;
                        case WeaponActionType.ExStart:
                            MethodInfo startPlaneEx = typeof(PlanePlayerWeaponManager).GetMethod("StartEx", AllFlags);
                            if (startPlaneEx != null) startPlaneEx.Invoke(planeRemote.weaponManager, null);
                            break;
                        case WeaponActionType.SuperStart:
                            MethodInfo startPlaneSuper = typeof(PlanePlayerWeaponManager).GetMethod("StartSuper", AllFlags);
                            if (startPlaneSuper != null) startPlaneSuper.Invoke(planeRemote.weaponManager, null);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("HandleWeaponAction warning: " + ex.Message);
            }
            finally
            {
                _applyingRemoteWeaponAction = false;
            }
        }

        private void HandleLevelEvent(BinaryReader br)
        {
            LevelEventType evtType = (LevelEventType)br.ReadByte();
            byte playerIdByte = br.ReadByte();
            float valA = br.ReadSingle();
            float valB = br.ReadSingle();
            int intVal = br.ReadInt32();

            switch (evtType)
            {
                case LevelEventType.BossDamage:
                    if (_transport.Role == CoopRole.Client && Level.Current != null)
                    {
                        SyncClientBossPhaseIndex(intVal, valB);
                        if (valB >= 0f)
                        {
                            _lastHostBossCurrentHealth = valB;
                            if (EnsureBossProperties())
                            {
                                float localHp = GetBossPropertiesHealth();
                                // Keep 1 HP Knockout Gate until LevelEventType.LevelWin arrives
                                float targetHp = _hostConfirmedLevelWin ? valB : Mathf.Max(1f, valB);
                                if (localHp > targetHp + 0.5f)
                                {
                                    try { _bossDealDamageMethod.Invoke(_cachedBossProps, new object[] { localHp - targetHp }); } catch { }
                                }
                            }
                        }

                        if (Level.Current.timeline != null)
                        {
                            float maxSafeDmg = _hostConfirmedLevelWin
                                ? Level.Current.timeline.health
                                : Mathf.Max(0f, Level.Current.timeline.health - 1f);
                            float targetDmg = Mathf.Min(valA, maxSafeDmg);
                            float delta = targetDmg - Level.Current.timeline.damage;
                            if (delta > 0f)
                                Level.Current.timeline.DealDamage(delta);
                        }
                    }
                    break;

                case LevelEventType.BossPhaseChange:
                    if (_transport.Role == CoopRole.Client)
                    {
                        _currentRngSeed = intVal;
                        SynchronizeBossPropertiesRng(_currentRngSeed);
                        SyncClientBossPhaseIndex(playerIdByte, valB);
                        if (Level.Current != null && Level.Current.timeline != null)
                        {
                            float delta = valA - Level.Current.timeline.damage;
                            if (delta > 0f)
                                Level.Current.timeline.DealDamage(delta);
                        }
                        CupheadCoopBootstrapper.Log("[CoopDiag] BossPhase: Client synced to phase stateIndex=" + playerIdByte + " (hp=" + valB + ", seed=" + intVal + ")");
                    }
                    break;

                case LevelEventType.SpawnerTriggerSync:
                    if (_transport.Role == CoopRole.Client)
                    {
                        ApplyRemoteSpawnerTriggerSync(intVal, valA < 1.5f);
                    }
                    break;

                case LevelEventType.LevelWin:
                    if (_transport.Role == CoopRole.Client)
                    {
                        ForceClientLevelWinKnockout();
                    }
                    break;

                case LevelEventType.LevelLose:
                    if (_transport.Role == CoopRole.Client)
                    {
                        ForceClientMausoleumLose();
                    }
                    break;

                case LevelEventType.MausoleumGhostParried:
                    ApplyRemoteMausoleumGhostParried(new Vector2(valA, valB), intVal != 0);
                    break;

                case LevelEventType.LevelReady:
                    _remoteReadyForLevelId = intVal;
                    if (_transport.Role == CoopRole.Client && (int)valB != 0)
                    {
                        _currentRngSeed = (int)valB;
                        SynchronizeBossPropertiesRng(_currentRngSeed);
                    }
                    if (_waitingAtLevelReadyGate && _levelReadyGateLevelId == intVal)
                    {
                        CupheadCoopBootstrapper.Log("LevelReadyGate released via remote LevelReady event for level " + (Levels)intVal);
                        ReleaseLevelReadyGate();
                    }
                    else if (Level.Current != null && (int)Level.Current.CurrentLevel == intVal && _transport.IsConnected)
                    {
                        // Echo LevelReady back so the partner never gets stuck waiting if our initial packet arrived before their gate opened
                        byte[] echoReady = CoopProtocol.BuildLevelEvent(
                            LevelEventType.LevelReady,
                            (byte)GetLocalPlayerId(),
                            _sceneLoadSequence,
                            _transport.Role == CoopRole.Host ? (float)_currentRngSeed : 0f,
                            intVal);
                        _transport.Send(echoReady, true);
                    }
                    break;

                case LevelEventType.PauseSync:
                    CupheadCoopBootstrapper.Log("[CoopDiag] PauseSync: Partner toggled pause=" + (intVal != 0));
                    ApplyRemotePauseSync(intVal != 0);
                    break;

                case LevelEventType.PlayerDeath:
                    CupheadCoopBootstrapper.Log("[CoopDiag] PlayerDeath: Partner (" + (PlayerId)playerIdByte + ") died at (" + valA.ToString("F1") + "," + valB.ToString("F1") + ")");
                    ApplyRemoteDeathEvent((PlayerId)playerIdByte, new Vector3(valA, valB, 0f));
                    break;

                case LevelEventType.PlayerRevive:
                case LevelEventType.PlayerStealHealthRevive:
                    CupheadCoopBootstrapper.Log("[CoopDiag] PlayerRevive: (" + evtType + ") for " + (PlayerId)playerIdByte + " at (" + valA.ToString("F1") + "," + valB.ToString("F1") + ")");
                    ApplyRemoteReviveEvent(evtType, (PlayerId)playerIdByte, new Vector3(valA, valB, 0f));
                    break;

                case LevelEventType.LevelReload:
                    if (_transport.Role == CoopRole.Client)
                    {
                        CupheadCoopBootstrapper.Log("[CoopDiag] LevelOutcome: Client applying LevelReload (Retry) for level=" + (Levels)(int)valA + " (seed=" + intVal + ")");
                        _remoteReadyForLevelId = -1;
                        SetIgnoreRemoteDeathWindow(2.5f);
                        _currentRngSeed = intVal;
                        SynchronizeBossPropertiesRng(_currentRngSeed);

                        if (_clientInitiatedReload && (Time.unscaledTime - _clientInitiatedReloadTime) < 8.0f)
                        {
                            // Client already initiated this reload locally; just adopt the Host's RNG seed!
                            _clientInitiatedReload = false;
                            break;
                        }

                        _wasSceneLoaderLoading = true;
                        _ignoreClientSceneRequestUntilTime = Time.unscaledTime + 3.5f;
                        _applyingRemoteSceneLoad = true;
                        try
                        {
                            Level.SetCurrentMode((Level.Mode)(int)valB);
                            if (Level.IsDicePalace || Level.IsDicePalaceMain)
                            {
                                DicePalaceMainLevelGameInfo.CleanUpRetry();
                            }
                            SceneLoader.ReloadLevel();
                        }
                        finally
                        {
                            _applyingRemoteSceneLoad = false;
                        }
                    }
                    break;

                case LevelEventType.KingDiceBoardSync:
                    if (_transport.Role == CoopRole.Client)
                    {
                        DicePalaceMainLevelGameInfo.PLAYER_SPACES_MOVED = (int)valA;
                        DicePalaceMainLevelGameInfo.TURN_COUNTER = (int)valB;
                        if (DicePalaceMainLevelGameInfo.HEART_INDEXES != null && DicePalaceMainLevelGameInfo.HEART_INDEXES.Length >= 3)
                        {
                            DicePalaceMainLevelGameInfo.HEART_INDEXES[0] = intVal & 0xFF;
                            DicePalaceMainLevelGameInfo.HEART_INDEXES[1] = (intVal >> 8) & 0xFF;
                            DicePalaceMainLevelGameInfo.HEART_INDEXES[2] = (intVal >> 16) & 0xFF;
                        }
                    }
                    break;

                case LevelEventType.KingDiceRoll:
                    ApplyRemoteKingDiceRoll(intVal);
                    break;

                case LevelEventType.CoinCollected:
                    ApplyRemoteCoinCollected(intVal);
                    break;

                case LevelEventType.DjimmiActivated:
                    if (PlayerData.Initialized && PlayerData.Data != null && !PlayerData.Data.DjimmiActivatedCurrentRegion())
                    {
                        _lastDjimmiState = true;
                        PlayerData.Data.TryActivateDjimmi();
                    }
                    break;
            }
        }

        private void ApplyRemoteDeathEvent(PlayerId deadPlayerId, Vector3 pos)
        {
            if (deadPlayerId != GetRemotePlayerId() || IsRemoteDeathCurrentlyIgnored)
                return;

            _applyingRemoteDeath = true;
            try
            {
                AbstractPlayerController p = PlayerManager.GetPlayer(deadPlayerId);
                if (p == null || p.IsDead)
                    return;

                if (pos != Vector3.zero)
                {
                    p.transform.position = new Vector3(pos.x, pos.y, p.transform.position.z);
                }

                if (p.stats != null)
                {
                    p.stats.SetHealth(0);
                    MethodInfo onStatsDeath = typeof(PlayerStatsManager).GetMethod("OnStatsDeath", AllFlags);
                    if (onStatsDeath != null)
                    {
                        onStatsDeath.Invoke(p.stats, null);
                    }
                }
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ApplyRemoteDeathEvent warning: " + ex.Message);
            }
            finally
            {
                _applyingRemoteDeath = false;
            }
        }

        private void ForceRestoreAliveRemotePuppet(AbstractPlayerController remoteCtrl, int health, Vector3 pos)
        {
            if (remoteCtrl == null)
                return;

            try
            {
                // Destroy any lingering death effect ghost for this player
                PlayerDeathEffect[] effects = UnityEngine.Object.FindObjectsOfType<PlayerDeathEffect>();
                if (effects != null)
                {
                    FieldInfo pidField = typeof(PlayerDeathEffect).GetField("playerId", AllFlags);
                    for (int i = 0; i < effects.Length; i++)
                    {
                        if (effects[i] != null && pidField != null && (PlayerId)pidField.GetValue(effects[i]) == remoteCtrl.id)
                        {
                            UnityEngine.Object.Destroy(effects[i].gameObject);
                        }
                    }
                }

                if (remoteCtrl.stats != null)
                {
                    remoteCtrl.stats.SetHealth(Mathf.Max(1, health));
                }

                FieldInfo isDeadBf = typeof(AbstractPlayerController).GetField("<IsDead>k__BackingField", AllFlags);
                if (isDeadBf != null)
                {
                    isDeadBf.SetValue(remoteCtrl, false);
                }

                if (!remoteCtrl.gameObject.activeSelf)
                {
                    remoteCtrl.gameObject.SetActive(true);
                }

                MethodInfo onRevive = typeof(AbstractPlayerController).GetMethod("OnRevive", AllFlags);
                if (onRevive != null)
                {
                    onRevive.Invoke(remoteCtrl, new object[] { pos });
                }

                ConfigureLocalAndRemoteInputs();
                CupheadCoopBootstrapper.Log("Self-healed desynced dead state on remote puppet (" + remoteCtrl.id + ").");
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("ForceRestoreAliveRemotePuppet warning: " + ex.Message);
            }
        }

        private void ApplyRemoteReviveEvent(LevelEventType evtType, PlayerId revivedId, Vector3 pos)
        {
            try
            {
                PlayerDeathEffect[] effects = UnityEngine.Object.FindObjectsOfType<PlayerDeathEffect>();
                if (effects != null)
                {
                    FieldInfo pidField = typeof(PlayerDeathEffect).GetField("playerId", AllFlags);
                    FieldInfo exitingField = typeof(PlayerDeathEffect).GetField("exiting", AllFlags);
                    for (int i = 0; i < effects.Length; i++)
                    {
                        PlayerDeathEffect eff = effects[i];
                        if (eff == null || pidField == null) continue;
                        PlayerId pid = (PlayerId)pidField.GetValue(eff);
                        if (pid != revivedId) continue;

                        _processedDeathEffects.Add(eff.GetInstanceID());
                        bool alreadyExiting = exitingField != null && (bool)exitingField.GetValue(eff);
                        if (!alreadyExiting)
                        {
                            string methodName = (evtType == LevelEventType.PlayerStealHealthRevive)
                                ? "ReviveOutOfFrame"
                                : "OnParrySwitch";
                            MethodInfo m = typeof(PlayerDeathEffect).GetMethod(methodName, AllFlags);
                            if (m != null)
                            {
                                m.Invoke(eff, null);
                                return;
                            }
                        }
                    }
                }

                AbstractPlayerController p = PlayerManager.GetPlayer(revivedId);
                if (p != null && p.IsDead)
                {
                    if (evtType == LevelEventType.PlayerStealHealthRevive)
                    {
                        PlayerId partnerId = (revivedId == PlayerId.PlayerOne) ? PlayerId.PlayerTwo : PlayerId.PlayerOne;
                        AbstractPlayerController partner = PlayerManager.GetPlayer(partnerId);
                        if (partner != null && partner.stats != null && partner.stats.PartnerCanSteal)
                        {
                            partner.stats.OnPartnerStealHealth();
                        }
                    }
                    MethodInfo onRevive = typeof(AbstractPlayerController).GetMethod("OnRevive", AllFlags);
                    if (onRevive != null)
                        onRevive.Invoke(p, new object[] { pos });
                }
            }
            catch { }
        }

        private void ApplyRemoteKingDiceRoll(int rolledVal)
        {
            if (Level.Current == null || !Level.IsDicePalaceMain)
                return;

            try
            {
                DicePalaceMainLevel diceLvl = Level.Current as DicePalaceMainLevel;
                if (diceLvl == null) return;

                FieldInfo gmField = typeof(DicePalaceMainLevel).GetField("gameManager", AllFlags);
                if (gmField == null) return;

                DicePalaceMainLevelGameManager gm = gmField.GetValue(diceLvl) as DicePalaceMainLevelGameManager;
                if (gm == null) return;

                FieldInfo diceField = typeof(DicePalaceMainLevelGameManager).GetField("dice", AllFlags);
                if (diceField == null) return;

                DicePalaceMainLevelDice dice = diceField.GetValue(gm) as DicePalaceMainLevelDice;
                if (dice == null) return;

                _lastSyncedDiceInstanceId = dice.GetInstanceID();
                dice.roll = (DicePalaceMainLevelDice.Roll)Mathf.Clamp(rolledVal, 0, 2);
                dice.waitingToRoll = false;
            }
            catch { }
        }

        private void ApplyRemoteCoinCollected(int coinGlobalIdHash)
        {
            if (Level.Current == null)
                return;

            try
            {
                _syncedCoinHashes.Add(coinGlobalIdHash);
                LevelCoin[] coins = UnityEngine.Object.FindObjectsOfType<LevelCoin>();
                if (coins == null) return;

                FieldInfo collectedField = typeof(LevelCoin).GetField("_collected", AllFlags);
                MethodInfo collectMethod = typeof(LevelCoin).GetMethod("Collect", AllFlags);
                if (collectedField == null || collectMethod == null) return;

                for (int i = 0; i < coins.Length; i++)
                {
                    LevelCoin coin = coins[i];
                    if (coin == null) continue;
                    if (GetStableStringHash(coin.GlobalID) == coinGlobalIdHash)
                    {
                        bool already = (bool)collectedField.GetValue(coin);
                        if (!already)
                        {
                            collectMethod.Invoke(coin, new object[] { GetRemotePlayerId() });
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        private void HandleSceneSync(BinaryReader br)
        {
            string sceneName = br.ReadString();
            int levelId = br.ReadInt32();
            int diffMode = br.ReadInt32();
            int rngSeed = br.ReadInt32();
            int loadSeq = 0;
            if (br.BaseStream.Position + 4 <= br.BaseStream.Length)
            {
                loadSeq = br.ReadInt32();
            }

            if (IsDlcSceneOrLevel(sceneName, levelId) && !BothPlayersHaveDlc)
            {
                _lastDlcWarning = "BLOQUEADO: O Host tentou carregar uma fase da DLC (" + sceneName + "), mas voce nao possui a DLC! Carregamento bloqueado para evitar tela infinita.";
                CupheadCoopBootstrapper.Log(_lastDlcWarning);
                return;
            }

            _currentRngSeed = rngSeed;
            SynchronizeBossPropertiesRng(_currentRngSeed);

            if (_transport.Role == CoopRole.Client && !string.IsNullOrEmpty(sceneName))
            {
                if (sceneName == "scene_slot_select" || sceneName == "scene_title" || sceneName == "scene_start")
                    return;

                string activeScene = SceneManager.GetActiveScene().name;
                bool isNewLoad = (activeScene != sceneName)
                    || ((loadSeq != 0) ? (loadSeq != _lastReceivedSceneLoadSeq) : (sceneName != _lastSyncedScene));
                if (isNewLoad)
                {
                    _lastReceivedSceneLoadSeq = loadSeq;
                    _lastSyncedScene = sceneName;
                    _remoteReadyForLevelId = -1;

                    // If the Client itself requested this exact scene transition recently, don't reload it a second time!
                    if (activeScene == sceneName && _lastClientRequestedScene == sceneName && (Time.unscaledTime - _lastClientRequestTime) < 12.0f)
                    {
                        _lastClientRequestedScene = string.Empty;
                        SynchronizeBossPropertiesRng(_currentRngSeed);
                        CupheadCoopBootstrapper.Log("Client adopted Host RNG seed for Client-requested scene: " + sceneName);
                        return;
                    }

                    if (Map.Current != null)
                    {
                        SyncMapReturnPositionsFromActivePlayers();
                    }

                    _wasSceneLoaderLoading = true;
                    _ignoreClientSceneRequestUntilTime = Time.unscaledTime + 3.5f;
                    _applyingRemoteSceneLoad = true;
                    try
                    {
                        Level.SetCurrentMode((Level.Mode)diffMode);
                        SceneLoader.SetCurrentLevel((Levels)levelId);
                        if (activeScene == sceneName && Level.Current != null)
                        {
                            SceneLoader.ReloadLevel();
                        }
                        else
                        {
                            SceneLoader.LoadScene(
                                sceneName,
                                SceneLoader.Transition.Iris,
                                SceneLoader.Transition.Iris,
                                SceneLoader.Icon.Hourglass,
                                null);
                        }
                        CupheadCoopBootstrapper.Log("Client syncing to Host scene: " + sceneName + " (seq=" + loadSeq + ", seed=" + rngSeed + ")");
                    }
                    finally
                    {
                        _applyingRemoteSceneLoad = false;
                    }
                }
            }
        }

        private void HandleAchievementSync(BinaryReader br)
        {
            AchievementSyncKind kind = (AchievementSyncKind)br.ReadByte();
            string id = br.ReadString();
            int intVal = br.ReadInt32();
            float floatVal = br.ReadSingle();

            EnsureOnlineWrapper();
            if (_onlineWrapper != null)
            {
                _onlineWrapper.ApplyRemoteAchievementPacket(kind, id, intVal, floatVal);
            }
        }

        private void HandleSaveProgressSync(BinaryReader br)
        {
            int levelId = br.ReadInt32();
            bool completed = br.ReadBoolean();
            int grade = br.ReadInt32();
            int difficultyBeaten = br.ReadInt32();
            float bestTime = br.ReadSingle();
            bool chaliceP1 = br.ReadBoolean();
            bool chaliceP2 = br.ReadBoolean();
            bool curseP1 = br.ReadBoolean();
            bool curseP2 = br.ReadBoolean();
            bool hardModeBase = br.ReadBoolean();
            bool hardModeDlc = br.ReadBoolean();
            int coinsP1 = br.ReadInt32();
            int coinsP2 = br.ReadInt32();

            if (!PlayerData.Initialized || PlayerData.Data == null)
                return;

            PlayerData.PlayerLevelDataObject lvlObj = PlayerData.Data.GetLevelData((Levels)levelId);
            if (lvlObj != null)
            {
                lvlObj.played = true;
                if (completed) lvlObj.completed = true;
                if (grade > (int)lvlObj.grade) lvlObj.grade = (LevelScoringData.Grade)grade;
                if (difficultyBeaten > (int)lvlObj.difficultyBeaten) lvlObj.difficultyBeaten = (Level.Mode)difficultyBeaten;
                if (bestTime < lvlObj.bestTime || lvlObj.bestTime <= 0f) lvlObj.bestTime = bestTime;
                if (chaliceP1 || chaliceP2)
                {
                    lvlObj.completedAsChaliceP1 = true;
                    lvlObj.completedAsChaliceP2 = true;
                }
                if (curseP1 || curseP2)
                {
                    lvlObj.curseCharmP1 = true;
                    lvlObj.curseCharmP2 = true;
                }
            }

            if (hardModeBase) PlayerData.Data.IsHardModeAvailable = true;
            if (hardModeDlc) PlayerData.Data.IsHardModeAvailableDLC = true;

            int authoritativeCoins = Math.Max(0, coinsP1);
            SetPlayerCurrencyExact(PlayerId.PlayerOne, authoritativeCoins);
            SetPlayerCurrencyExact(PlayerId.PlayerTwo, authoritativeCoins);

            PlayerData.SaveCurrentFile();
            if (_onlineWrapper != null)
            {
                _onlineWrapper.AuditCoopPostWinAchievements();
            }

            CupheadCoopBootstrapper.Log("Synced Save Progress & Achievements for level " + (Levels)levelId);
        }
    }
}
