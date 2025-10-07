using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimLex
{
    /// <summary>
    /// 収集・辞書・出力の中核。UIPatches からここを叩く。
    /// 「同一テキストの連打」を避けるため、時間窓デバウンス＋セッション重複キャッシュを実装。
    /// </summary>
    public static class TranslatorHub
    {
        // ==== 設定/パス ====
        private static string _dictTsv;
        private static string _exportRoot;
        private static string _exportMode = "Both"; // TextOnly / Full / Both
        private static bool _perMod = true;
        private static bool _emitAggregate = true;
        private static string _perModSubdir = "PerMod";

        private static Action<string> _logInfo;
        private static Action<string> _logWarn;

        // ==== 状態 ====
        private static readonly object _lock = new object();

        // 辞書（英語→日本語）
        private static Dictionary<string, string> _dict = new Dictionary<string, string>(StringComparer.Ordinal);
        public static int DictionaryEntryCount => _dict.Count;

        // 監視
        private static FileSystemWatcher _watcher;

        // セッション統計
        private static long _sessionReplaced = 0;
        private static long _sessionCollected = 0;
        private static long _sessionIoErrors = 0;

        // トータル統計（起動〜）
        private static long _totalReplaced = 0;
        private static long _totalCollected = 0;
        private static long _totalIoErrors = 0;

        // ==== 重複抑制 ====
        // セッションで一度でも登録した英語原文（HashSet）
        private static readonly HashSet<string> _seenOnce = new HashSet<string>(StringComparer.Ordinal);

        // 直近の出現時刻（ms）でデバウンス。キーは英語原文
        private static readonly Dictionary<string, long> _recentSeen = new Dictionary<string, long>(StringComparer.Ordinal);
        private const int DEBOUNCE_MS = 2000; // 2秒以内の再出現は無視

        // ==== 出力バッファ（アグリゲートのデバウンス書き） ====
        private static bool _pauseAggregate = false;
        private static int _aggregateDebounceMs = 250;
        private static readonly List<string> _aggPending = new List<string>();
        private static Timer _aggTimer;

        // ==== API ====

        public static void InitIO(
            string dictTsv, string exportRoot, string exportMode, bool perMod, bool emitAggregate,
            Action<string> logInfo, Action<string> logWarn, string perModSubdir)
        {
            lock (_lock)
            {
                _dictTsv = dictTsv;
                _exportRoot = exportRoot;
                _exportMode = exportMode ?? "Both";
                _perMod = perMod;
                _emitAggregate = emitAggregate;
                _perModSubdir = string.IsNullOrEmpty(perModSubdir) ? "PerMod" : perModSubdir;

                _logInfo = logInfo ?? (s => { });
                _logWarn = logWarn ?? (s => { });

                Directory.CreateDirectory(_exportRoot);

                LoadDictionary_NoThrow(_dictTsv);

                _logInfo("TranslatorHub.InitIO OK: dict=" + _dictTsv + ", exportRoot=" + _exportRoot +
                         ", perMod=" + _perMod + ", perModSubdir=" + _perModSubdir + ", aggregate=" + _emitAggregate);

                // Agg タイマー
                _aggTimer?.Dispose();
                _aggTimer = new Timer(_ => FlushAggregateSafe(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        public static void ApplyRuntimeOptions(bool pauseAggregate, int aggregateDebounceMs, bool watchDict, string dictPath)
        {
            lock (_lock)
            {
                _pauseAggregate = pauseAggregate;
                _aggregateDebounceMs = Math.Max(0, aggregateDebounceMs);

                // 監視切替
                TryEnableDictWatcher(watchDict, dictPath);
            }
        }

        public static (long totalReplaced, long totalCollected, long totalIoErrors) GetTotals()
            => (_totalReplaced, _totalCollected, _totalIoErrors);

        public static void GetAndResetSessionStats(out long replaced, out long collected, out long ioErrors, out long excluded)
        {
            lock (_lock)
            {
                replaced = _sessionReplaced;
                collected = _sessionCollected;
                ioErrors = _sessionIoErrors;
                excluded = NoiseFilter.ExcludedCount; // 参照表示（こちらのセッション値は持たない）
                _totalReplaced += _sessionReplaced;
                _totalCollected += _sessionCollected;
                _totalIoErrors += _sessionIoErrors;
                _sessionReplaced = _sessionCollected = _sessionIoErrors = 0;
            }
        }

        public static void ClearSessionCaches(out int perModCache, out int aggregateCache)
        {
            lock (_lock)
            {
                perModCache = _seenOnce.Count;
                aggregateCache = _recentSeen.Count;
                _seenOnce.Clear();
                _recentSeen.Clear();
            }
        }

        public static void ReloadDictionary()
        {
            lock (_lock)
            {
                LoadDictionary_NoThrow(_dictTsv, verbose: true);
            }
        }

        // ==== 収集・置換のメイン入口 ====
        public static string TranslateOrEnroll(string english, string source, string scope, string modGuess = "Unknown")
        {
            if (string.IsNullOrEmpty(english)) return english;

            if (NoiseFilter.IsNoise(english)) return english;

            if (NoiseFilter.IsScreenExcluded(out var _))
            {
                return english;
            }

            if (TryTranslate(english, out var ja))
            {
                Interlocked.Increment(ref _sessionReplaced);
                return ja;
            }

            TryEnrollOnce(english, source, scope, modGuess);
            return english;
        }

        // ==== ボタンからの再構築系 ====

        public static void RebuildAggregateAndUntranslated(out int aggregateLines, out int untranslatedLines, out int modSections)
        {
            aggregateLines = untranslatedLines = modSections = 0;

            try
            {
                string allDir = Path.Combine(_exportRoot, "_All");
                Directory.CreateDirectory(allDir);

                // 1) 集約 TSV を再構築（PerMod を走査）
                var rows = new List<(string mod, string source, string scope, string text)>();
                string perRoot = Path.Combine(_exportRoot, _perModSubdir);
                if (Directory.Exists(perRoot))
                {
                    foreach (var modDir in Directory.GetDirectories(perRoot))
                    {
                        string tsv = Path.Combine(modDir, "strings_en.tsv");
                        if (!File.Exists(tsv)) continue;
                        foreach (var line in File.ReadAllLines(tsv, new UTF8Encoding(false)))
                        {
                            if (line.StartsWith("timestamp_utc")) continue;
                            var parts = line.Split('\t');
                            if (parts.Length >= 5)
                            {
                                string mod = parts[1];
                                string source = parts[2];
                                string scope = parts[3];
                                string text = parts[4];
                                rows.Add((mod, source, scope, text));
                            }
                        }
                    }
                }

                // aggregate txt
                string aggTxt = Path.Combine(allDir, "texts_en_aggregate.txt");
                string header = "# rebuilt_at=" + DateTime.UtcNow.ToString("O") + Environment.NewLine;
                File.WriteAllText(aggTxt + ".tmp", header, new UTF8Encoding(false));
                using (var sw = new StreamWriter(aggTxt + ".tmp", true, new UTF8Encoding(false)))
                    foreach (var r in rows) { sw.WriteLine(r.text); aggregateLines++; }
                ReplaceAtomically(aggTxt);

                // 2) 未訳（dictに無いものだけ）
                var dictKeys = new HashSet<string>(_dict.Keys, StringComparer.Ordinal);
                string untranslated = Path.Combine(allDir, "untranslated.txt");
                File.WriteAllText(untranslated + ".tmp", header, new UTF8Encoding(false));
                using (var sw = new StreamWriter(untranslated + ".tmp", true, new UTF8Encoding(false)))
                    foreach (var r in rows)
                        if (!dictKeys.Contains(r.text)) { sw.WriteLine(r.text); untranslatedLines++; }
                ReplaceAtomically(untranslated);

                // 3) Mod別（テキスト）
                string grouped = Path.Combine(allDir, "grouped_by_mod.txt");
                File.WriteAllText(grouped + ".tmp", header, new UTF8Encoding(false));
                using (var sw = new StreamWriter(grouped + ".tmp", true, new UTF8Encoding(false)))
                {
                    foreach (var grp in rows.GroupBy(r => r.mod).OrderBy(g => g.Key))
                    {
                        sw.WriteLine("### " + grp.Key);
                        foreach (var r in grp) sw.WriteLine(r.text);
                        sw.WriteLine();
                        modSections++;
                    }
                }
                ReplaceAtomically(grouped);
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("[Rebuild] failed: " + ex);
                Interlocked.Increment(ref _sessionIoErrors);
            }
        }

        public static bool TryBuildTsvTemplateFromUntranslated(out string path, out string error)
        {
            path = "";
            error = null;
            try
            {
                string allDir = Path.Combine(_exportRoot, "_All");
                Directory.CreateDirectory(allDir);
                string src = Path.Combine(allDir, "untranslated.txt");
                if (!File.Exists(src)) { error = "untranslated.txt not found"; return false; }

                string dictDir = Path.GetDirectoryName(_dictTsv) ?? _exportRoot;
                Directory.CreateDirectory(dictDir);
                path = Path.Combine(dictDir, "strings_ja_template.tsv");

                using (var sr = new StreamReader(src, new UTF8Encoding(false)))
                using (var sw = new StreamWriter(path + ".tmp", false, new UTF8Encoding(false)))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("#")) continue;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        sw.Write(line);
                        sw.Write('\t');
                        sw.WriteLine();
                    }
                }
                ReplaceAtomically(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Interlocked.Increment(ref _sessionIoErrors);
                return false;
            }
        }

        // ==== 内部処理 ====

        private static void LoadDictionary_NoThrow(string path, bool verbose = false)
        {
            try
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                int lines = 0, dupNorm = 0;

                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path, new UTF8Encoding(false)))
                    {
                        lines++;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (line.StartsWith("#")) continue;

                        var idx = line.IndexOf('\t');
                        if (idx <= 0) continue;

                        string en = line.Substring(0, idx);
                        string ja = idx < line.Length - 1 ? line.Substring(idx + 1) : "";

                        if (!map.ContainsKey(en))
                            map[en] = ja;
                        else
                            dupNorm++;
                    }
                }

                _dict = map;
                _logInfo?.Invoke($"Dictionary loaded: lines={lines}, entries={_dict.Count}, dupNormalized={dupNorm}");
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("Dictionary load failed: " + ex);
            }
        }

        private static void TryEnableDictWatcher(bool enable, string path)
        {
            // 既存停止
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;

            if (!enable) return;

            try
            {
                var fi = new FileInfo(path);
                var fs = new FileSystemWatcher(fi.DirectoryName ?? ".", fi.Name);
                fs.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
                fs.Changed += (_, __) => DebouncedReload();
                fs.Created += (_, __) => DebouncedReload();
                fs.Renamed += (_, __) => DebouncedReload();
                fs.EnableRaisingEvents = true;
                _watcher = fs;
                _logInfo?.Invoke("Dict watcher enabled: " + path);
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("Dict watcher failed: " + ex.Message);
            }
        }

        private static int _reloadPending = 0;
        private static void DebouncedReload()
        {
            // 簡易デバウンス：短時間に複数イベントが来ても1回だけ実行
            if (Interlocked.Exchange(ref _reloadPending, 1) == 0)
            {
                Verse.LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try
                    {
                        Thread.Sleep(150);
                        ReloadDictionary();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _reloadPending, 0);
                    }
                });
            }
        }

        private static bool TryTranslate(string en, out string ja)
        {
            lock (_lock)
            {
                return _dict.TryGetValue(en, out ja) && !string.IsNullOrEmpty(ja);
            }
        }

        private static void TryEnrollOnce(string en, string source, string scope, string modGuess)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_lock)
            {
                // 時間窓（デバウンス）
                if (_recentSeen.TryGetValue(en, out var last) && (now - last) < DEBOUNCE_MS)
                    return;
                _recentSeen[en] = now;

                // セッション重複
                if (_seenOnce.Contains(en))
                    return;
                _seenOnce.Add(en);
            }

            // ここまで来たら「初回扱い」で収集
            try
            {
                if (_perMod)
                {
                    string modDir = Path.Combine(_exportRoot, _perModSubdir, Safe(modGuess));
                    Directory.CreateDirectory(modDir);

                    if (_exportMode == "TextOnly" || _exportMode == "Both")
                    {
                        AppendLine(Path.Combine(modDir, "texts_en.txt"), en);
                    }
                    if (_exportMode == "Full" || _exportMode == "Both")
                    {
                        string tsv = Path.Combine(modDir, "strings_en.tsv");
                        if (!File.Exists(tsv))
                        {
                            WriteAll(tsv, "timestamp_utc\tmod\tsource\tscope\ttext" + Environment.NewLine);
                        }
                        AppendLine(tsv, DateTime.UtcNow.ToString("O") + "\t" + Safe(modGuess) + "\t" + Safe(source) + "\t" + Safe(scope) + "\t" + en);
                    }
                }
                else
                {
                    string cur = Path.Combine(_exportRoot, "Current");
                    Directory.CreateDirectory(cur);
                    if (_exportMode == "TextOnly" || _exportMode == "Both")
                        AppendLine(Path.Combine(cur, "texts_en.txt"), en);
                    if (_exportMode == "Full" || _exportMode == "Both")
                    {
                        string tsv = Path.Combine(cur, "strings_en.tsv");
                        if (!File.Exists(tsv))
                            WriteAll(tsv, "timestamp_utc\tmod\tsource\tscope\ttext" + Environment.NewLine);
                        AppendLine(tsv, DateTime.UtcNow.ToString("O") + "\t" + Safe(modGuess) + "\t" + Safe(source) + "\t" + Safe(scope) + "\t" + en);
                    }
                }

                // アグリゲートに軽量追記（デバウンス書き）
                if (_emitAggregate && !_pauseAggregate)
                {
                    lock (_lock) { _aggPending.Add(en); }
                    ScheduleAggregateFlush();
                }

                Interlocked.Increment(ref _sessionCollected);
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("[Enroll] IO failed: " + ex.Message);
                Interlocked.Increment(ref _sessionIoErrors);
            }
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }

        // ==== I/O helpers ====

        private static void WriteAll(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        private static void AppendLine(string path, string line)
        {
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                sw.WriteLine(line);
            }
        }

        private static void ReplaceAtomically(string finalPath)
        {
            string tmp = finalPath + ".tmp";
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmp, finalPath);
        }

        // ==== Aggregate flush ====

        private static void ScheduleAggregateFlush()
        {
            if (_aggregateDebounceMs <= 0) { FlushAggregateSafe(); return; }
            _aggTimer?.Change(_aggregateDebounceMs, Timeout.Infinite);
        }

        private static void FlushAggregateSafe()
        {
            try
            {
                List<string> buf;
                lock (_lock)
                {
                    if (_aggPending.Count == 0) return;
                    buf = new List<string>(_aggPending);
                    _aggPending.Clear();
                }

                string allDir = Path.Combine(_exportRoot, "_All");
                Directory.CreateDirectory(allDir);
                string aggTxt = Path.Combine(allDir, "texts_en_aggregate.txt");
                using (var fs = new FileStream(aggTxt, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    foreach (var s in buf) sw.WriteLine(s);
                }
            }
            catch (Exception ex)
            {
                _logWarn?.Invoke("[AggregateFlush] failed: " + ex.Message);
                Interlocked.Increment(ref _sessionIoErrors);
            }
        }
    }
}
