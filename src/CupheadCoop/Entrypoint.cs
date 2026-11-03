using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Doorstop
{
    /// <summary>
    /// Unity Doorstop 4.x entrypoint: `static void Doorstop.Entrypoint.Start()`
    /// Automatically invoked by winhttp.dll right after mono_jit_init_version.
    /// IMPORTANT: Start() and everything it calls synchronously MUST NOT reference
    /// UnityEngine types directly, because Mono JIT-compiles the called method before
    /// executing it, and UnityEngine.dll is not loaded until right after Doorstop.Entrypoint.Start().
    /// </summary>
    public static class Entrypoint
    {
        private static int _initialized;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _initialized, 1) != 0)
                return;

            CupheadCoop.CupheadCoopBootstrapper.InitializeEarly();
        }
    }
}

namespace CupheadCoop
{
    public static class CupheadCoopBootstrapper
    {
        private static bool _sceneHooked;
        private static string _logPath = "CupheadCoop.log";
        private static string _lastUnityErrorMsg = string.Empty;
        private static int _unityErrorsLoggedInCurrentScene;
        private const int MaxUnityErrorsPerScene = 40;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void InitializeEarly()
        {
            try
            {
                _logPath = Path.GetFullPath("CupheadCoop.log");
                Log("CupheadCoopBootstrapper.InitializeEarly() invoked by Doorstop (v" + CoopProtocol.ModVersion + ")!");

                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Assembly-CSharp")
                    {
                        AttachUnityHooks();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("InitializeEarly error: " + ex);
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            try
            {
                Log("[UNHANDLED EXCEPTION] " + (args != null && args.ExceptionObject != null ? args.ExceptionObject.ToString() : "Unknown"));
            }
            catch { }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                if (args.LoadedAssembly != null)
                {
                    string asmName = args.LoadedAssembly.GetName().Name;
                    if (asmName == "Assembly-CSharp")
                    {
                        Log("Assembly-CSharp loaded into AppDomain; attaching Unity hooks.");
                        AttachUnityHooks();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("OnAssemblyLoad error: " + ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AttachUnityHooks()
        {
            if (_sceneHooked)
                return;
            _sceneHooked = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Application.logMessageReceived += OnUnityLogMessageReceived;
            Log("Hooked SceneManager.sceneLoaded and Application.logMessageReceived successfully.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OnUnityLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            try
            {
                if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert)
                    return;

                if (_unityErrorsLoggedInCurrentScene >= MaxUnityErrorsPerScene)
                    return;

                string safeCond = condition ?? string.Empty;
                if (safeCond == _lastUnityErrorMsg)
                    return;

                _lastUnityErrorMsg = safeCond;
                _unityErrorsLoggedInCurrentScene++;

                string sceneName = "unknown";
                try { sceneName = SceneManager.GetActiveScene().name; } catch { }

                string roleStr = "None";
                uint tick = 0;
                if (CoopModManager.Instance != null)
                {
                    if (CoopModManager.Instance.Transport != null)
                        roleStr = CoopModManager.Instance.Transport.Role.ToString();
                    if (CoopModManager.Instance.Sync != null)
                        tick = CoopModManager.Instance.Sync.LocalTick;
                }

                Log(string.Format(
                    "[UNITY {0}] [Scene={1} Role={2} Tick={3}] {4}{5}StackTrace: {6}",
                    type,
                    sceneName,
                    roleStr,
                    tick,
                    safeCond,
                    Environment.NewLine,
                    (stackTrace ?? string.Empty).Trim()));
            }
            catch { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                _unityErrorsLoggedInCurrentScene = 0;
                _lastUnityErrorMsg = string.Empty;
                Log("OnSceneLoaded: " + scene.name);
                CoopModManager.EnsureCreated();
                if (CoopModManager.Instance != null)
                {
                    CoopModManager.Instance.OnUnitySceneLoaded(scene.name);
                }
            }
            catch (Exception ex)
            {
                Log("OnSceneLoaded error: " + ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Log(string message)
        {
            try
            {
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [CupheadCoop] {1}{2}",
                    DateTime.Now, message, Environment.NewLine);
                File.AppendAllText(_logPath, line);
            }
            catch
            {
                // Ignore early I/O errors
            }
        }
    }
}
