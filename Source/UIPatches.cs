using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLex
{
    /// <summary>
    /// UI描画直前の文字列を拾って翻訳/収集に回すパッチ群。
    /// 環境差分に強い「ゆるめのシグネチャ探索」で各オーバーロードを個別にパッチする。
    /// 対象：
    ///  - Widgets.Label / Listing_Standard.Label（ラベル）
    ///  - Widgets.ButtonText（ボタン）
    ///  - Listing_Standard.SliderLabeled（★ラベル合成前にラベルだけ翻訳）
    ///  - TooltipHandler.TipRegion（string / TipSignal の両系統）
    ///  - FloatMenuOption（右クリック）
    ///  - Command.LabelCap（ギズモ）
    ///  - Listing_Standard.Slider（署名確認のみ／安全化）
    /// </summary>
    [StaticConstructorOnStartup]
    public static class UIPatches
    {
        private static bool _applyAtRuntime;

        public static void Apply(Harmony harmony, Action<string> logInfo, Action<string> logWarn, bool applyAtRuntime)
        {
            _applyAtRuntime = applyAtRuntime;

            try
            {
                // ---- Widgets.Label(Rect, string) ----
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

                // ---- Listing_Standard.Label(string, …)（どのオーバーロードでも第1引数がstringならOK）----
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
                        {
                            harmony.Patch(t, prefix: new HarmonyMethod(typeof(UIPatches), nameof(Listing_Label_Generic_Prefix)));
                        }
                        logInfo?.Invoke($"Patched: Listing_Standard.Label x{targets.Length}");
                    }
                });

                // ---- Widgets.ButtonText(Rect, string, …)（第1=Rect, 第2=string のものを拾う）----
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

                // ---- Listing_Standard.SliderLabeled(...)：★ラベル合成前にラベルだけ翻訳 ----
                Try(() =>
                {
                    var targets = typeof(Listing_Standard).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(mi => mi.Name == "SliderLabeled" && mi.GetParameters().Any(p => p.ParameterType == typeof(string)))
                        .ToArray();

                    foreach (var t in targets)
                        harmony.Patch(t, prefix: new HarmonyMethod(typeof(UIPatches), nameof(SliderLabeled_Prefix)));

                    if (targets.Length > 0)
                        logInfo?.Invoke($"Patched: Listing_Standard.SliderLabeled x{targets.Length}");
                    else
                        logWarn?.Invoke("Patch skip: Listing_Standard.SliderLabeled(*) not found");
                });

                // ---- Slider：署名差異で例外を出さないように try 保護（収集はしない）----
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

                // ---- TooltipHandler.TipRegion(Rect, string) の全オーバーロード（第2引数=string）----
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
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(UIPatches), nameof(Tooltip_String_Prefix)));
                    }
                    if (tipStrMethods.Length > 0)
                        logInfo?.Invoke($"Patched: TooltipHandler.TipRegion(Rect,string) x{tipStrMethods.Length}");
                });

                // ---- TooltipHandler.TipRegion(Rect, TipSignal) ----
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

                // ---- FloatMenuOption ctor（第1=string, 第2=Action を満たす最初のコンストラクタ）----
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

                // ---- Command.LabelCap getter ----
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

        // -----------------------------
        // Label / Button
        // -----------------------------
        public static bool Widgets_Label_Prefix(Rect rect, ref string label)
        {
            if (string.IsNullOrEmpty(label)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(label, "Widgets.Label", "UI");
            if (_applyAtRuntime) label = ja;
            return true;
        }

        // どの Listing_Standard.Label(*) でも第1引数 string を拾えるように、必要最小限の引数のみ受ける
        public static bool Listing_Label_Generic_Prefix(ref string __0)
        {
            if (string.IsNullOrEmpty(__0)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(__0, "Listing_Standard.Label", "UI");
            if (_applyAtRuntime) __0 = ja;
            return true;
        }

        public static bool ButtonText_Prefix(Rect rect, ref string __1)
        {
            // __1 は label（第2引数）
            if (string.IsNullOrEmpty(__1)) return true;
            var ja = TranslatorHub.TranslateOrEnroll(__1, "Widgets.ButtonText", "UI");
            if (_applyAtRuntime) __1 = ja;
            return true;
        }

        // -----------------------------
        // SliderLabeled（★合成前にラベルだけ翻訳）
        // -----------------------------
        public static bool SliderLabeled_Prefix(MethodBase __originalMethod, object[] __args)
        {
            try
            {
                var ps = __originalMethod.GetParameters();
                // 最初の string パラメータをラベルとみなす
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType == typeof(string))
                    {
                        var s = __args[i] as string;
                        if (!string.IsNullOrEmpty(s))
                        {
                            var ja = TranslatorHub.TranslateOrEnroll(s, "Listing_Standard.SliderLabeled", "UI");
                            if (_applyAtRuntime) __args[i] = ja;
                        }
                        break;
                    }
                }
            }
            catch { }
            return true;
        }

        // スライダー：収集なし。署名だけ合わせて“パッチ成功”ログを出せるようにする
        public static bool Slider_Prefix(float val, float min, float max) => true;

        // -----------------------------
        // Tooltip
        // -----------------------------
        public static bool Tooltip_String_Prefix(Rect rect, ref string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            try
            {
                var ja = TranslatorHub.TranslateOrEnroll(text, "TooltipHandler.TipRegion", "Tooltip");
                if (_applyAtRuntime) text = ja;
            }
            catch { }
            return true;
        }

        public static bool Tooltip_Signal_Prefix(Rect rect, ref TipSignal tip)
        {
            try
            {
                // textGetter を優先して実文字列を取り出す（改行含む）
                string raw = null;
                try { raw = tip.textGetter != null ? tip.textGetter() : tip.text; } catch { raw = tip.text; }

                if (!string.IsNullOrEmpty(raw))
                {
                    var ja = TranslatorHub.TranslateOrEnroll(raw, "TooltipHandler.TipRegion", "Tooltip");
                    if (_applyAtRuntime)
                    {
                        // 既存の delay（または priority 相当）を維持しつつ text だけ置換
                        tip = new TipSignal(ja, tip.delay);
                    }
                }
            }
            catch { }
            return true;
        }

        // -----------------------------
        // FloatMenu / Gizmo
        // -----------------------------
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
