using Verse;

namespace UniqueWeaponsUnbound
{
    // Formats a def reference for error logs. Includes the originating
    // ModContentPack name so bug reports caused by malformed third-party defs
    // can be directed to the correct mod author.
    internal static class DefLogHelper
    {
        public static string SourceForLog(this Def def)
        {
            if (def == null)
                return "<null>";

            string modName = def.modContentPack?.Name ?? "<unknown mod>";
            return def.defName + " (from " + modName + ")";
        }
    }
}
