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

    /// <summary>
    /// While a ParaWASD conversation is open, block the game's world click-selection
    /// (UpdateSelect handles selecting Paras, items, walls, floors and terrain on click).
    /// The conversation dialog and the together-card UI are Unity UI canvases that handle
    /// their own clicks through the EventSystem, so they stay fully usable; only world
    /// objects/Paras become unselectable, as required for the conversation mode.
    /// </summary>
    [HarmonyPatch(typeof(UpdateSelect), "UpdateForPlayer")]
    public static class UpdateSelectConversationPatch
    {
        static bool Prefix()
        {
            var c = ParaWASDController.ActiveInstance;
            return c == null || !c.IsInConversation;
        }
    }

    /// <summary>
    /// While a ParaWASD conversation is open, suppress world hover-highlighting.
    /// The center-locked look raycast points straight at the conversation partner,
    /// so without this UpdateHover would keep the Para outlined for the whole
    /// conversation. UpdateSelect (click-selection) is blocked separately above.
    /// </summary>
    [HarmonyPatch(typeof(UpdateHover), "UpdateForPlayer")]
    public static class UpdateHoverConversationPatch
    {
        static bool Prefix()
        {
            var c = ParaWASDController.ActiveInstance;
            return c == null || !c.IsInConversation;
        }
    }

    /// <summary>
    /// Hide the base-game together meter (UICharactersTogetherBar, shown above the
    /// character portraits) while a ParaWASD conversation is open, so only our own
    /// in-dialog meter is visible. A CanvasGroup keeps the whole bar hidden and
    /// non-interactive without touching the game's list pooling or active state; the
    /// bar's own Update restores it the moment the conversation ends.
    /// </summary>
    [HarmonyPatch(typeof(UICharactersTogetherBar), "Update")]
    public static class TogetherBarHidePatch
    {
        static void Postfix(UICharactersTogetherBar __instance)
        {
            var c = ParaWASDController.ActiveInstance;
            bool hide = c != null && c.IsInConversation;

            var cg = __instance.GetComponent<CanvasGroup>();
            if (cg == null)
            {
                if (!hide) return;
                cg = __instance.gameObject.AddComponent<CanvasGroup>();
            }

            cg.alpha = hide ? 0f : 1f;
            cg.interactable = !hide;
            cg.blocksRaycasts = !hide;
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
