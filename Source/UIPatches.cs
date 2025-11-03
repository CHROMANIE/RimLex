using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLex
{
    [StaticConstructorOnStartup]
    public static class UIPatches
    {
        private static bool _applyAtRuntime;

        public static void Apply(Harmony harmony, Action<string> logInfo, Action<string> logWarn, bool applyAtRuntime)
        {
            _applyAtRuntime = applyAtRuntime;

            void TryPatch(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    logWarn?.Invoke("Patch step failed: " + ex.Message);
                    ModInitializer.LogWarn("Patch step failed: " + ex.Message);
                }
            }

            void PatchSingle(string description, Func<MethodBase> resolver, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
            {
                TryPatch(() =>
                {
                    var method = resolver();
                    if (method == null)
                    {
                        logWarn?.Invoke("Patch skip: " + description + " not found");
                        return;
                    }
                    harmony.Patch(method, prefix: prefix, postfix: postfix);
                    logInfo?.Invoke("Patched: " + description);
                });
            }

            void PatchMany(string description, MethodBase[] targets, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
            {
                TryPatch(() =>
                {
                    if (targets == null || targets.Length == 0)
                    {
                        logWarn?.Invoke("Patch skip: " + description + " not found");
                        return;
                    }
                    foreach (var method in targets)
                        harmony.Patch(method, prefix: prefix, postfix: postfix);
                    logInfo?.Invoke($"Patched: {description} x{targets.Length}");
                });
            }

            PatchSingle("Widgets.Label(Rect,string)",
                () => typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(mi =>
                    {
                        if (mi.Name != nameof(Widgets.Label)) return false;
                        var ps = mi.GetParameters();
                        return ps.Length == 2 && ps[0].ParameterType == typeof(Rect) && ps[1].ParameterType == typeof(string);
                    }),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(Widgets_Label_Prefix)));

            PatchSingle("Widgets.Label(Rect,TaggedString)",
                () => AccessTools.Method(typeof(Widgets), nameof(Widgets.Label), new[] { typeof(Rect), typeof(TaggedString) }),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(Widgets_Label_Tagged_Prefix)));

            PatchMany("Listing_Standard.Label",
                typeof(Listing_Standard).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(mi => mi.Name == "Label" && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].ParameterType == typeof(string))
                    .Cast<MethodBase>()
                    .ToArray(),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(Listing_Label_Generic_Prefix)));

            PatchSingle("Listing_Standard.LabelDouble", () =>
                AccessTools.GetDeclaredMethods(typeof(Listing_Standard))
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "LabelDouble") return false;
                        var ps = m.GetParameters();
                        return ps.Length >= 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string);
                    }),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(Listing_LabelDouble_Prefix)));

            PatchSingle("Widgets.ButtonText",
                () => typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(mi => mi.Name == "ButtonText")
                    .FirstOrDefault(mi =>
                    {
                        var ps = mi.GetParameters();
                        return ps.Length >= 2 && ps[0].ParameterType == typeof(Rect) && ps[1].ParameterType == typeof(string);
                    }),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(ButtonText_Prefix)));

            PatchSingle("Listing_Standard.Slider",
                () => AccessTools.Method(typeof(Listing_Standard), "Slider", new[] { typeof(float), typeof(float), typeof(float) }),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(Slider_Prefix)));

            PatchMany("TooltipHandler.TipRegion(Rect,string)",
                typeof(TooltipHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(mi => mi.Name == "TipRegion")
                    .Where(mi =>
                    {
                        var ps = mi.GetParameters();
                        if (ps.Length < 2) return false;
                        if (ps[0].ParameterType != typeof(Rect)) return false;
                        return ps[1].ParameterType == typeof(string);
                    })
                    .Cast<MethodBase>()
                    .ToArray(),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(Tooltip_String_Prefix)));

            PatchSingle("TooltipHandler.TipRegion(Rect,TipSignal)",
                () => typeof(TooltipHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(mi =>
                    {
                        if (mi.Name != "TipRegion") return false;
                        var ps = mi.GetParameters();
                        return ps.Length == 2 && ps[0].ParameterType == typeof(Rect) && ps[1].ParameterType == typeof(TipSignal);
                    }),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(Tooltip_Signal_Prefix)));

            PatchMany("TooltipHandler.TipRegion(Rect,Func<string>)",
                typeof(TooltipHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(mi => mi.Name == "TipRegion")
                    .Where(mi =>
                    {
                        var ps = mi.GetParameters();
                        if (ps.Length < 2) return false;
                        if (ps[0].ParameterType != typeof(Rect)) return false;
                        return ps[1].ParameterType == typeof(Func<string>);
                    })
                    .Cast<MethodBase>()
                    .ToArray(),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(Tooltip_Func_Prefix)));

            PatchMany("TooltipHandler.TipRegion(Rect,Func<string>,string)",
                typeof(TooltipHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(mi => mi.Name == "TipRegion")
                    .Where(mi =>
                    {
                        var ps = mi.GetParameters();
                        if (ps.Length < 3) return false;
                        if (ps[0].ParameterType != typeof(Rect)) return false;
                        if (ps[1].ParameterType != typeof(Func<string>)) return false;
                        return ps[2].ParameterType == typeof(string);
                    })
                    .Cast<MethodBase>()
                    .ToArray(),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(Tooltip_FuncWithKey_Prefix)));

            PatchSingle("FloatMenuOption::.ctor",
                () => typeof(FloatMenuOption).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(ci =>
                    {
                        var ps = ci.GetParameters();
                        return ps.Length >= 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Action);
                    }),
                prefix: new HarmonyMethod(typeof(UIPatches), nameof(FloatMenuOption_Ctor_Prefix)));

            PatchSingle("Command.LabelCap.get",
                () => AccessTools.PropertyGetter(typeof(Command), nameof(Command.LabelCap)),
                postfix: new HarmonyMethod(typeof(UIPatches), nameof(Command_LabelCap_Postfix)));

            logInfo?.Invoke($"UIPatches applied. ApplyDictAtRuntime={_applyAtRuntime}");
        }

        public static bool Widgets_Label_Prefix(Rect rect, ref string label)
        {
            if (string.IsNullOrEmpty(label)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(label, "Widgets.Label", "UI");
            if (_applyAtRuntime) label = ja;
            return true;
        }

        public static bool Widgets_Label_Tagged_Prefix(Rect rect, ref TaggedString label)
        {
            var text = label.ToString();
            if (string.IsNullOrEmpty(text)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(text, "Widgets.Label", "UI");
            if (_applyAtRuntime) label = ja;
            return true;
        }

        public static bool Listing_Label_Generic_Prefix(ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(__0, "Listing_Standard.Label", "UI");
            if (_applyAtRuntime) __0 = ja;
            return true;
        }

        public static bool Listing_LabelDouble_Prefix(ref string __0, ref string __1)
        {
            if (!string.IsNullOrEmpty(__0))
            {
                var jaLeft = TranslatorHub.TranslateOrEnroll(__0, "Listing_Standard.LabelDouble", "UI");
                if (_applyAtRuntime) __0 = jaLeft;
            }

            if (!string.IsNullOrEmpty(__1))
            {
                var jaRight = TranslatorHub.TranslateOrEnroll(__1, "Listing_Standard.LabelDouble", "UI");
                if (_applyAtRuntime) __1 = jaRight;
            }

            return true;
        }

        public static bool ButtonText_Prefix(Rect rect, ref string __1)
        {
            if (string.IsNullOrEmpty(__1)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(__1, "Widgets.ButtonText", "UI");
            if (_applyAtRuntime) __1 = ja;
            return true;
        }

        public static bool Slider_Prefix(float val, float min, float max) => true;

        public static bool Tooltip_String_Prefix(Rect rect, ref string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            try
            {
                var ja = TranslatorHub.TranslateOrEnroll(text, "TooltipHandler.TipRegion", "Tooltip");
                if (_applyAtRuntime)
                    text = Normalizer.ReifyNewlines(ja);
            }
            catch
            {
            }
            return true;
        }

        public static bool Tooltip_Signal_Prefix(Rect rect, ref TipSignal tip)
        {
            try
            {
                var t = tip.text;
                if (!string.IsNullOrEmpty(t))
                {
                    var ja = TranslatorHub.TranslateOrEnroll(t, "TooltipHandler.TipRegion", "Tooltip");
                    if (_applyAtRuntime)
                        tip = new TipSignal(Normalizer.ReifyNewlines(ja), tip.delay);
                }
            }
            catch
            {
            }
            return true;
        }

        public static void Tooltip_Func_Prefix(Rect rect, ref Func<string> __1)
        {
            var original = __1;
            if (original == null) return;

            __1 = () =>
            {
                try
                {
                    var value = original();
                    if (string.IsNullOrEmpty(value)) return value;
                    var ja = TranslatorHub.TranslateOrEnroll(value, "TooltipHandler.TipRegion(Func)", "Tooltip");
                    return _applyAtRuntime ? Normalizer.ReifyNewlines(ja) : ja;
                }
                catch
                {
                    return original();
                }
            };
        }

        public static void Tooltip_FuncWithKey_Prefix(Rect rect, ref Func<string> __1, ref string __2)
            => Tooltip_Func_Prefix(rect, ref __1);

        public static bool FloatMenuOption_Ctor_Prefix(ref string label)
        {
            if (string.IsNullOrEmpty(label)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(label, "FloatMenuOption", "Context");
            if (_applyAtRuntime) label = ja;
            return true;
        }

        public static void Command_LabelCap_Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            var ja = TranslatorHub.TranslateOrEnroll(__result, "Command.Label", "Gizmo");
            if (_applyAtRuntime) __result = ja;
        }
    }
}
