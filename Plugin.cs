using BepInEx;
using BepInEx.Configuration;
#if BEPINEX_UNITY_MONO
using BepInEx.Unity.Mono;
#endif
using HarmonyLib;
using UnityEngine;

namespace ParaWASD
{
    // BepInEx 5 parses this with System.Version, which rejects pre-release suffixes
    // like "-beta". Keep it strictly numeric; the "beta" label lives in the release only.
    [BepInPlugin("com.parawasd.plugin", "ParaWASD", "0.97.3")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }

        internal static ConfigEntry<float> MouseSensitivity { get; private set; }
        internal static ConfigEntry<bool> InvertMouseY { get; private set; }
        internal static ConfigEntry<float> PitchMinimum { get; private set; }
        internal static ConfigEntry<float> PitchMaximum { get; private set; }
        internal static ConfigEntry<float> FieldOfView { get; private set; }
        internal static ConfigEntry<float> CameraSmoothing { get; private set; }
        internal static ConfigEntry<float> EyeHeightOffset { get; private set; }
        internal static ConfigEntry<float> FallbackEyeHeightOffset { get; private set; }
        internal static ConfigEntry<float> ForwardOffset { get; private set; }
        internal static ConfigEntry<float> MoveSpeed { get; private set; }
        internal static ConfigEntry<float> SprintMultiplier { get; private set; }
        internal static ConfigEntry<bool> CenterInteractEnabled { get; private set; }
        internal static ConfigEntry<float> CenterInteractDistance { get; private set; }

        private Harmony _harmony;
        private ParaWASDController _controller;

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo("ParaWASD loading...");

            BindConfig();

            _harmony = new Harmony("com.parawasd.plugin");
            _harmony.PatchAll();

            Logger.LogInfo("ParaWASD loaded. Press F6 to toggle.");
        }

        private void BindConfig()
        {
            MouseSensitivity = Config.Bind(
                "Look",
                "MouseSensitivity",
                2.0f,
                new ConfigDescription("Mouse look sensitivity while ParaWASD is active.", new AcceptableValueRange<float>(0.1f, 10f)));
            InvertMouseY = Config.Bind("Look", "InvertMouseY", false, "Reverse vertical mouse look.");
            PitchMinimum = Config.Bind(
                "Look",
                "PitchMinimum",
                -80f,
                new ConfigDescription("Lowest vertical look angle in degrees.", new AcceptableValueRange<float>(-89f, 0f)));
            PitchMaximum = Config.Bind(
                "Look",
                "PitchMaximum",
                80f,
                new ConfigDescription("Highest vertical look angle in degrees.", new AcceptableValueRange<float>(0f, 89f)));

            FieldOfView = Config.Bind(
                "Camera",
                "FieldOfView",
                70f,
                new ConfigDescription("First-person camera field of view.", new AcceptableValueRange<float>(40f, 110f)));
            CameraSmoothing = Config.Bind(
                "Camera",
                "CameraSmoothing",
                0f,
                new ConfigDescription("Optional camera smoothing. Set to 0 for the original instant camera.", new AcceptableValueRange<float>(0f, 25f)));
            EyeHeightOffset = Config.Bind(
                "Camera",
                "EyeHeightOffset",
                0.15f,
                new ConfigDescription("Vertical camera offset when the Para head bone is available.", new AcceptableValueRange<float>(-0.5f, 0.75f)));
            FallbackEyeHeightOffset = Config.Bind(
                "Camera",
                "FallbackEyeHeightOffset",
                1.6f,
                new ConfigDescription("Vertical camera offset if the Para head bone cannot be found.", new AcceptableValueRange<float>(0.5f, 2.2f)));
            ForwardOffset = Config.Bind(
                "Camera",
                "ForwardOffset",
                0.05f,
                new ConfigDescription("Forward camera offset from the Para head position.", new AcceptableValueRange<float>(-0.2f, 0.4f)));

            MoveSpeed = Config.Bind(
                "Movement",
                "MoveSpeed",
                3.0f,
                new ConfigDescription("Base WASD movement speed.", new AcceptableValueRange<float>(0.5f, 8f)));
            SprintMultiplier = Config.Bind(
                "Movement",
                "SprintMultiplier",
                2.0f,
                new ConfigDescription("Movement speed multiplier while holding Left Shift.", new AcceptableValueRange<float>(1f, 4f)));

            CenterInteractEnabled = Config.Bind("Interaction", "CenterInteractEnabled", true, "Press E in look mode to open interactions for the object, floor, terrain, or character at the center of the camera.");
            CenterInteractDistance = Config.Bind(
                "Interaction",
                "CenterInteractDistance",
                25f,
                new ConfigDescription("Maximum distance for center-screen look interactions.", new AcceptableValueRange<float>(1f, 100f)));
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
