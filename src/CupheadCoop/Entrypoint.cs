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

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void InitializeEarly()
        {
            try
            {
                _logPath = Path.GetFullPath("CupheadCoop.log");
                Log("CupheadCoopBootstrapper.InitializeEarly() invoked by Doorstop!");

                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

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
            Log("Hooked SceneManager.sceneLoaded successfully.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
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
