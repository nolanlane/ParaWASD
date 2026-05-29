using BepInEx;
#if BEPINEX_UNITY_MONO
using BepInEx.Unity.Mono;
#endif
using HarmonyLib;
using UnityEngine;

namespace ParaWASD
{
    [BepInPlugin("com.knowyourlane.parawasd", "ParaWASD", "0.96.0-beta")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }

        private Harmony _harmony;
        private ParaWASDController _controller;

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo("ParaWASD loading...");

            _harmony = new Harmony("com.knowyourlane.parawasd");
            _harmony.PatchAll();

            Logger.LogInfo("ParaWASD loaded. Press F6 to toggle.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6))
            {
                ToggleParaWASD();
            }
        }

        private void ToggleParaWASD()
        {
            if (_controller == null)
            {
                var go = new GameObject("ParaWASDController");
                DontDestroyOnLoad(go);
                _controller = go.AddComponent<ParaWASDController>();
                Logger.LogInfo("ParaWASD mode ENABLED");
            }
            else if (_controller.IsActive)
            {
                _controller.Deactivate();
                Logger.LogInfo("ParaWASD mode DISABLED");
            }
            else
            {
                _controller.Activate();
                Logger.LogInfo("ParaWASD mode ENABLED");
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            if (_controller != null)
                Destroy(_controller.gameObject);
        }
    }
}
