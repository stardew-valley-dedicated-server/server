using HarmonyLib;
using StardewValley.Menus;

namespace JunimoServer.Shared
{
    /// <summary>
    /// Suppresses the vanilla ButtonTutorialMenu (the "USE TOOL / MENU / MOVE / RUN" hint
    /// that slides in from the left on first farmhouse / farm entry). A ctor postfix
    /// marks the instance destroyed; draw() short-circuits on its own !destroy guard
    /// and GameLocation.cleanupBeforePlayerExit sweeps it via onScreenMenus.RemoveWhere
    /// on the next location change. The instance survives for one location-lifetime
    /// as an inert object (update() is a few int ops, draw() returns immediately).
    /// </summary>
    public static class ButtonTutorialSuppressor
    {
        public static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Constructor(typeof(ButtonTutorialMenu), new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(ButtonTutorialSuppressor), nameof(Ctor_Postfix))
            );
        }

        private static void Ctor_Postfix(ButtonTutorialMenu __instance)
        {
            __instance.destroy = true;
        }
    }
}
