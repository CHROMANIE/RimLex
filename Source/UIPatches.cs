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

            try
            {
                // （既存パッチ群は変更なし：Label/Button/Slider/FloatMenu/Gizmo ...）

                // ---- Widgets.Label(Rect, string)
                Try(() =>
                {
                    var m = typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(mi =>
                        {
                            if (mi.Name != nameof(Widgets.Label)) return false;
                            var ps = mi.GetParameters();
                            return ps.Length == 2 && ps[0].ParameterType == typeof(Rect) && ps[1].ParameterType == typeof(string);
                        });
                    if (m != null)
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(UIPatches), nameof(Widgets_Label_Prefix)));
                        logInfo?.Invoke("Patched: Widgets.Label(Rect,string)");
                    }
                    else logWarn?.Invoke("Patch skip: Widgets.Label(Rect,string) not found");
                });

                // ---- Listing_Standard.Label(*)
                Try(() =>
                {
                    var targets = typeof(Listing_Standard).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(mi => mi.Name == "Label" && mi.GetParameters().Length >= 1 && mi.GetParameters()[0].ParameterType == typeof(string))
                        .ToArray();
                    if (targets.Length == 0)
                    {
                        logWarn?.Invoke("Patch skip: Listing_Standard.Label(*) not found");
                    }
                    else
                    {
                        foreach (var t in targets)
                            harmony.Patch(t, prefix: new HarmonyMethod(typeof(UIPatches), nameof(Listing_Label_Generic_Prefix)));
                        logInfo?.Invoke($"Patched: Listing_Standard.Label x{targets.Length}");
                    }
                });

                // ---- Widgets.ButtonText(Rect, string, …)
                Try(() =>
                {
                    var target = typeof(Widgets).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(mi => mi.Name == "ButtonText")
                        .FirstOrDefault(mi =>
                        {
                            var ps = mi.GetParameters();
                            return ps.Length >= 2 && ps[0].ParameterType == typeof(Rect) && ps[1].ParameterType == typeof(string);
                        });
                    if (target != null)
                    {
                        harmony.Patch(target, prefix: new HarmonyMethod(typeof(UIPatches), nameof(ButtonText_Prefix)));
                        logInfo?.Invoke("Patched: Widgets.ButtonText");
                    }
                    else logWarn?.Invoke("Patch skip: Widgets.ButtonText(Rect,string,...) not found");
                });

                // ---- Slider (署名保護のみ)
                Try(() =>
                {
                    var slider = AccessTools.Method(typeof(Listing_Standard), "Slider", new[] { typeof(float), typeof(float), typeof(float) });
                    if (slider != null)
                    {
                        harmony.Patch(slider, prefix: new HarmonyMethod(typeof(UIPatches), nameof(Slider_Prefix)));
                        logInfo?.Invoke("Patched: Listing_Standard.Slider");
                    }
                    else logWarn?.Invoke("Patch skip: Listing_Standard.Slider(float,float,float) not found");
                });

                // ---- TooltipHandler.TipRegion(Rect, string)  ※環境によって存在しない場合あり
                Try(() =>
                {
                    var tipStrMethods = typeof(TooltipHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(mi => mi.Name == "TipRegion")
                        .Where(mi =>
                        {
                            var ps = mi.GetParameters();
                            return ps.Length == 2 && ps[0].ParameterType == typeof(Rect) && ps[1].ParameterType == typeof(string);
                        })
                        .ToArray();
                    foreach (var m in tipStrMethods)
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(UIPatches), nameof(Tooltip_String_Prefix)));
                    if (tipStrMethods.Length > 0)
                        logInfo?.Invoke($"Patched: TooltipHandler.TipRegion(Rect,string) x{tipStrMethods.Length}");
                });

                // ---- TooltipHandler.TipRegion(Rect, TipSignal)
                Try(() =>
                {
                    var m = typeof(TooltipHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(mi =>
                        {
                            if (mi.Name != "TipRegion") return false;
                            var ps = mi.GetParameters();
                            return ps.Length == 2 && ps[0].ParameterType == typeof(Rect) && ps[1].ParameterType == typeof(TipSignal);
                        });
                    if (m != null)
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(UIPatches), nameof(Tooltip_Signal_Prefix)));
                        logInfo?.Invoke("Patched: TooltipHandler.TipRegion(Rect,TipSignal)");
                    }
                    else logWarn?.Invoke("Patch skip: TooltipHandler.TipRegion(Rect,TipSignal) not found");
                });

                // ---- ★追加：TooltipHandler.TipRegion(Rect, Func<string>)
                Try(() =>
                {
                    var tipFunc2 = typeof(TooltipHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(mi => mi.Name == "TipRegion")
                        .Where(mi =>
                        {
                            var ps = mi.GetParameters();
                            return ps.Length == 2 && ps[0].ParameterType == typeof(Rect) && ps[1].ParameterType == typeof(Func<string>);
                        })
                        .ToArray();
                    foreach (var m in tipFunc2)
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(UIPatches), nameof(Tooltip_Func_Prefix)));
                    if (tipFunc2.Length > 0)
                        logInfo?.Invoke($"Patched: TooltipHandler.TipRegion(Rect,Func<string>) x{tipFunc2.Length}");
                });

                // ---- ★追加：TooltipHandler.TipRegion(Rect, Func<string>, string)
                Try(() =>
                {
                    var tipFunc3 = typeof(TooltipHandler).GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(mi => mi.Name == "TipRegion")
                        .Where(mi =>
                        {
                            var ps = mi.GetParameters();
                            return ps.Length == 3
                                   && ps[0].ParameterType == typeof(Rect)
                                   && ps[1].ParameterType == typeof(Func<string>)
                                   && ps[2].ParameterType == typeof(string);
                        })
                        .ToArray();
                    foreach (var m in tipFunc3)
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(UIPatches), nameof(Tooltip_FuncWithKey_Prefix)));
                    if (tipFunc3.Length > 0)
                        logInfo?.Invoke($"Patched: TooltipHandler.TipRegion(Rect,Func<string>,string) x{tipFunc3.Length}");
                });

                // ---- FloatMenuOption::.ctor(string, Action, …)
                Try(() =>
                {
                    var ctor = typeof(FloatMenuOption).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(ci =>
                        {
                            var ps = ci.GetParameters();
                            return ps.Length >= 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Action);
                        });
                    if (ctor != null)
                    {
                        harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(UIPatches), nameof(FloatMenuOption_Ctor_Prefix)));
                        logInfo?.Invoke("Patched: FloatMenuOption::.ctor");
                    }
                    else logWarn?.Invoke("Patch skip: FloatMenuOption ctor not found");
                });

                // ---- Command.LabelCap.get
                Try(() =>
                {
                    var getter = AccessTools.PropertyGetter(typeof(Command), nameof(Command.LabelCap));
                    if (getter != null)
                    {
                        harmony.Patch(getter, postfix: new HarmonyMethod(typeof(UIPatches), nameof(Command_LabelCap_Postfix)));
                        logInfo?.Invoke("Patched: Command.LabelCap.get");
                    }
                    else logWarn?.Invoke("Patch skip: Command.LabelCap.get not found");
                });

                logInfo?.Invoke($"UIPatches applied. ApplyDictAtRuntime={_applyAtRuntime}");
            }
            catch (Exception ex)
            {
                logWarn?.Invoke("UIPatches.Apply fatal: " + ex);
            }
        }

        private static void Try(Action body)
        {
            try { body(); } catch (Exception ex) { ModInitializer.LogWarn("Patch step failed: " + ex.Message); }
        }

        // ----- Label/Button -----

        public static bool Widgets_Label_Prefix(Rect rect, ref string label)
        {
            if (string.IsNullOrEmpty(label)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(label, "Widgets.Label", "UI");
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

        public static bool ButtonText_Prefix(Rect rect, ref string __1)
        {
            if (string.IsNullOrEmpty(__1)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(__1, "Widgets.ButtonText", "UI");
            if (_applyAtRuntime) __1 = ja;
            return true;
        }

        public static bool Slider_Prefix(float val, float min, float max) => true;

        // ----- Tooltip -----

        public static bool Tooltip_String_Prefix(Rect rect, ref string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            try
            {
                var ja = TranslatorHub.TranslateOrEnroll(text, "TooltipHandler.TipRegion", "Tooltip");
                if (_applyAtRuntime)
                    text = Normalizer.ReifyNewlines(ja);   // 改行復元
            }
            catch { }
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
                        tip = new TipSignal(Normalizer.ReifyNewlines(ja), tip.delay);  // 改行復元
                }
            }
            catch { }
            return true;
        }

        // ★追加：TooltipHandler.TipRegion(Rect, Func<string>)
        public static void Tooltip_Func_Prefix(Rect rect, ref Func<string> __1)
        {
            var orig = __1;
            if (orig == null) return;

            __1 = () =>
            {
                try
                {
                    var s = orig();
                    if (string.IsNullOrEmpty(s)) return s;
                    var ja = TranslatorHub.TranslateOrEnroll(s, "TooltipHandler.TipRegion(Func)", "Tooltip");
                    return _applyAtRuntime ? Normalizer.ReifyNewlines(ja) : ja;
                }
                catch { return orig(); }
            };
        }

        // ★追加：TooltipHandler.TipRegion(Rect, Func<string>, string)
        public static void Tooltip_FuncWithKey_Prefix(Rect rect, ref Func<string> __1, ref string __2)
            => Tooltip_Func_Prefix(rect, ref __1);

        // ----- FloatMenu / Gizmo -----

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
