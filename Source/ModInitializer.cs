using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimLex
{
    public class ModInitializer : Mod
    {
        public static readonly string ModId = "com.rimlex";
        public static string ModDir;
        private static StreamWriter _log;
        private static Config _cfg;

        // 設定画面スクロール
        private static Vector2 _scrollPos = Vector2.zero;
        private static float _viewHeight = 800f;

        public ModInitializer(ModContentPack content) : base(content)
        {
            try
            {
                ModDir = content.RootDir;

                string iniPath = Path.Combine(ModDir, "RimLex.ini");
                string dictPath = Path.Combine(ModDir, "Dict", "strings_ja.tsv");
                string logPath = Path.Combine(ModDir, "RimLex.log");
                string exportDir = Path.Combine(ModDir, "Export");

                _cfg = Config.Load(iniPath, dictPath, logPath, exportDir);

                try
                {
                    var dir = Path.GetDirectoryName(_cfg.LogPath);
                    if (string.IsNullOrWhiteSpace(dir)) dir = ModDir;
                    Directory.CreateDirectory(dir);
                    _log = new StreamWriter(_cfg.LogPath, true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimLex] File log init failed: " + ex.Message);
                    _log = null;
                }

                LogInfo("=== RimLex initializing ===");
                try { Directory.CreateDirectory(_cfg.ExportRoot); } catch { }

                NoiseFilter.Init(_cfg);

                TranslatorHub.InitIO(
                    _cfg.DictTsv,
                    _cfg.ExportRoot,
                    _cfg.ExportMode,
                    _cfg.ExportPerMod,
                    _cfg.EmitAggregate,
                    LogInfo, LogWarn,
                    _cfg.PerModSubdir
                );

                TranslatorHub.ApplyRuntimeOptions(_cfg.PauseAggregate, _cfg.AggregateDebounceMs, _cfg.WatchDict, _cfg.DictTsv);

                var harmony = new Harmony(ModId);
                UIPatches.Apply(harmony, LogInfo, LogWarn, _cfg.ApplyDictAtRuntime);

                LogInfo("Options: Apply=" + _cfg.ApplyDictAtRuntime +
                        ", PerMod=" + _cfg.ExportPerMod +
                        ", Aggregate=" + _cfg.EmitAggregate +
                        ", Mode=" + _cfg.ExportMode +
                        ", PerModSubdir=" + _cfg.PerModSubdir +
                        ", PauseAggregate=" + _cfg.PauseAggregate +
                        ", WatchDict=" + _cfg.WatchDict);

                LogInfo("=== RimLex initialized ===");
            }
            catch (Exception ex)
            {
                LogWarn("Initialization error: " + ex);
            }
        }

        public override string SettingsCategory() => "RimLex - UI Text Localizer";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            float scrollbar = 16f;
            var outer = inRect;
            var view = new Rect(0f, 0f, outer.width - scrollbar, _viewHeight <= 0f ? outer.height : _viewHeight);

            Widgets.BeginScrollView(outer, ref _scrollPos, view, true);
            var ls = new Listing_Standard();
            ls.Begin(new Rect(0f, 0f, view.width, view.height));

            ls.Label("UI Text Localizer (runtime dictionary-based)");
            var totals = TranslatorHub.GetTotals();
            ls.Label($"直近セッション：置換/収集/除外/IO  →  {GetSessionText()}");
            ls.Label($"合計（起動〜）：置換={totals.totalReplaced} / 収集={totals.totalCollected} / 除外={NoiseFilter.ExcludedCount} / I/O={totals.totalIoErrors}");
            ls.GapLine();

            CheckboxWithTip(ls, "即時反映を有効化（ApplyDictAtRuntime）",
                "ON: 辞書ヒット時にその場で日本語化。OFF: 収集のみ。",
                ref _cfg.ApplyDictAtRuntime);

            CheckboxWithTip(ls, "Per-Modに分割して出力（ExportPerMod）",
                "ON: Export/<PerModSubdir>/<Mod名>/ へ。OFF: Export/Current/ へ。",
                ref _cfg.ExportPerMod);

            CheckboxWithTip(ls, "_All に統合出力する（EmitAggregate）",
                "ON: Export/_All/texts_en_aggregate.txt に軽量追記（整理で再構築）。",
                ref _cfg.EmitAggregate);

            DrawEnumDropdown(ls, "ExportMode",
                "TextOnly: texts_en.txt / Full: strings_en.tsv / Both: 両方。",
                new string[] { "TextOnly", "Full", "Both" }, _cfg.ExportMode, v => _cfg.ExportMode = v);

            Rect rSub = ls.GetRect(30f);
            Widgets.Label(rSub.LeftHalf(), "PerModSubdir： " + _cfg.PerModSubdir);
            if (Widgets.ButtonText(rSub.RightHalf(), "変更"))
            {
                var opts = new System.Collections.Generic.List<FloatMenuOption>
                {
                    new FloatMenuOption("PerMod", () => _cfg.PerModSubdir = "PerMod"),
                    new FloatMenuOption("Mods",   () => _cfg.PerModSubdir = "Mods"),
                    new FloatMenuOption("ByMod",  () => _cfg.PerModSubdir = "ByMod")
                };
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(rSub, "Per-Mod 出力の下位フォルダ名。");

            ls.GapLine();
            Widgets.Label(ls.GetRect(Text.LineHeight), $"MinLength：{_cfg.MinLength}   (ExcludePatterns 使用中)");
            Rect rML = ls.GetRect(30f);
            if (Widgets.ButtonText(rML.LeftHalf(), "ExcludePatterns を表示"))
                Messages.Message(_cfg.ExcludePatterns ?? "(none)", MessageTypeDefOf.NeutralEvent, false);
            if (Widgets.ButtonText(rML.RightHalf(), "画面除外リスト(編集はini)"))
                Messages.Message("Included/ExcludedWindows は RimLex.ini を編集。既定で Dialog_ModSettings / EditWindow_Log / Page_ModsConfig は除外されています。", MessageTypeDefOf.NeutralEvent, false);

            CheckboxWithTip(ls, "集約軽量追記を一時停止（PauseAggregate）",
                "ON: _All への軽量追記を停止（整理は可能）。",
                ref _cfg.PauseAggregate);

            Rect rDeb = ls.GetRect(30f);
            Widgets.Label(rDeb.LeftHalf(), "AggregateDebounceMs： " + _cfg.AggregateDebounceMs + " ms");
            if (Widgets.ButtonText(rDeb.RightHalf().LeftHalf(), "-50")) _cfg.AggregateDebounceMs = Math.Max(0, _cfg.AggregateDebounceMs - 50);
            if (Widgets.ButtonText(rDeb.RightHalf().RightHalf(), "+50")) _cfg.AggregateDebounceMs += 50;

            CheckboxWithTip(ls, "辞書ファイルを監視して自動リロード（WatchDict）",
                "ON: Dict/strings_ja.tsv の更新で自動リロード。",
                ref _cfg.WatchDict);

            CheckboxWithTip(ls, "除外画面のログ記録（LogExcludedScreens）",
                "ON: 画面除外イベントを RimLex.log に集約出力。",
                ref _cfg.LogExcludedScreens);

            CheckboxWithTip(ls, "デバッグHUDを表示（将来）",
                "右上に簡易カウンタ（実装保留）。",
                ref _cfg.ShowDebugHUD);

            if (DrawButton(ls, "設定を保存", "RimLex.ini に保存。"))
            {
                TryMenuAction("Save ini", () =>
                {
                    _cfg.Save();
                    NoiseFilter.Init(_cfg);
                    TranslatorHub.ApplyRuntimeOptions(_cfg.PauseAggregate, _cfg.AggregateDebounceMs, _cfg.WatchDict, _cfg.DictTsv);
                    Messages.Message("設定を保存しました。", MessageTypeDefOf.TaskCompletion, false);
                });
            }

            ls.GapLine();

            if (DrawButton(ls, "未訳一覧を生成", "Export/_All/untranslated.txt 等を再生成。"))
            {
                TryMenuAction("Build untranslated", () =>
                {
                    TranslatorHub.RebuildAggregateAndUntranslated(out var agg, out var untrans, out var mods);
                    Messages.Message($"未訳 {untrans} 行 / 集約 {agg} 行 / Mod別 {mods} セクション", MessageTypeDefOf.TaskCompletion, false);
                });
            }

            if (DrawButton(ls, "整理（MOD別/集約）を生成", "集約ファイルを再構築。"))
            {
                TryMenuAction("Rebuild aggregate", () =>
                {
                    TranslatorHub.RebuildAggregateAndUntranslated(out var agg, out var untrans, out var mods);
                    Messages.Message($"集約 {agg} 行 / 未訳 {untrans} 行 / Mod別 {mods}", MessageTypeDefOf.TaskCompletion, false);
                });
            }

            if (DrawButton(ls, "未訳からTSV雛形を作成", "Dict/strings_ja_template.tsv を作成。"))
            {
                TryMenuAction("Build TSV template", () =>
                {
                    if (TranslatorHub.TryBuildTsvTemplateFromUntranslated(out var path, out var err))
                        Messages.Message("雛形を生成: " + path, MessageTypeDefOf.TaskCompletion, false);
                    else
                        Messages.Message("生成失敗: " + (err ?? "unknown"), MessageTypeDefOf.RejectInput, false);
                });
            }

            ls.GapLine();

            if (DrawButton(ls, "フォルダを開く：_All", "Export/_All を開きます。"))
                OpenFolder(Path.Combine(_cfg.ExportRoot, "_All"));
            if (DrawButton(ls, "フォルダを開く：PerMod", $"Export/{_cfg.PerModSubdir} を開きます。"))
                OpenFolder(Path.Combine(_cfg.ExportRoot, _cfg.PerModSubdir));
            if (DrawButton(ls, "フォルダを開く：Export", "Export ルートフォルダ。"))
                OpenFolder(_cfg.ExportRoot);

            ls.GapLine();

            if (DrawButton(ls, "収集をリセット", "Export を初期化。"))
            {
                TryMenuAction("Export reset", () =>
                {
                    try
                    {
                        if (Directory.Exists(_cfg.ExportRoot)) Directory.Delete(_cfg.ExportRoot, true);
                        Directory.CreateDirectory(_cfg.ExportRoot);
                        TranslatorHub.ClearSessionCaches(out var pm, out var ag);
                        Messages.Message($"Export 初期化（キャッシュ: perMod {pm} / aggregate {ag}）", MessageTypeDefOf.TaskCompletion, false);
                    }
                    catch (Exception ex) { LogWarn("[Menu] Export reset failed: " + ex); }
                });
            }

            if (DrawButton(ls, "セッションキャッシュをクリア", "メモリ上の二重書き防止をクリア。"))
            {
                TranslatorHub.ClearSessionCaches(out var pm, out var ag);
                Messages.Message($"クリアしました（perMod {pm} / aggregate {ag}）。", MessageTypeDefOf.NeutralEvent, false);
            }

            if (DrawButton(ls, "ログをクリア", "RimLex.log を空にします。"))
            {
                TryMenuAction("Log clear", () =>
                {
                    try
                    {
                        _log?.Flush();
                        _log?.Dispose();
                    }
                    finally
                    {
                        _log = null;
                    }

                    File.WriteAllText(_cfg.LogPath, string.Empty, new System.Text.UTF8Encoding(false));
                    _log = new StreamWriter(_cfg.LogPath, true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                    Messages.Message("ログをクリアしました。", MessageTypeDefOf.NeutralEvent, false);
                    LogInfo("[Menu] Log cleared.");
                });
            }

            if (DrawButton(ls, "辞書を再読み込み", "Dict/strings_ja.tsv を再読み込み。"))
            {
                TryMenuAction("Reload dict", () =>
                {
                    TranslatorHub.ReloadDictionary();
                    Messages.Message($"辞書を再読み込み（{TranslatorHub.DictionaryEntryCount} 行）。", MessageTypeDefOf.TaskCompletion, false);
                });
            }

            if (DrawButton(ls, "動作確認", "Export/SelfTest にテスト出力。"))
            {
                TryMenuAction("SelfTest", () =>
                {
                    Directory.CreateDirectory(_cfg.ExportRoot);
                    string dir = Path.Combine(_cfg.ExportRoot, "SelfTest");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "ok.txt"), "Hello from RimLex self-test\n", new System.Text.UTF8Encoding(false));
                    LogInfo("[SelfTest] Export write OK: " + dir);
                    Messages.Message("テスト出力に成功しました。", MessageTypeDefOf.TaskCompletion, false);
                });
            }

            _viewHeight = Mathf.Max(ls.CurHeight + 12f, outer.height);
            ls.End();
            Widgets.EndScrollView();
        }

        private static string GetSessionText()
        {
            TranslatorHub.GetAndResetSessionStats(out var rep, out var col, out var io, out var exc);
            return $"置換={rep} / 収集={col} / 除外={exc} / I/O={io}";
        }

        // ===== 汎用UIヘルパ =====

        private static void CheckboxWithTip(Listing_Standard ls, string label, string tip, ref bool value)
        {
            Rect r = ls.GetRect(Text.LineHeight);
            bool v = value;
            Widgets.CheckboxLabeled(r, label, ref v);
            TooltipHandler.TipRegion(r, tip);
            value = v;
        }

        private static bool DrawButton(Listing_Standard ls, string label, string tip)
        {
            Rect r = ls.GetRect(30f);
            bool clicked = Widgets.ButtonText(r, label);
            TooltipHandler.TipRegion(r, tip);
            return clicked;
        }

        private static void DrawEnumDropdown(Listing_Standard ls, string label, string tip, string[] choices, string currentValue, Action<string> setValue)
        {
            Rect r = ls.GetRect(30f);
            Widgets.Label(r.LeftHalf(), label + "： " + currentValue);
            if (Widgets.ButtonText(r.RightHalf(), "変更"))
            {
                var options = new System.Collections.Generic.List<FloatMenuOption>();
                foreach (var c in choices) options.Add(new FloatMenuOption(c, () => setValue(c)));
                Find.WindowStack.Add(new FloatMenu(options));
            }
            TooltipHandler.TipRegion(r, tip);
        }

        // ===== 追加：未定義だったユーティリティ =====

        private static void TryMenuAction(string name, Action action)
        {
            try { action(); LogInfo("[Menu] " + name + " OK"); }
            catch (Exception ex)
            {
                LogWarn("[Menu] " + name + " failed: " + ex);
                Messages.Message(name + " 失敗: " + ex.Message, MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo()
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogWarn("[OpenFolder] " + path + " : " + ex.Message);
            }
        }

        // ログ
        public static void LogInfo(string msg)
        {
            try { _log?.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] [INFO] " + msg); } catch { }
            try { Log.Message("[RimLex] " + msg); } catch { }
        }
        public static void LogWarn(string msg)
        {
            try { _log?.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] [WARN] " + msg); } catch { }
            try { Log.Warning("[RimLex] " + msg); } catch { }
        }
    }
}
