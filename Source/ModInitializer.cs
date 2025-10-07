using System;
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
        private static float _viewHeight = 800f; // ★初期高さ。0だと初回に何も描けないことがある

        public ModInitializer(ModContentPack content) : base(content)
        {
            try
            {
                ModDir = content.RootDir;

                // ini 既定
                string iniPath = Path.Combine(ModDir, "RimLex.ini");
                string dictPath = Path.Combine(ModDir, "Dict", "strings_ja.tsv");
                string logPath = Path.Combine(ModDir, "RimLex.log");
                string exportDir = Path.Combine(ModDir, "Export");

                _cfg = Config.Load(iniPath, dictPath, logPath, exportDir);

                // ファイルログ
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
            // スクロールビュー
            float scrollbar = 16f;
            var outer = inRect;
            var view = new Rect(0f, 0f, outer.width - scrollbar, _viewHeight <= 0f ? outer.height : _viewHeight);

            Widgets.BeginScrollView(outer, ref _scrollPos, view, true);

            var lsRect = new Rect(0f, 0f, view.width, view.height);
            var ls = new Listing_Standard();
            ls.Begin(lsRect);

            // ===== UI 本体 =====
            ls.Label("UI Text Localizer (runtime dictionary-based)");
            var totals = TranslatorHub.GetTotals();
            ls.Label($"直近セッション：置換/収集/除外/IO  →  {GetSessionText()}");
            ls.Label($"合計（起動〜）：置換={totals.totalReplaced} / 収集={totals.totalCollected} / 除外={NoiseFilter.ExcludedCount} / I/O={totals.totalIoErrors}");
            ls.GapLine();

            CheckboxWithTip(ls, "即時反映を有効化（ApplyDictAtRuntime）",
                "ONにすると辞書ヒット時にその場で日本語表示に置き換えます。OFFのときは収集のみ行います。",
                ref _cfg.ApplyDictAtRuntime);

            CheckboxWithTip(ls, "Per-Modに分割して出力（ExportPerMod）",
                "ON: Export/<PerModSubdir>/<Mod名>/ に書き分けます。OFF: Export/Current/ に一括出力します。",
                ref _cfg.ExportPerMod);

            CheckboxWithTip(ls, "_All に統合出力する（EmitAggregate）",
                "ON: 収集中に Export/_All/texts_en_aggregate.txt に軽量追記します。整理ボタンで正式再生成します。",
                ref _cfg.EmitAggregate);

            DrawEnumDropdown(ls, "ExportMode",
                "TextOnly: texts_en.txt のみ / Full: strings_en.tsv のみ / Both: 両方に出力します。",
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
            TooltipHandler.TipRegion(rSub, "Per-Mod のフォルダをまとめる下位フォルダ名です。既存の旧レイアウトも自動で読み取ります。");

            ls.GapLine();
            Widgets.Label(ls.GetRect(Text.LineHeight), $"MinLength：{_cfg.MinLength}   (ExcludePatterns 使用中)");
            Rect rML = ls.GetRect(30f);
            if (Widgets.ButtonText(rML.LeftHalf(), "ExcludePatterns を表示")) Messages.Message(_cfg.ExcludePatterns ?? "(none)", MessageTypeDefOf.NeutralEvent, false);
            if (Widgets.ButtonText(rML.RightHalf(), "画面除外リスト(編集はini)")) Messages.Message("Included/ExcludedWindows は RimLex.ini を編集してください。", MessageTypeDefOf.NeutralEvent, false);

            CheckboxWithTip(ls, "集約軽量追記を一時停止（PauseAggregate）",
                "ON: _All/texts_en_aggregate.txt への軽量追記を止めます（整理で正規再構築は可能）。I/Oをさらに抑えたい時向け。",
                ref _cfg.PauseAggregate);

            Rect rDeb = ls.GetRect(30f);
            Widgets.Label(rDeb.LeftHalf(), "AggregateDebounceMs： " + _cfg.AggregateDebounceMs + " ms");
            TooltipHandler.TipRegion(rDeb, "軽量追記をこの間隔でまとめ書きします。");
            if (Widgets.ButtonText(rDeb.RightHalf().LeftHalf(), "-50")) _cfg.AggregateDebounceMs = Math.Max(0, _cfg.AggregateDebounceMs - 50);
            if (Widgets.ButtonText(rDeb.RightHalf().RightHalf(), "+50")) _cfg.AggregateDebounceMs += 50;

            CheckboxWithTip(ls, "辞書ファイルを監視して自動リロード（WatchDict）",
                "ON: Dict/strings_ja.tsv をファイル更新で自動リロードします（小さな遅延あり）。",
                ref _cfg.WatchDict);

            CheckboxWithTip(ls, "除外画面のログ記録（LogExcludedScreens）",
                "ON: 画面ホワイト/ブラックリストで弾いたときに RimLex.log へ Excluded(...) を記録します（内部で間引きあり）。",
                ref _cfg.LogExcludedScreens);

            CheckboxWithTip(ls, "デバッグHUDを表示（将来）",
                "ON: 右上に簡易カウンタを描画（実装保留）。",
                ref _cfg.ShowDebugHUD);

            if (DrawButton(ls, "設定を保存", "RimLex.ini に現在の設定を保存します。次回起動時もこの値が使われます。"))
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

            if (DrawButton(ls, "未訳一覧を生成",
                "Export/_All/untranslated.txt（辞書未登録のみ）と\nExport/_All/strings_en_aggregate.tsv（表形式）を再生成します。"))
            {
                TryMenuAction("Build untranslated", () =>
                {
                    TranslatorHub.RebuildAggregateAndUntranslated(out var agg, out var untrans, out var mods);
                    Messages.Message($"未訳 {untrans} 行 / 集約 {agg} 行 / Mod別 {mods} セクションを出力しました。", MessageTypeDefOf.TaskCompletion, false);
                });
            }

            if (DrawButton(ls, "整理（MOD別/集約）を生成",
                "Export/_All/grouped_by_mod.txt（### Modごとの一覧）と\nExport/_All/texts_en_aggregate.txt（全Modの総集編）を再構築します。"))
            {
                TryMenuAction("Rebuild aggregate", () =>
                {
                    TranslatorHub.RebuildAggregateAndUntranslated(out var agg, out var untrans, out var mods);
                    Messages.Message($"集約 {agg} 行 / 未訳 {untrans} 行 / Mod別 {mods} セクションを再構築しました。", MessageTypeDefOf.TaskCompletion, false);
                });
            }

            if (DrawButton(ls, "未訳からTSV雛形を作成",
                "Export/_All/untranslated.txt を Dict/strings_ja_template.tsv に変換します（英語<TAB> のみ）。"))
            {
                TryMenuAction("Build TSV template", () =>
                {
                    if (TranslatorHub.TryBuildTsvTemplateFromUntranslated(out var path, out var err))
                        Messages.Message("雛形を生成しました: " + path, MessageTypeDefOf.TaskCompletion, false);
                    else
                        Messages.Message("雛形の生成に失敗: " + (err ?? "unknown"), MessageTypeDefOf.RejectInput, false);
                });
            }

            ls.GapLine();

            if (DrawButton(ls, "フォルダを開く：_All", "Export/_All フォルダを開きます。"))
                OpenFolder(Path.Combine(_cfg.ExportRoot, "_All"));
            if (DrawButton(ls, "フォルダを開く：PerMod", $"Export/{_cfg.PerModSubdir} フォルダ（各Modの収集先）を開きます。"))
                OpenFolder(Path.Combine(_cfg.ExportRoot, _cfg.PerModSubdir));
            if (DrawButton(ls, "フォルダを開く：Export", "Export ルートフォルダを開きます。"))
                OpenFolder(_cfg.ExportRoot);

            ls.GapLine();

            if (DrawButton(ls, "収集をリセット",
                "Export フォルダを空にして再作成します。全MODの収集履歴が消えます（辞書には影響しません）。"))
            {
                TryMenuAction("Export reset (manual)", () =>
                {
                    try
                    {
                        if (Directory.Exists(_cfg.ExportRoot))
                            Directory.Delete(_cfg.ExportRoot, true);
                        Directory.CreateDirectory(_cfg.ExportRoot);
                        TranslatorHub.ClearSessionCaches(out var pm, out var ag);
                        Messages.Message($"Export を初期化しました（キャッシュ: perMod {pm} / aggregate {ag} クリア）。", MessageTypeDefOf.TaskCompletion, false);
                    }
                    catch (Exception ex)
                    {
                        LogWarn("[Menu] Export reset failed: " + ex);
                        Messages.Message("Export の初期化に失敗しました。ログを確認してください。", MessageTypeDefOf.RejectInput, false);
                    }
                });
            }

            if (DrawButton(ls, "セッションキャッシュをクリア",
                "ゲーム起動中の二重書き込み防止キャッシュ（メモリ上）を初期化します。ファイル（Export/*）には触れません。"))
            {
                TranslatorHub.ClearSessionCaches(out var pm, out var ag);
                Messages.Message($"セッションキャッシュをクリアしました（perMod {pm} / aggregate {ag}）。", MessageTypeDefOf.NeutralEvent, false);
            }

            if (DrawButton(ls, "ログをクリア",
                "RimLex.log を空にして再初期化します。以後のログが見やすくなります。"))
            {
                TryMenuAction("Log clear", () =>
                {
                    try { _log?.Flush(); _log?.Dispose(); } catch { }
                    _log = null;

                    var logPath = _cfg.LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? ModDir);
                    using (var fs = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                        sw.WriteLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [INFO] log cleared.");

                    _log = new StreamWriter(logPath, true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                    Messages.Message("ログをクリアしました。", MessageTypeDefOf.NeutralEvent, false);
                    LogInfo("[Menu] Log cleared.");
                });
            }

            if (DrawButton(ls, "辞書を再読み込み", "Dict/strings_ja.tsv を再読み込みして、翻訳を即時反映します。"))
            {
                TryMenuAction("Reload dict", () =>
                {
                    TranslatorHub.ReloadDictionary();
                    Messages.Message($"辞書を再読み込みしました（{TranslatorHub.DictionaryEntryCount} 行）。", MessageTypeDefOf.TaskCompletion, false);
                });
            }

            if (DrawButton(ls, "動作確認", "Export/SelfTest に “Hello from RimLex self-test” を書き出します。"))
            {
                TryMenuAction("SelfTest", () =>
                {
                    Directory.CreateDirectory(_cfg.ExportRoot);
                    string dir = Path.Combine(_cfg.ExportRoot, "SelfTest");
                    Directory.CreateDirectory(dir);

                    AppendAtomic(Path.Combine(dir, "texts_en.txt"), "Hello from RimLex self-test" + Environment.NewLine);

                    string tsv = Path.Combine(dir, "strings_en.tsv");
                    if (!File.Exists(tsv))
                        WriteAtomic(tsv, "timestamp_utc\tmod\tsource\tscope\ttext" + Environment.NewLine);
                    AppendAtomic(tsv, DateTime.UtcNow.ToString("O") + "\tSelfTest\tMenu\tHello\tHello from RimLex self-test" + Environment.NewLine);

                    LogInfo("[SelfTest] Export write OK: " + dir);
                    Messages.Message("テスト出力に成功しました。Export/SelfTest を確認してください。", MessageTypeDefOf.TaskCompletion, false);
                });
            }
            // ===== ここまで =====

            _viewHeight = Mathf.Max(ls.CurHeight + 12f, outer.height); // ★実高さを反映（最低でも画面高）
            ls.End();
            Widgets.EndScrollView();
        }

        private static string GetSessionText()
        {
            TranslatorHub.GetAndResetSessionStats(out var rep, out var col, out var io, out var exc);
            return $"置換={rep} / 収集={col} / 除外={exc} / I/O={io}";
        }

        // UI helpers
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

        private static void TryMenuAction(string name, Action action)
        {
            try { action(); LogInfo("[Menu] " + name + " done."); }
            catch (Exception ex)
            {
                LogWarn("[Menu] " + name + " failed: " + ex);
                Messages.Message(name + " に失敗しました。詳細はログ参照。", MessageTypeDefOf.RejectInput, false);
            }
        }

        private static void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); Application.OpenURL("file:///" + path.Replace('\\', '/')); }
            catch (Exception ex) { LogWarn("OpenFolder failed: " + ex.Message); }
        }

        private static void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        private static void AppendAtomic(string path, string content)
        {
            if (!File.Exists(path)) { WriteAtomic(path, content); return; }
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                sw.Write(content);
        }

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
