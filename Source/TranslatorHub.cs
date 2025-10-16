// RimLex TranslatorHub.cs v0.10.0-rc6a @2025-10-16
// 変更履歴（抜粋）:
// - 2025-10-09: CS0177対策（out初期化）、/n・# 既存仕様の維持
// - 2025-10-12: 自分名義(RimLex)のPer-Mod出力をUnknownへ寄せるサニタイズ
// - 2025-10-16: PerMod推定強化（Unknown落ち対策）
//   * Stack上の Assembly と RunningMods.loadedAssemblies を ReferenceEquals / Location一致で照合
//   * Mod Name / PackageId / Namespace 断片によるヒューリスティックを追加
//   * RootDir が string/DirectoryInfo の両方に対応
// - 2025-10-16: ★置換の確実化（今回の修正）
//   * TranslateOrEnroll の順序を「除外→翻訳→（未ヒットなら）ノイズ判定→Enroll」に変更（維持）
//   * 改行を含むツールチップに対して「行ごと置換フォールバック」を追加（維持）
//   * コロン前後の空白ゆらぎを正規化（":#", " : #", " :#"… を ": " に統一）（維持）
//   * ★NormalizeForKey() に Trim + 連続スペース圧縮を追加（先頭/複スペースの揺らぎ吸収）
//   * ForTsv() のタイポ修正: replace → Replace（維持）
//
// 既存の収集・UI・/n・# 仕様は不変。最小侵襲での修正。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Verse;

namespace RimLex
{
    public static class TranslatorHub
    {
        // ==== 基本設定・状態 ====
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

        // ==== 正規化のための正規表現 ====
        private static readonly Regex RxNumbers = new Regex(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex RxSlashNLoose = new Regex(@"\s*/\s*n\s*", RegexOptions.Compiled);
        private static readonly Regex RxColonSpaces = new Regex(@"\s*:\s*", RegexOptions.Compiled);
        // ★追加：複数連続スペース（タブ含む）を1つに圧縮
        private static readonly Regex RxMultiSpaces = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);

        // ==== 正規化ユーティリティ ====
        private static string NormalizeNewlines(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            string t = s.Replace("\\n", "\n");      // 文字列としての \n → 実改行
            t = RxSlashNLoose.Replace(t, "\n");     // "/n" バリエーション → 実改行
            return t;
        }

        private sealed class ShapeParts
        {
            public string Shape;
            public List<string> Numbers;
        }
        private static ShapeParts MakeShape(string s)
        {
            var nums = new List<string>();
            string shaped = RxNumbers.Replace(s, m => { nums.Add(m.Value); return "#"; });
            return new ShapeParts { Shape = shaped, Numbers = nums };
        }

        // ★強化：Trim + 連続スペース圧縮を追加
        private static string NormalizeForKey(string s)
        {
            string t = NormalizeNewlines((s ?? "").Replace("\r\n", "\n").Replace("\r", "\n"));
            t = RxColonSpaces.Replace(t, ": ");   // " :#" → ": "
            t = RxMultiSpaces.Replace(t, " ");    // "  "やタブ→" "
            t = t.Trim();                          // 先頭・末尾スペース除去
            return t;
        }

        private static string ForTsv(string s)
            => (s ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", "/n");

        // ==== 初期化・オプション ====
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

                _sessionReplaced = 0;
                _sessionCollected = 0;
                _sessionIoErrors = 0;
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

        // ==== 翻訳本体（翻訳→行別フォールバック→ノイズ判定→Enroll） ====
        public static string TranslateOrEnroll(string english, string source, string scope, string modGuess = "Unknown")
        {
            if (string.IsNullOrEmpty(english)) return english;

            // 自身の設定画面や除外画面は対象外
            if (IsSelfSettingsCall()) return english;
            if (NoiseFilter.IsScreenExcluded(out _)) return english;

            // 1) 翻訳（完全一致→形状一致）
            string key = NormalizeForKey(english);
            if (TryTranslateExact(key, out var ja) || TryTranslateByShape(key, out ja))
            {
                Interlocked.Increment(ref _sessionReplaced);
                return ja;
            }

            // 1b) 多行ツールチップは行ごとフォールバック翻訳（Enrollはしない）
            if (LooksLikeTooltip(source, scope) && key.IndexOf('\n') >= 0)
            {
                var (hit, joined) = TryTranslatePerLine(key);
                if (hit)
                {
                    Interlocked.Increment(ref _sessionReplaced);
                    return joined;
                }
            }

            // 2) 未ヒット時のみノイズ判定
            if (NoiseFilter.IsNoise(english)) return english;

            // 3) 未ヒット・非ノイズ → 収集（PerMod推定あり）
            TryEnrollOnce(key, source, scope, modGuess);
            return english;
        }

        private static bool LooksLikeTooltip(string source, string scope)
        {
            if (!string.IsNullOrEmpty(scope) && scope.IndexOf("tooltip", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(source) && source.IndexOf("tooltip", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static (bool hit, string joined) TryTranslatePerLine(string text)
        {
            var lines = text.Split('\n');
            bool any = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (string.IsNullOrEmpty(ln)) continue;

                string j;
                if (TryTranslateExact(ln, out j) || TryTranslateByShape(ln, out j))
                {
                    lines[i] = j;
                    any = true;
                }
            }
            return (any, string.Join("\n", lines));
        }

        // ==== 集約・テンプレート生成 ====
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
                                string source = parts[2];
                                string scope = parts[3];
                                string text = parts[4].Replace("/n", "\n"); // 内部は \n
                                rows.Add((mod, source, scope, text));
                            }
                        }
                    }
                }

                // texts_en_aggregate.txt（追記ではなく再構築）
                string aggTxt = Path.Combine(allDir, "texts_en_aggregate.txt");
                string header = "# rebuilt_at=" + DateTime.UtcNow.ToString("O") + Environment.NewLine;
                File.WriteAllText(aggTxt + ".tmp", header, new UTF8Encoding(false));
                using (var sw = new StreamWriter(aggTxt + ".tmp", true, new UTF8Encoding(false)))
                    foreach (var r in rows) { sw.WriteLine(r.text.Replace("\n", "/n")); aggregateLines++; }
                ReplaceAtomically(aggTxt);

                // untranslated.txt（辞書未ヒットのみ）
                var dictKeys = new HashSet<string>(_dictExact.Keys.Concat(_dictShape.Keys), StringComparer.Ordinal);
                string untranslated = Path.Combine(allDir, "untranslated.txt");
                File.WriteAllText(untranslated + ".tmp", header, new UTF8Encoding(false));
                using (var sw = new StreamWriter(untranslated + ".tmp", true, new UTF8Encoding(false)))
                {
                    foreach (var r in rows)
                    {
                        var shp = MakeShape(r.text);
                        if (!dictKeys.Contains(r.text) && !dictKeys.Contains(shp.Shape))
                        { sw.WriteLine(r.text.Replace("\n", "/n")); untranslatedLines++; }
                    }
                }
                ReplaceAtomically(untranslated);

                // grouped_by_mod.txt
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

        // ==== 辞書ロード・監視 ====
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

                        string en = NormalizeForKey(raw.Substring(0, idx).Replace("/n", "\n"));
                        string ja = NormalizeForKey(raw.Substring(idx + 1));

                        if (en.IndexOf('#') >= 0)
                        {
                            if (!shape.ContainsKey(en)) { shape[en] = ja; shapeN++; } else dup++;
                        }
                        else
                        {
                            if (!exact.ContainsKey(en)) { exact[en] = ja; exactN++; } else dup++;
                        }
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

        // ==== 照合・置換 ====
        private static bool TryTranslateExact(string key, out string ja)
        {
            key = NormalizeForKey(key);
            lock (_lock) return _dictExact.TryGetValue(key, out ja) && !string.IsNullOrEmpty(ja);
        }

        private static bool TryTranslateByShape(string key, out string ja)
        {
            key = NormalizeForKey(key);
            ja = null;
            var shp = MakeShape(key);

            string tmpl;
            lock (_lock)
            {
                if (!_dictShape.TryGetValue(shp.Shape, out tmpl)) return false;
            }

            int i = 0;
            var sb = new StringBuilder();
            foreach (char ch in tmpl)
            {
                if (ch == '#') sb.Append(i < shp.Numbers.Count ? shp.Numbers[i++] : "#");
                else sb.Append(ch);
            }
            ja = sb.ToString();
            return true;
        }

        // ==== 収集（PerMod推定・アグリゲート）====
        private static bool LooksLikeSelfMod(string name)
            => !string.IsNullOrEmpty(name) && name.IndexOf("rimlex", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string SanitizeModBucket(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
                return "Unknown";
            if (LooksLikeSelfMod(name))
                return "Unknown";
            return name.Trim();
        }

        private static readonly string[] _ignoreAsmPrefixes = new[] { "RimLex", "Verse", "Unity", "UnityEngine", "Assembly-CSharp", "Harmony", "0Harmony", "System" };
        private static readonly HashSet<string> _warnedAsm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string GuessModFromStack()
        {
            try
            {
                var st = new StackTrace(2, false);
                string fallback = null;

                int depth = Math.Min(st.FrameCount, 48);
                for (int i = 0; i < depth; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    var t = m?.DeclaringType;
                    var asm = t?.Assembly;
                    if (asm == null) continue;

                    string an = asm.GetName().Name ?? "";

                    if (_ignoreAsmPrefixes.Any(p => an.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (an.StartsWith("Verse", StringComparison.OrdinalIgnoreCase)) fallback ??= "Unknown(Verse)";
                        else if (an.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) || an.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase)) fallback ??= "Unknown(Unity)";
                        else if (an.StartsWith("Harmony", StringComparison.OrdinalIgnoreCase) || an.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase)) fallback ??= "Unknown(Harmony)";
                        else if (an.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) fallback ??= "Unknown(Assembly-CSharp)";
                        continue;
                    }

                    if (TryResolveModFromAssembly(asm, out var modName))
                        return modName;

                    var hint = (t?.Namespace ?? "") + " " + an;
                    var guess = TryMatchByHint(hint);
                    if (guess != null) return guess;

                    fallback ??= "Unknown(" + an + ")";

                    if (_warnedAsm.Add(an))
                        _logWarn("GuessMod: unresolved assembly=" + an);
                }

                return fallback ?? "Unknown";
            }
            catch { }
            return "Unknown";
        }

        private static bool TryResolveModFromAssembly(Assembly asm, out string modName)
        {
            modName = null;
            try
            {
                string asmName = asm.GetName().Name ?? "";
                string asmLoc = CanonicalPath(SafePath(asm.Location));

                foreach (var mcp in LoadedModManager.RunningMods)
                {
                    var las = mcp.assemblies?.loadedAssemblies;
                    if (las != null)
                    {
                        foreach (var la in las)
                        {
                            if (object.ReferenceEquals(la, asm))
                            { modName = mcp.Name ?? mcp.PackageId ?? asmName; return true; }

                            string laLoc = CanonicalPath(SafePath(la.Location));
                            if (!string.IsNullOrEmpty(asmLoc) && !string.IsNullOrEmpty(laLoc) &&
                                string.Equals(asmLoc, laLoc, StringComparison.OrdinalIgnoreCase))
                            { modName = mcp.Name ?? mcp.PackageId ?? asmName; return true; }
                        }
                    }

                    string rootPathRaw = TryGetModRootPath(mcp);
                    if (!string.IsNullOrEmpty(rootPathRaw) && !string.IsNullOrEmpty(asmLoc))
                    {
                        string rootPath = CanonicalPath(SafePath(rootPathRaw));
                        if (!string.IsNullOrEmpty(rootPath) &&
                            asmLoc.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                        { modName = mcp.Name ?? mcp.PackageId ?? asmName; return true; }
                    }
                }
            }
            catch { }
            return false;
        }

        private static string TryGetModRootPath(ModContentPack mcp)
        {
            try
            {
                var t = mcp.GetType();

                var p = t.GetProperty("RootDir", BindingFlags.Public | BindingFlags.Instance);
                if (p != null)
                {
                    var v = p.GetValue(mcp, null);
                    if (v is DirectoryInfo di) return di.FullName;
                    if (v is string s && !string.IsNullOrWhiteSpace(s)) return s;
                }

                p = t.GetProperty("RootDirPath", BindingFlags.Public | BindingFlags.Instance);
                if (p != null)
                {
                    var v = p.GetValue(mcp, null) as string;
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }
            catch { }
            return null;
        }

        private static string TryMatchByHint(string hint)
        {
            if (string.IsNullOrEmpty(hint)) return null;
            string h = hint.ToLowerInvariant();

            foreach (var mcp in LoadedModManager.RunningMods)
            {
                string name = (mcp.Name ?? "").ToLowerInvariant();
                string pid = (mcp.PackageId ?? "").ToLowerInvariant();

                if ((!string.IsNullOrEmpty(name) && h.Contains(name)) ||
                    (!string.IsNullOrEmpty(pid) && h.Contains(pid)))
                    return mcp.Name ?? mcp.PackageId ?? null;

                foreach (var tok in Tokenize(name).Concat(Tokenize(pid)))
                    if (tok.Length >= 3 && h.Contains(tok))
                        return mcp.Name ?? mcp.PackageId ?? null;
            }
            return null;
        }

        private static IEnumerable<string> Tokenize(string s)
        {
            if (string.IsNullOrEmpty(s)) yield break;
            var buf = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) buf.Append(char.ToLowerInvariant(c));
                else { if (buf.Length > 0) { yield return buf.ToString(); buf.Clear(); } }
            }
            if (buf.Length > 0) yield return buf.ToString();
        }

        private static string CanonicalPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            try { return Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/'); }
            catch { return p.Replace('\\', '/'); }
        }
        private static string SafePath(string p) => p ?? "";

        private static void TryEnrollOnce(string keyRaw, string source, string scope, string modGuess)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_lock)
            {
                if (_recentSeen.TryGetValue(keyRaw, out var last) && (now - last) < DEBOUNCE_MS) return;
                _recentSeen[keyRaw] = now;
                if (_seenOnce.Contains(keyRaw)) return;
                _seenOnce.Add(keyRaw);
            }

            try
            {
                var shp = MakeShape(NormalizeForKey(keyRaw));
                string keyForExport = shp.Shape;
                string keyTxt = keyForExport.Replace("\n", "/n");

                string modName = (modGuess != null && modGuess != "Unknown") ? modGuess : GuessModFromStack() ?? "Unknown";
                modName = SanitizeModBucket(modName);

                if (_perMod)
                {
                    string modDir = Path.Combine(_exportRoot, _perModSubdir, Safe(modName));
                    Directory.CreateDirectory(modDir);

                    if (_exportMode == "TextOnly" || _exportMode == "Both")
                        AppendLine(Path.Combine(modDir, "texts_en.txt"), keyTxt);

                    if (_exportMode == "Full" || _exportMode == "Both")
                    {
                        string tsv = Path.Combine(modDir, "strings_en.tsv");
                        if (!File.Exists(tsv))
                            WriteAll(tsv, "timestamp_utc\tmod\tsource\tscope\ttext" + Environment.NewLine);
                        AppendLine(tsv, DateTime.UtcNow.ToString("O") + "\t" + Safe(modName) + "\t" + Safe(source) + "\t" + Safe(scope) + "\t" + ForTsv(keyForExport));
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
                        AppendLine(tsv, DateTime.UtcNow.ToString("O") + "\t" + Safe(modName) + "\t" + Safe(source) + "\t" + Safe(scope) + "\t" + ForTsv(keyForExport));
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

        // ==== 自己参照の検出 ====
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

        // ==== I/O helpers ====
        private static string Safe(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");

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
