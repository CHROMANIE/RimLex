using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimLex
{
    [StaticConstructorOnStartup]
    public static class UIPatches_TooltipAndLabels
    {
        private const string HarmonyId = "RimLex.UIPatches.Extra";

        static UIPatches_TooltipAndLabels()
        {
            try
            {
                var h = new Harmony(HarmonyId);

                // TooltipHandler.TipRegion(Rect, string)
                var tipStr = AccessTools.Method(typeof(TooltipHandler), "TipRegion", new Type[] { typeof(Rect), typeof(string) });
                if (tipStr != null)
                {
                    h.Patch(tipStr,
                        prefix: new HarmonyMethod(typeof(UIPatches_TooltipAndLabels), nameof(TipRegion_String_Prefix)));
                    TryLog("Patched: TooltipHandler.TipRegion(Rect,string)");
                }

                // Widgets.Label(Rect, TaggedString)  ← ★追加
                var wLabelTagged = AccessTools.Method(typeof(Widgets), "Label", new Type[] { typeof(Rect), typeof(TaggedString) });
                if (wLabelTagged != null)
                {
                    h.Patch(wLabelTagged,
                        prefix: new HarmonyMethod(typeof(UIPatches_TooltipAndLabels), nameof(Widgets_Label_Tagged_Prefix)));
                    TryLog("Patched: Widgets.Label(Rect,TaggedString)");
                }

                // Listing_Standard.Label(...)（第一引数string）
                var labelMethods = AccessTools.GetDeclaredMethods(typeof(Listing_Standard))
                    .Where(m => m.Name == "Label" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string))
                    .ToArray();

                foreach (var m in labelMethods)
                    h.Patch(m, prefix: new HarmonyMethod(typeof(UIPatches_TooltipAndLabels), nameof(Listing_Label_Prefix)));
                if (labelMethods.Length > 0)
                    TryLog($"Patched: Listing_Standard.Label x{labelMethods.Length}");

                // Listing_Standard.LabelDouble(string, string, ...)
                var labelDouble = AccessTools.Method(typeof(Listing_Standard), "LabelDouble",
                    new Type[] { typeof(string), typeof(string), typeof(float), typeof(string) });
                if (labelDouble == null)
                {
                    labelDouble = AccessTools.GetDeclaredMethods(typeof(Listing_Standard))
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "LabelDouble") return false;
                            var p = m.GetParameters();
                            return p.Length >= 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string);
                        });
                }
                if (labelDouble != null)
                {
                    h.Patch(labelDouble,
                        prefix: new HarmonyMethod(typeof(UIPatches_TooltipAndLabels), nameof(Listing_LabelDouble_Prefix)));
                    TryLog("Patched: Listing_Standard.LabelDouble(string,string,...)");
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[RimLex] UIPatches_TooltipAndLabels init failed: " + ex);
            }
        }

        // --- Prefixes ---

        public static void TipRegion_String_Prefix(Rect rect, ref string tip)
        {
            try
            {
                if (!string.IsNullOrEmpty(tip))
                    tip = TranslatorHub.TranslateOrEnroll(tip, "TooltipHandler", "tooltip");
            }
            catch { }
        }

        // ★追加：Widgets.Label(Rect, TaggedString)
        public static void Widgets_Label_Tagged_Prefix(Rect rect, ref TaggedString label)
        {
            try
            {
                string s = label.ToString();
                if (!string.IsNullOrEmpty(s))
                    label = TranslatorHub.TranslateOrEnroll(s, "Widgets", "label");
            }
            catch { }
        }

        // Listing_Standard.Label(string …)
        public static void Listing_Label_Prefix(ref string __0)
        {
            try
            {
                if (!string.IsNullOrEmpty(__0))
                    __0 = TranslatorHub.TranslateOrEnroll(__0, "Listing_Standard", "label");
            }
            catch { }
        }

        // Listing_Standard.LabelDouble(string left, string right, …)
        public static void Listing_LabelDouble_Prefix(ref string __0, ref string __1)
        {
            try
            {
                if (!string.IsNullOrEmpty(__0))
                    __0 = TranslatorHub.TranslateOrEnroll(__0, "Listing_Standard", "label");
                if (!string.IsNullOrEmpty(__1))
                    __1 = TranslatorHub.TranslateOrEnroll(__1, "Listing_Standard", "label");
            }
            catch { }
        }

        private static void TryLog(string msg)
        {
            try { Log.Message($"[RimLex][INFO] {msg}"); } catch { }
        }
    }
}
