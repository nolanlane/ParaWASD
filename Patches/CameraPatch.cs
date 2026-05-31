using HarmonyLib;
using UnityEngine;

namespace ParaWASD.Patches
{
    [HarmonyPatch(typeof(HybridCamera), "Update")]
    public static class CameraPatch
    {
        static bool Prefix()
        {
            var c = ParaWASDController.ActiveInstance;
            return c == null || !c.IsActive;
        }
    }

    [HarmonyPatch(typeof(UpdateFreeCamera), "UpdateForPlayer")]
    public static class FreeCameraPatch
    {
        static bool Prefix()
        {
            var c = ParaWASDController.ActiveInstance;
            return c == null || !c.IsActive;
        }
    }

    /// <summary>
    /// Keeps the game's cursor model locked during ParaWASD look mode.
    /// Several gameplay raycasts read CursorManager.MouseLockedInPlace and
    /// MouseLockedPosition rather than Unity's Cursor.lockState directly.
    /// </summary>
    [HarmonyPatch(typeof(CursorManager), "LateUpdate")]
    public static class CursorManagerPatch
    {
        static bool Prefix()
        {
            var c = ParaWASDController.ActiveInstance;
            if (c != null && c.IsLookMode)
            {
                c.ForceLookCursorLock();
                return false;
            }

            return true;
        }

        // Drives the F6 toggle. Paralives destroys the BepInEx manager GameObject during
        // scene cleanup, so Plugin.Update stops firing; CursorManager is a global manager
        // that keeps ticking every frame, and this Harmony patch lives in the game assembly
        // (not on the destroyed manager), so it keeps polling F6. Postfixes run even when the
        // Prefix above skips the original body.
        static void Postfix() => Plugin.Instance?.Tick();
    }

    [HarmonyPatch(typeof(InputManager), "GetCursorPosition")]
    public static class InputManagerCursorPositionPatch
    {
        static bool Prefix(int playerIndex, ref Vector3 __result)
        {
            var c = ParaWASDController.ActiveInstance;
            if (c == null || !c.IsLookMode)
                return true;

            __result = ParaWASDController.GetScreenCenterPosition(playerIndex);
            return false;
        }
    }
}
