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
