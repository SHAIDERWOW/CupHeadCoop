using System;
using System.Collections.Generic;
using UnityEngine;

namespace CupheadCoop
{
    /// <summary>
    /// Decorator around Cuphead's `OnlineInterfaceSteam` (`OnlineManager.Instance.Interface`).
    /// Guarantees that every Steam achievement and stat unlocked during a co-op session is
    /// unlocked locally on Steam AND broadcast reliably to the co-op partner so both players
    /// earn achievements natively on Steam.
    /// </summary>
    public class CoopOnlineInterface : OnlineInterface
    {
        private readonly OnlineInterface _inner;
        private readonly CoopNetworkTransport _transport;
        private bool _applyingRemoteSync;

        public int AchievementsSyncedCount { get; private set; }
        public string LastUnlockedAchievement { get; private set; }
        public static string OverrideSaveDirectory { get; set; }
        public static bool TestModeNoSteamWrite { get; set; }
        public static bool SuppressClientDiskSave { get; set; }
        private readonly List<string> _unlockedHistory = new List<string>();

        public IList<string> UnlockedHistory
        {
            get { return _unlockedHistory; }
        }

        public OnlineInterface InnerInterface
        {
            get { return _inner; }
        }

        public CoopOnlineInterface(OnlineInterface inner, CoopNetworkTransport transport)
        {
            _inner = inner;
            _transport = transport;
            LastUnlockedAchievement = "None";
        }

        public static CoopOnlineInterface Install(CoopNetworkTransport transport)
        {
            if (OnlineManager.Instance == null || OnlineManager.Instance.Interface == null)
                return null;

            CoopOnlineInterface existing = OnlineManager.Instance.Interface as CoopOnlineInterface;
            if (existing != null)
                return existing;

            CoopOnlineInterface wrapper = new CoopOnlineInterface(OnlineManager.Instance.Interface, transport);
            System.Reflection.FieldInfo backing = typeof(OnlineManager).GetField("<Interface>k__BackingField",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (backing != null)
            {
                backing.SetValue(OnlineManager.Instance, wrapper);
            }
            CupheadCoopBootstrapper.Log("Installed CoopOnlineInterface wrapper over " + wrapper.InnerInterface.GetType().FullName);
            return wrapper;
        }

        public event SignInEventHandler OnUserSignedIn
        {
            add { _inner.OnUserSignedIn += value; }
            remove { _inner.OnUserSignedIn -= value; }
        }

        public event SignOutEventHandler OnUserSignedOut
        {
            add { _inner.OnUserSignedOut += value; }
            remove { _inner.OnUserSignedOut -= value; }
        }

        public OnlineUser MainUser { get { return _inner.MainUser; } }
        public OnlineUser SecondaryUser { get { return _inner.SecondaryUser; } }
        public bool CloudStorageInitialized { get { return _inner.CloudStorageInitialized; } }
        public bool SupportsMultipleUsers { get { return _inner.SupportsMultipleUsers; } }
        public bool SupportsUserSignIn { get { return _inner.SupportsUserSignIn; } }

        public void Init() { _inner.Init(); }
        public void Reset() { _inner.Reset(); }
        public void SignInUser(bool silent, PlayerId player, ulong controllerId) { _inner.SignInUser(silent, player, controllerId); }
        public void SwitchUser(PlayerId player, ulong controllerId) { _inner.SwitchUser(player, controllerId); }
        public OnlineUser GetUserForController(ulong id) { return _inner.GetUserForController(id); }
        public List<ulong> GetControllersForUser(PlayerId player) { return _inner.GetControllersForUser(player); }
        public bool IsUserSignedIn(PlayerId player) { return _inner.IsUserSignedIn(player); }
        public OnlineUser GetUser(PlayerId player) { return _inner.GetUser(player); }
        public void SetUser(PlayerId player, OnlineUser user) { _inner.SetUser(player, user); }
        public Texture2D GetProfilePic(PlayerId player) { return _inner.GetProfilePic(player); }
        public void GetAchievement(PlayerId player, string id, AchievementEventHandler achievementRetrievedHandler)
        {
            _inner.GetAchievement(player, id, achievementRetrievedHandler);
        }

        public void UnlockAchievement(PlayerId player, string id)
        {
            if (string.IsNullOrEmpty(id))
                return;

            if (!TestModeNoSteamWrite)
            {
                // Unlock locally on Steam (OnlineInterfaceSteam) and LocalAchievementsManager
                _inner.UnlockAchievement(PlayerId.PlayerOne, id);
                try
                {
                    LocalAchievementsManager.UnlockAchievement(PlayerId.PlayerOne, id);
                }
                catch (Exception ex)
                {
                    CupheadCoopBootstrapper.Log("LocalAchievementsManager.UnlockAchievement warning: " + ex.Message);
                }
            }

            if (!_unlockedHistory.Contains(id))
                _unlockedHistory.Add(id);

            LastUnlockedAchievement = id;
            AchievementsSyncedCount++;
            CupheadCoopBootstrapper.Log("Achievement Unlocked locally: " + id + " (from " + player + ", testMode=" + TestModeNoSteamWrite + ")");

            if (!_applyingRemoteSync && _transport != null && _transport.IsConnected)
            {
                byte[] pkt = CoopProtocol.BuildAchievementSync(AchievementSyncKind.UnlockAchievement, id, 0, 0f);
                _transport.Send(pkt, true);
            }
        }

        public void SyncAchievementsAndStats()
        {
            _inner.SyncAchievementsAndStats();
            if (!_applyingRemoteSync && _transport != null && _transport.IsConnected)
            {
                byte[] pkt = CoopProtocol.BuildAchievementSync(AchievementSyncKind.SyncStats, string.Empty, 0, 0f);
                _transport.Send(pkt, true);
            }
        }

        public void SetStat(PlayerId player, string id, int value)
        {
            _inner.SetStat(PlayerId.PlayerOne, id, value);
            if (!_applyingRemoteSync && _transport != null && _transport.IsConnected)
            {
                byte[] pkt = CoopProtocol.BuildAchievementSync(AchievementSyncKind.SetStatInt, id, value, 0f);
                _transport.Send(pkt, true);
            }
        }

        public void SetStat(PlayerId player, string id, float value)
        {
            _inner.SetStat(PlayerId.PlayerOne, id, value);
            if (!_applyingRemoteSync && _transport != null && _transport.IsConnected)
            {
                byte[] pkt = CoopProtocol.BuildAchievementSync(AchievementSyncKind.SetStatFloat, id, 0, value);
                _transport.Send(pkt, true);
            }
        }

        public void SetStat(PlayerId player, string id, string value)
        {
            _inner.SetStat(PlayerId.PlayerOne, id, value);
        }

        public void IncrementStat(PlayerId player, string id, int value)
        {
            _inner.IncrementStat(PlayerId.PlayerOne, id, value);
            try
            {
                LocalAchievementsManager.IncrementStat(PlayerId.PlayerOne, id, value);
            }
            catch { }

            if (!_applyingRemoteSync && _transport != null && _transport.IsConnected)
            {
                byte[] pkt = CoopProtocol.BuildAchievementSync(AchievementSyncKind.IncrementStatInt, id, value, 0f);
                _transport.Send(pkt, true);
            }
        }

        public void ApplyRemoteAchievementPacket(AchievementSyncKind kind, string id, int intVal, float floatVal)
        {
            _applyingRemoteSync = true;
            try
            {
                switch (kind)
                {
                    case AchievementSyncKind.UnlockAchievement:
                        UnlockAchievement(PlayerId.PlayerOne, id);
                        break;
                    case AchievementSyncKind.SetStatInt:
                        SetStat(PlayerId.PlayerOne, id, intVal);
                        break;
                    case AchievementSyncKind.SetStatFloat:
                        SetStat(PlayerId.PlayerOne, id, floatVal);
                        break;
                    case AchievementSyncKind.IncrementStatInt:
                        IncrementStat(PlayerId.PlayerOne, id, intVal);
                        break;
                    case AchievementSyncKind.SyncStats:
                        SyncAchievementsAndStats();
                        break;
                }
            }
            finally
            {
                _applyingRemoteSync = false;
            }
        }

        public void SetRichPresence(PlayerId player, string id, bool active) { _inner.SetRichPresence(player, id, active); }
        public void SetRichPresenceActive(PlayerId player, bool active) { _inner.SetRichPresenceActive(player, active); }
        public void InitializeCloudStorage(PlayerId player, InitializeCloudStoreHandler handler) { _inner.InitializeCloudStorage(player, handler); }
        public void UninitializeCloudStorage() { _inner.UninitializeCloudStorage(); }

        public void SaveCloudData(IDictionary<string, string> data, SaveCloudDataHandler handler)
        {
            if (SuppressClientDiskSave && string.IsNullOrEmpty(OverrideSaveDirectory))
            {
                // Protect the Client's personal local save files on disk while mirroring the Host's World Map in RAM.
                if (handler != null) handler(true);
                return;
            }
            if (!string.IsNullOrEmpty(OverrideSaveDirectory))
            {
                try
                {
                    if (!System.IO.Directory.Exists(OverrideSaveDirectory))
                        System.IO.Directory.CreateDirectory(OverrideSaveDirectory);
                    foreach (KeyValuePair<string, string> kv in data)
                    {
                        string filePath = System.IO.Path.Combine(OverrideSaveDirectory, kv.Key + ".sav");
                        System.IO.File.WriteAllText(filePath, kv.Value ?? string.Empty);
                    }
                    if (handler != null) handler(true);
                }
                catch (Exception ex)
                {
                    CupheadCoopBootstrapper.Log("Isolated SaveCloudData error: " + ex.Message);
                    if (handler != null) handler(false);
                }
                return;
            }
            _inner.SaveCloudData(data, handler);
        }

        public void LoadCloudData(string[] keys, LoadCloudDataHandler handler)
        {
            if (!string.IsNullOrEmpty(OverrideSaveDirectory))
            {
                try
                {
                    if (!System.IO.Directory.Exists(OverrideSaveDirectory))
                        System.IO.Directory.CreateDirectory(OverrideSaveDirectory);
                    string[] results = new string[keys.Length];
                    for (int i = 0; i < keys.Length; i++)
                    {
                        string filePath = System.IO.Path.Combine(OverrideSaveDirectory, keys[i] + ".sav");
                        if (System.IO.File.Exists(filePath))
                            results[i] = System.IO.File.ReadAllText(filePath);
                        else
                            results[i] = string.Empty;
                    }
                    if (handler != null) handler(results, CloudLoadResult.Success);
                }
                catch (Exception ex)
                {
                    CupheadCoopBootstrapper.Log("Isolated LoadCloudData error: " + ex.Message);
                    if (handler != null) handler(new string[keys.Length], CloudLoadResult.Failed);
                }
                return;
            }
            _inner.LoadCloudData(keys, handler);
        }

        public void UpdateControllerMapping() { _inner.UpdateControllerMapping(); }
        public bool ControllerMappingChanged() { return _inner.ControllerMappingChanged(); }

        /// <summary>
        /// Audits post-win conditions where vanilla Cuphead only checked PlayerOne (e.g., Ms. Chalice boss kills,
        /// Paladin / Divine Relic, Chalice Super Win) so that if PlayerTwo fulfilled the requirement in Co-op,
        /// the achievement still unlocks for both players.
        /// </summary>
        public void AuditCoopPostWinAchievements()
        {
            try
            {
                if (Level.Current == null || !PlayerData.Initialized || PlayerData.Data == null)
                    return;

                Levels currentLevel = Level.Current.CurrentLevel;
                bool isChaliceBossLevel = Array.IndexOf(Level.chaliceLevels, currentLevel) >= 0;

                if (isChaliceBossLevel && PlayerManager.playerWasChalice != null && PlayerManager.playerWasChalice.Length > 1 && PlayerManager.playerWasChalice[1])
                {
                    UnlockAchievement(PlayerId.PlayerTwo, OnlineAchievementData.DLC.DefeatBossAsChalice);

                    int chaliceP2Count = PlayerData.Data.CountLevelsChaliceCompleted(Level.chaliceLevels, PlayerId.PlayerTwo);
                    int chaliceP1Count = PlayerData.Data.CountLevelsChaliceCompleted(Level.chaliceLevels, PlayerId.PlayerOne);
                    if (Math.Max(chaliceP1Count, chaliceP2Count) >= OnlineAchievementData.DLC.Triggers.DefeatXBossesAsChaliceTrigger)
                    {
                        UnlockAchievement(PlayerId.PlayerTwo, OnlineAchievementData.DLC.DefeatXBossesAsChalice);
                    }
                }

                SyncAchievementsAndStats();
            }
            catch (Exception ex)
            {
                CupheadCoopBootstrapper.Log("AuditCoopPostWinAchievements warning: " + ex.Message);
            }
        }
    }
}
