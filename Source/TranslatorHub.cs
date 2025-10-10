// RimLex TranslatorHub.cs v0.10.0-rc6a @2025-10-09 11:25
// 修正: RebuildAggregateAndUntranslated の out引数を try の外で初期化（CS0177対策）。
// 仕様: rc6の「/n ゆるふわ正規化」「TXT/TSVは /n 一行表記」「自己参照=設定画面のみ」そのまま。
// 追加修正(この改版):
//  - TranslateOrEnroll の処理順を「翻訳優先」に変更。
//    → ノイズ判定は未訳だったときだけ適用（動的数値でも既訳は置換される）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Verse;

namespace RimLex
{
    public static class TranslatorHub
    {
        private static string _dictTsv;
        private static string _exportRoot;
        private static string _exportMode = "Both";
        private static bool _perMod = true;
        private static bool _emitAggregate = true;
        private static string _perModSubdir = "PerMod";

        private static Action<string> _logInfo = _ => { };
        private static Action<string> _logWarn = _ => { };

        private static readonly object _lock = new object();

        private static Dictionary<string, string> _dictExact = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, string> _dictShape = new Dictionary<string, string>(StringComparer.Ordinal);
        public static int DictionaryEntryCount => _dictExact.Count + _dictShape.Count;

        private static FileSystemWatcher _watcher;

        private static long _sessionReplaced = 0;
        private static long _sessionCollected = 0;
        private static long _sessionIoErrors = 0;

        private static long _totalReplaced = 0;
        private static long _totalCollected = 0;
        private static long _totalIoErrors = 0;

        private static readonly HashSet<string> _seenOnce = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> _recentSeen = new Dictionary<string, long>(StringComparer.Ordinal);
        private const int DEBOUNCE_MS = 2000;

        private static bool _pauseAggregate = false;
        private static int _aggregateDebounceMs = 250;
        private static readonly List<string> _aggPending = new List<string>();
        private static Timer _aggTimer;

        public static void InitIO(
            string dictTsv, string exportRoot, string exportMode, bool perMod, bool emitAggregate,
            Action<string> logInfo, Action<string> logWarn, string perModSubdir)
        {
            lock (_lock)
            {
                _dictTsv = dictTsv;
                _exportRoot = exportRoot;
                _exportMode = string.IsNullOrEmpty(exportMode) ? "Both" : exportMode;
                _perMod = perMod;
                _emitAggregate = emitAggregate;
                _perModSubdir = string.IsNullOrEmpty(perModSubdir) ? "PerMod" : perModSubdir;

                _logInfo = logInfo ?? _logInfo;
                _logWarn = logWarn ?? _logWarn;

                Directory.CreateDirectory(_exportRoot);
                LoadDictionary_NoThrow(_dictTsv);
                ProvenanceIndex.Init(_dictTsv, _logInfo, _logWarn, () => Interlocked.Increment(ref _sessionIoErrors));

                _aggTimer?.Dispose();
                _aggTimer = new Timer(_ => FlushAggregateSafe(), null, Timeout.Infinite, Timeout.Infinite);

                _logInfo("TranslatorHub.InitIO OK: dict=" + _dictTsv + ", exportRoot=" + _exportRoot +
                         ", perMod=" + _perMod + ", perModSubdir=" + _perModSubdir + ", aggregate=" + _emitAggregate);
            }
        }

        public static void ApplyRuntimeOptions(bool pauseAggregate, int aggregateDebounceMs, bool watchDict, string dictPath)
        {
            lock (_lock)
            {
                _pauseAggregate = pauseAggregate;
                _aggregateDebounceMs = Math.Max(0, aggregateDebounceMs);
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
                excluded = NoiseFilter.ExcludedCount;
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
            lock (_lock) LoadDictionary_NoThrow(_dictTsv, verbose: true);
        }

        public static string TranslateOrEnroll(string english, string source, string scope, string modGuess = "Unknown")
        {
            if (string.IsNullOrEmpty(english)) return english;

            if (IsSelfSettingsCall()) return english;
            if (NoiseFilter.IsScreenExcluded(out _)) return english;

            string normalized = TextShapeUtil.NormalizeForKey(english);
            var shapeParts = TextShapeUtil.MakeShape(normalized);
            string resolvedMod = ResolveModName(modGuess);
            string modName = SanitizeModName(resolvedMod);

            bool isNoise = NoiseFilter.IsNoise(english);
            if (!isNoise)
                ProvenanceIndex.Register(shapeParts.Shape, modName);

            if (TryTranslateExact(normalized, out var ja) || TryTranslateByShape(normalized, shapeParts, out ja))
            {
                Interlocked.Increment(ref _sessionReplaced);
                return ja;
            }

            if (!isNoise)
                TryEnrollOnce(normalized, source, scope, modName, shapeParts);

            return english;
        }

        public static void RebuildAggregateAndUntranslated(out int aggregateLines, out int untranslatedLines, out int modSections)
        {
            aggregateLines = 0;
            untranslatedLines = 0;
            modSections = 0;

            try
            {
                string allDir = Path.Combine(_exportRoot, "_All");
                Directory.CreateDirectory(allDir);

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
                                string sourceId = parts[2];
                                string scopeId = parts[3];
                                string text = parts[4].Replace("/n", "\n");
                                rows.Add((mod, sourceId, scopeId, text));
                            }
                        }
                    }
                }

                string aggTxt = Path.Combine(allDir, "texts_en_aggregate.txt");
                string header = "# rebuilt_at=" + DateTime.UtcNow.ToString("O") + Environment.NewLine;
                File.WriteAllText(aggTxt + ".tmp", header, new UTF8Encoding(false));
                using (var sw = new StreamWriter(aggTxt + ".tmp", true, new UTF8Encoding(false)))
                    foreach (var r in rows) { sw.WriteLine(r.text.Replace("\n", "/n")); aggregateLines++; }
                ReplaceAtomically(aggTxt);

                var dictKeys = new HashSet<string>(_dictExact.Keys.Concat(_dictShape.Keys), StringComparer.Ordinal);
                string untranslated = Path.Combine(allDir, "untranslated.txt");
                File.WriteAllText(untranslated + ".tmp", header, new UTF8Encoding(false));
                using (var sw = new StreamWriter(untranslated + ".tmp", true, new UTF8Encoding(false)))
                {
                    foreach (var r in rows)
                    {
                        var normalized = TextShapeUtil.NormalizeForKey(r.text);
                        var shp = TextShapeUtil.MakeShape(normalized);
                        if (!dictKeys.Contains(normalized) && !dictKeys.Contains(shp.Shape))
                        { sw.WriteLine(r.text.Replace("\n", "/n")); untranslatedLines++; }
                    }
                }
                ReplaceAtomically(untranslated);

                string grouped = Path.Combine(allDir, "grouped_by_mod.txt");
                File.WriteAllText(grouped + ".tmp", header, new UTF8Encoding(false));
                using (var sw = new StreamWriter(grouped + ".tmp", true, new UTF8Encoding(false)))
                {
                    foreach (var grp in rows.GroupBy(r => r.mod).OrderBy(g => g.Key))
                    {
                        sw.WriteLine("### " + grp.Key);
                        foreach (var r in grp) sw.WriteLine(r.text.Replace("\n", "/n"));
                        sw.WriteLine();
                        modSections++;
                    }
                }
                ReplaceAtomically(grouped);
            }
            catch (Exception ex)
            {
                _logWarn("[Rebuild] failed: " + ex);
                Interlocked.Increment(ref _sessionIoErrors);
            }
        }

        public static bool TryBuildTsvTemplateFromUntranslated(out string path, out string error)
        {
            path = ""; error = null;
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
                        sw.Write(line.Replace("\n", "/n"));
                        sw.Write('\t'); sw.WriteLine();
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

        public static bool TryRebuildProvenanceIndex(out int keyCount, out int modCount, out string error)
        {
            string exportRoot;
            string perModSubdir;
            lock (_lock)
            {
                exportRoot = _exportRoot;
                perModSubdir = _perModSubdir;
            }
            return ProvenanceIndex.TryRebuildFromPerMod(exportRoot, perModSubdir, out keyCount, out modCount, out error);
        }

        private static void LoadDictionary_NoThrow(string path, bool verbose = false)
        {
            try
            {
                var exact = new Dictionary<string, string>(StringComparer.Ordinal);
                var shape = new Dictionary<string, string>(StringComparer.Ordinal);
                int lines = 0, exactN = 0, shapeN = 0, dup = 0;

                if (File.Exists(path))
                {
                    foreach (var raw in File.ReadAllLines(path, new UTF8Encoding(false)))
                    {
                        lines++;
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        if (raw.StartsWith("#")) continue;

                        int idx = raw.IndexOf('\t');
                        if (idx <= 0) continue;

                        string en = TextShapeUtil.NormalizeForKey(raw.Substring(0, idx).Replace("/n", "\n"));
                        string ja = TextShapeUtil.NormalizeForKey(raw.Substring(idx + 1));

                        if (en.IndexOf('#') >= 0)
                        { if (!shape.ContainsKey(en)) { shape[en] = ja; shapeN++; } else dup++; }
                        else
                        { if (!exact.ContainsKey(en)) { exact[en] = ja; exactN++; } else dup++; }
                    }
                }

                _dictExact = exact; _dictShape = shape;
                _logInfo($"Dictionary loaded: lines={lines}, exact={exactN}, shape={shapeN}, dupNormalized={dup}");
            }
            catch (Exception ex)
            {
                _logWarn("Dictionary load failed: " + ex);
            }
        }

        private static void TryEnableDictWatcher(bool enable, string path)
        {
            try { _watcher?.Dispose(); } catch { }
            _watcher = null; if (!enable) return;

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
                _logInfo("Dict watcher enabled: " + path);
            }
            catch (Exception ex)
            {
                _logWarn("Dict watcher failed: " + ex.Message);
            }
        }

        private static int _reloadPending = 0;
        private static void DebouncedReload()
        {
            if (Interlocked.Exchange(ref _reloadPending, 1) == 0)
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try { Thread.Sleep(150); ReloadDictionary(); }
                    finally { Interlocked.Exchange(ref _reloadPending, 0); }
                });
            }
        }

        private static bool TryTranslateExact(string key, out string ja)
        {
            string norm = TextShapeUtil.NormalizeForKey(key);
            lock (_lock) return _dictExact.TryGetValue(norm, out ja) && !string.IsNullOrEmpty(ja);
        }

        private static bool TryTranslateByShape(string normalizedKey, TextShapeUtil.ShapeParts shapeParts, out string ja)
        {
            ja = null;
            var parts = shapeParts ?? TextShapeUtil.MakeShape(TextShapeUtil.NormalizeForKey(normalizedKey));

            string tmpl;
            lock (_lock)
            {
                if (!_dictShape.TryGetValue(parts.Shape, out tmpl)) return false;
            }

            int i = 0;
            var sb = new StringBuilder();
            foreach (char ch in tmpl)
            {
                if (ch == '#') sb.Append(i < parts.Numbers.Count ? parts.Numbers[i++] : "#");
                else sb.Append(ch);
            }
            ja = sb.ToString();
            return true;
        }

        private static void TryEnrollOnce(string normalizedKey, string source, string scope, string modName, TextShapeUtil.ShapeParts shapeParts)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_lock)
            {
                if (_recentSeen.TryGetValue(normalizedKey, out var last) && (now - last) < DEBOUNCE_MS) return;
                _recentSeen[normalizedKey] = now;
                if (_seenOnce.Contains(normalizedKey)) return;
                _seenOnce.Add(normalizedKey);
            }

            try
            {
                var shp = shapeParts ?? TextShapeUtil.MakeShape(normalizedKey);
                string keyForExport = shp.Shape;
                if (string.IsNullOrEmpty(keyForExport)) return;
                string keyTxt = keyForExport.Replace("\n", "/n");

                string safeMod = Safe(modName);
                if (string.IsNullOrEmpty(safeMod)) safeMod = "Unknown";

                if (_perMod)
                {
                    string modDir = Path.Combine(_exportRoot, _perModSubdir, safeMod);
                    Directory.CreateDirectory(modDir);

                    if (_exportMode == "TextOnly" || _exportMode == "Both")
                        AppendLine(Path.Combine(modDir, "texts_en.txt"), keyTxt);

                    if (_exportMode == "Full" || _exportMode == "Both")
                    {
                        string tsv = Path.Combine(modDir, "strings_en.tsv");
                        if (!File.Exists(tsv))
                            WriteAll(tsv, "timestamp_utc\tmod\tsource\tscope\ttext" + Environment.NewLine);
                        AppendLine(tsv, DateTime.UtcNow.ToString("O") + "\t" + safeMod + "\t" + Safe(source) + "\t" + Safe(scope) + "\t" + TextShapeUtil.ForTsv(keyForExport));
                    }
                }
                else
                {
                    string cur = Path.Combine(_exportRoot, "Current");
                    Directory.CreateDirectory(cur);

                    if (_exportMode == "TextOnly" || _exportMode == "Both")
                        AppendLine(Path.Combine(cur, "texts_en.txt"), keyTxt);

                    if (_exportMode == "Full" || _exportMode == "Both")
                    {
                        string tsv = Path.Combine(cur, "strings_en.tsv");
                        if (!File.Exists(tsv))
                            WriteAll(tsv, "timestamp_utc\tmod\tsource\tscope\ttext" + Environment.NewLine);
                        AppendLine(tsv, DateTime.UtcNow.ToString("O") + "\t" + safeMod + "\t" + Safe(source) + "\t" + Safe(scope) + "\t" + TextShapeUtil.ForTsv(keyForExport));
                    }
                }

                if (_emitAggregate && !_pauseAggregate)
                {
                    lock (_lock) { _aggPending.Add(keyForExport); }
                    ScheduleAggregateFlush();
                }

                Interlocked.Increment(ref _sessionCollected);
            }
            catch (Exception ex)
            {
                _logWarn("[Enroll] IO failed: " + ex.Message);
                Interlocked.Increment(ref _sessionIoErrors);
            }
        }

        private static string ResolveModName(string modGuess)
        {
            if (!string.IsNullOrWhiteSpace(modGuess) && !string.Equals(modGuess, "Unknown", StringComparison.OrdinalIgnoreCase))
                return modGuess;
            string guessed = GuessModFromStack();
            return string.IsNullOrWhiteSpace(guessed) ? "Unknown" : guessed;
        }

        private static string SanitizeModName(string modName)
        {
            string cleaned = Safe(modName);
            cleaned = string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned.Trim();
            return string.IsNullOrEmpty(cleaned) ? "Unknown" : cleaned;
        }

        private static string GuessModFromStack()
        {
            try
            {
                var st = new StackTrace(2, false);
                string fallback = null;

                for (int i = 0; i < st.FrameCount; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    var asm = m?.DeclaringType?.Assembly;
                    if (asm == null) continue;

                    string an = asm.GetName().Name ?? "";
                    if (an.StartsWith("RimLex", StringComparison.OrdinalIgnoreCase)) continue;
                    if (an.StartsWith("Verse", StringComparison.OrdinalIgnoreCase)) { fallback ??= "Unknown(Verse)"; continue; }
                    if (an.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)) { fallback ??= "Unknown(Unity)"; continue; }
                    if (an.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase) || an.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase)) { fallback ??= "Unknown(Harmony)"; continue; }

                    foreach (var mcp in LoadedModManager.RunningMods)
                    {
                        if (mcp.assemblies?.loadedAssemblies != null)
                        {
                            foreach (var la in mcp.assemblies.loadedAssemblies)
                            {
                                if (string.Equals(la.GetName().Name, an, StringComparison.OrdinalIgnoreCase))
                                    return mcp.Name ?? mcp.PackageId ?? an;
                            }
                        }
                        string id = (mcp.PackageId ?? "").ToLowerInvariant();
                        if (!string.IsNullOrEmpty(id) && id.Contains(an.ToLowerInvariant()))
                            return mcp.Name ?? mcp.PackageId ?? an;
                    }

                    fallback ??= "Unknown(" + an + ")";
                }

                return fallback ?? "Unknown";
            }
            catch { }
            return "Unknown";
        }

        private static bool IsSelfSettingsCall()
        {
            try
            {
                var st = new StackTrace(2, false);
                for (int i = 0; i < Math.Min(st.FrameCount, 16); i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    var t = m?.DeclaringType;
                    if (t == null) continue;

                    if (t.FullName == "RimLex.ModInitializer" && m.Name == "DoSettingsWindowContents")
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static string Safe(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

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
                sw.WriteLine(line);
        }

        private static void ReplaceAtomically(string finalPath)
        {
            string tmp = finalPath + ".tmp";
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmp, finalPath);
        }

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
                    foreach (var s in buf) sw.WriteLine(s.Replace("\n", "/n"));
                }
            }
            catch (Exception ex)
            {
                _logWarn("[AggregateFlush] failed: " + ex.Message);
                Interlocked.Increment(ref _sessionIoErrors);
            }
        }
    }
}
