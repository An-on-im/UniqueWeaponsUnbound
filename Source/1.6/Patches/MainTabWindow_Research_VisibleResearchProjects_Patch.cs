using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace UniqueWeaponsUnbound.Patches
{
    [HarmonyPatch(typeof(MainTabWindow_Research),
        nameof(MainTabWindow_Research.VisibleResearchProjects), MethodType.Getter)]
    public static class MainTabWindow_Research_VisibleResearchProjects_Patch
    {
        // Vanilla caches the underlying list and invalidates it in PreOpen by
        // assigning a fresh List<>, so reference identity on __result is a
        // reliable "have we already processed this instance" signal. Without
        // this, our RemoveAll would re-scan every research def every frame the
        // tab is open (DrawProjectInfo / DrawRightRect / UpdateSearchResults
        // all hit the getter per-frame).
        private static List<ResearchProjectDef> lastFiltered;

        [HarmonyPostfix]
        public static void Postfix(List<ResearchProjectDef> __result)
        {
            // A throw here propagates into the Research tab getter and breaks
            // tab rendering for every research project, not just ours. ErrorOnce
            // so a recurring per-frame failure doesn't flood the log.
            try
            {
                FilterHiddenProjects(__result);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(
                    "[Unique Weapons Unbound] Research-tab filter failed: " + ex,
                    "UWU_ResearchFilterFail".GetHashCode());
            }
        }

        private static void FilterHiddenProjects(List<ResearchProjectDef> projects)
        {
            if (projects == null || ReferenceEquals(projects, lastFiltered))
                return;

            // Settings can be null if mod startup ordering or a corrupt settings
            // file leaves GetSettings returning null. Default to showing the
            // projects (matches requireCustomizationResearch=true) rather than
            // silently hiding them. Still cache the reference so we don't re-
            // check Settings every frame.
            if (UWU_Mod.Settings == null || UWU_Mod.Settings.requireCustomizationResearch)
            {
                lastFiltered = projects;
                return;
            }

            projects.RemoveAll(def =>
                def == UWU_ResearchDefOf.UniqueSmithing
                || def == UWU_ResearchDefOf.UniqueMachining
                || def == UWU_ResearchDefOf.UniqueFabrication);
            lastFiltered = projects;
        }
    }
}
