using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace UniqueWeaponsUnbound
{
    // Optional integration with Vanilla Expanded Framework's
    // RecipeInheritanceExtension, which lets modded workbenches inherit recipes
    // from vanilla anchors (smithy / machining / fabrication). Used by
    // WorkbenchUtility to fold those benches into the correct tier so weapon
    // customization is offered at them.
    //
    // All access goes through reflection so this mod compiles and runs without
    // VEF installed. Mirrors the structure of AlphaArmouryIntegration: the
    // static ctor resolves the type/field once and logs a single warning when
    // VEF is loaded but the integration surface has drifted (renamed type,
    // renamed field, unexpected field type). When VEF is absent the integration
    // is silently unavailable.
    internal static class VEFRecipeInheritanceIntegration
    {
        private const string ExtensionTypeName = "VEF.Buildings.RecipeInheritanceExtension";
        private const string FieldName = "inheritRecipesFrom";

        private static readonly Type ExtensionType;
        private static readonly FieldInfo InheritRecipesFromField;

        public static bool Available => InheritRecipesFromField != null;

        static VEFRecipeInheritanceIntegration()
        {
            try
            {
                ExtensionType = GenTypes.GetTypeInAnyAssembly(ExtensionTypeName);
                if (ExtensionType != null)
                {
                    InheritRecipesFromField = ExtensionType.GetField(
                        FieldName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (InheritRecipesFromField != null
                        && !typeof(List<ThingDef>).IsAssignableFrom(InheritRecipesFromField.FieldType))
                    {
                        InheritRecipesFromField = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Unique Weapons Unbound] VEF reflection failed: " + ex);
                return;
            }

            // VEF is "loaded" iff the extension type resolved. No packageId check
            // needed — the type's presence is direct evidence. Warning fires only
            // when VEF is present but the field surface has drifted, so users
            // without VEF stay quiet.
            if (ExtensionType != null && InheritRecipesFromField == null)
            {
                Log.Warning("[Unique Weapons Unbound] VEF active but "
                    + ExtensionTypeName + "." + FieldName
                    + " could not be resolved as List<ThingDef>; modded workbenches "
                    + "inheriting recipes via VEF will not be classified by tier.");
            }
        }

        // Returns true and emits the inherited-recipe source list if ext is a
        // VEF RecipeInheritanceExtension. Returns false for any non-extension
        // input or when the integration is unavailable.
        public static bool TryGetInheritFrom(DefModExtension ext, out List<ThingDef> inheritFrom)
        {
            inheritFrom = null;
            if (!Available || ext == null || !ExtensionType.IsInstanceOfType(ext))
                return false;

            inheritFrom = InheritRecipesFromField.GetValue(ext) as List<ThingDef>;
            return inheritFrom != null;
        }
    }
}
