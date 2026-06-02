using HarmonyLib;

namespace ParaWASD.Patches
{
    /// <summary>
    /// Stops the game from injecting autonomous interactions onto the Para the player is driving in
    /// first person, and ONLY that Para. Everyone else - the rest of the household and the whole
    /// town - keeps their normal vanilla autonomy. We block at the injection source instead of
    /// cancelling after the fact so the driven Para never holds a half-cancelled interaction.
    /// </summary>
    [HarmonyPatch(typeof(InteractionManager), "InjectInteraction")]
    public static class InjectInteractionAutonomyPatch
    {
        static bool Prefix(AssetCharacter character, bool isIdleAutonomous, bool isForcedAutonomous)
        {
            if (!isIdleAutonomous && !isForcedAutonomous)
                return true;
            if (character == null)
                return true;

            return !ParaWASDController.ShouldSuppressAutonomyFor(character.GUID);
        }
    }
}
