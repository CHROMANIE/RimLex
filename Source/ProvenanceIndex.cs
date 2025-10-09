// RimLex ProvenanceIndex.cs v0.10.0-rc6a (view-enabled)
// 機能: 恒久由来Index（Dict/Index/en_provenance.tsv）
// 追加: 人間向けビューを同時出力
//   - en_provenance_view.tsv   : first_seen_utc, last_seen_utc, count, mods, key_shape（mods ASC, key_shape ASC）
//   - en_provenance_recent.tsv : 同列。並びは last_seen_utc DESC（最近順）
//
// 既存の TSV (en_provenance.tsv) は従来どおり: key_shape, mods, first_seen_utc, last_seen_utc, count

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RimLex
{
    internal static class ProvenanceIndex
    {
        private static readonly object _lock = new object();
        private static string _indexPath;
        private static Action<string> _logInfo = _ => { };
        private static Action<string> _logWarn = _ => { };

        // 形状化: 数字→#、改行は内部では \n、保存時は /n
        private static readonly Regex RxNumbers = new Regex(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex RxSlashNLoose = new Regex(@"\s*/\s*n\s*", RegexOptions.Compiled);

        private static string NormalizeForKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            string t = s.Replace("\\n", "\n");
            t = RxSlashNLoose.Replace(t, "\n");
            return t;
        }
        private static string MakeShape(string s, out List<string> numbers)
        {
            var nums = new List<string>();
            string shaped = RxNumbers.Replace(s, m => { nums.Add(m.Value); return "#"; });
            numbers = nums;
            return shaped;
        }

        private sealed class Rec
        {
            public HashSet<string> Mods = new HashSet<string>(StringComparer.Ordinal);
            public long Count;
            public DateTime FirstSeenUtc;
            public DateTime LastSeenUtc;
        }

        private static readonly Dictionary<string, Rec> _map = new Dictionary<string, Rec>(StringComparer.Ordinal);
        private static bool _dirty = false;
        private static System.Threading.Timer _flushTimer;
        private const int FLUSH_DEBOUNCE_MS = 1500;

        public static void Init(string dictTsv, string exportRoot, Action<string> logInfo, Action<string> logWarn)
        {
            _logInfo = logInfo ?? (_ => { });
            _logWarn = logWarn ?? (_ => { });

            try
            {
                string dictDir = Path.GetDirectoryName(dictTsv) ?? exportRoot;
                string idxDir = Path.Combine(dictDir, "Index");
                Directory.CreateDirectory(idxDir);
                _indexPath = Path.Combine(idxDir, "en_provenance.tsv");
                Load_NoThrow(_indexPath);
                _logInfo("[Provenance] index at " + _indexPath + " (entries=" + _map.Count + ")");
            }
            catch (Exception ex)
            {
                _logWarn("[Provenance] init failed: " + ex.Message);
            }
        }

        public static string GetIndexPath() => _indexPath ?? "";

        public static void Register(string keyShape, string modName)
        {
            if (string.IsNullOrEmpty(keyShape)) return;
            modName = string.IsNullOrEmpty(modName) ? "Unknown" : modName;

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var key = NormalizeForKey(keyShape);
                if (!_map.TryGetValue(key, out var rec))
                {
                    rec = new Rec { FirstSeenUtc = now, LastSeenUtc = now, Count = 1 };
                    rec.Mods.Add(modName);
                    _map[key] = rec;
                }
                else
                {
                    rec.Count++;
                    rec.LastSeenUtc = now;
                    rec.Mods.Add(modName);
                }
                _dirty = true;
                DebouncedFlush();
            }
        }

        public static void FlushNow()
        {
            lock (_lock)
            {
                if (!_dirty || string.IsNullOrEmpty(_indexPath)) return;
                try
                {
                    // 正本（機械向け）
                    var lines = new List<string>();
                    lines.Add("key_shape\tmods\tfirst_seen_utc\tlast_seen_utc\tcount");
                    foreach (var kv in _map.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        var rec = kv.Value;
                        var mods = rec.Mods.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                        string row = string.Join("\t", new[] {
                            kv.Key.Replace("\n", "/n"),
                            string.Join(",", mods).Replace("\t", " "),
                            rec.FirstSeenUtc.ToString("O"),
                            rec.LastSeenUtc.ToString("O"),
                            rec.Count.ToString()
                        });
                        lines.Add(row);
                    }

                    string tmp = _indexPath + ".tmp";
                    File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                    ReplaceAtomically(_indexPath);

                    // ビュー（人間向け / 既存処理に影響なし）
                    WriteViews_NoThrow();

                    _dirty = false;
                }
                catch (Exception ex)
                {
                    _logWarn("[Provenance] flush failed: " + ex.Message);
                }
            }
        }

        public static bool RebuildFromPerMod(string exportRoot, string perModSubdir, out int keys, out int mods, out int rows, out string path, out string error)
        {
            keys = 0; mods = 0; rows = 0; path = _indexPath ?? ""; error = null;
            try
            {
                var map = new Dictionary<string, Rec>(StringComparer.Ordinal);
                string perModRoot = Path.Combine(exportRoot, perModSubdir ?? "PerMod");
                if (!Directory.Exists(perModRoot)) Directory.CreateDirectory(perModRoot);

                var modDirs = Directory.GetDirectories(perModRoot);
                var modSet = new HashSet<string>(StringComparer.Ordinal);

                foreach (var modDir in modDirs)
                {
                    string modName = Path.GetFileName(modDir);
                    modSet.Add(modName);
                    // strings_en.tsv 優先
                    string tsv = Path.Combine(modDir, "strings_en.tsv");
                    if (File.Exists(tsv))
                    {
                        foreach (var raw in File.ReadAllLines(tsv, new UTF8Encoding(false)))
                        {
                            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#")) continue;
                            var parts = raw.Split('\t');
                            if (parts.Length < 5) continue;
                            string text = parts[4];
                            rows++;
                            Add(map, text, modName);
                        }
                    }
                    // texts_en.txt も取り込む
                    string txt = Path.Combine(modDir, "texts_en.txt");
                    if (File.Exists(txt))
                    {
                        foreach (var raw in File.ReadAllLines(txt, new UTF8Encoding(false)))
                        {
                            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("#")) continue;
                            rows++;
                            Add(map, raw, modName);
                        }
                    }
                }

                lock (_lock)
                {
                    _map.Clear();
                    foreach (var kv in map) _map[kv.Key] = kv.Value;
                    _dirty = true;
                    FlushNow();
                    keys = _map.Count;
                    mods = modSet.Count;
                    path = _indexPath ?? "";
                }
                _logInfo("[Provenance] rebuilt from PerMod: keys=" + keys + " mods=" + mods + " rows=" + rows + " -> " + path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logWarn("[Provenance] rebuild failed: " + ex);
                return false;
            }
        }

        private static void Add(Dictionary<string, Rec> map, string raw, string modName)
        {
            string norm = NormalizeForKey(raw.Replace("/n", "\n"));
            var shape = MakeShape(norm, out _);
            if (!map.TryGetValue(shape, out var rec))
            {
                rec = new Rec { FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow, Count = 1 };
                rec.Mods.Add(modName);
                map[shape] = rec;
            }
            else
            {
                rec.Count++;
                rec.LastSeenUtc = DateTime.UtcNow;
                rec.Mods.Add(modName);
            }
        }

        private static void DebouncedFlush()
        {
            try
            {
                _flushTimer?.Dispose();
                _flushTimer = new System.Threading.Timer(_ => { try { FlushNow(); } catch { } }, null, FLUSH_DEBOUNCE_MS, System.Threading.Timeout.Infinite);
            }
            catch { }
        }

        private static void Load_NoThrow(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    int n = 0;
                    foreach (var raw in File.ReadAllLines(path, new UTF8Encoding(false)))
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        if (raw.StartsWith("#")) continue;
                        var parts = raw.Split('\t');
                        if (parts.Length < 5) continue;
                        string key = NormalizeForKey(parts[0].Replace("/n", "\n"));
                        var rec = new Rec();
                        foreach (var m in (parts[1] ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                            rec.Mods.Add(m.Trim());
                        if (DateTime.TryParse(parts[2], null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var fs)) rec.FirstSeenUtc = fs; else rec.FirstSeenUtc = DateTime.UtcNow;
                        if (DateTime.TryParse(parts[3], null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var ls)) rec.LastSeenUtc = ls; else rec.LastSeenUtc = rec.FirstSeenUtc;
                        if (long.TryParse(parts[4], out var c)) rec.Count = Math.Max(1, c); else rec.Count = 1;
                        _map[key] = rec; n++;
                    }
                    _logInfo("[Provenance] loaded " + n + " rows.");
                }
            }
            catch (Exception ex)
            {
                _logWarn("[Provenance] load failed: " + ex.Message);
            }
        }

        private static void ReplaceAtomically(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(path + ".tmp", path);
            }
            catch (Exception ex)
            {
                _logWarn("[Provenance] atomic replace failed: " + ex.Message);
            }
        }

        // --- 追加: ビュー出力（人間向け）。正本には影響しない ---
        private static void WriteViews_NoThrow()
        {
            try
            {
                if (string.IsNullOrEmpty(_indexPath)) return;
                string idxDir = Path.GetDirectoryName(_indexPath) ?? "";
                if (string.IsNullOrEmpty(idxDir)) return;

                // 共通ヘッダ
                string header = "first_seen_utc\tlast_seen_utc\tcount\tmods\tkey_shape";

                // 1) mods ASC, key_shape ASC
                var viewLines = new List<string>();
                viewLines.Add(header);
                foreach (var kv in _map
                             .OrderBy(k => string.Join(",", k.Value.Mods.OrderBy(x => x, StringComparer.Ordinal)), StringComparer.Ordinal)
                             .ThenBy(k => k.Key, StringComparer.Ordinal))
                {
                    var rec = kv.Value;
                    var mods = rec.Mods.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    string row = string.Join("\t", new[] {
                        rec.FirstSeenUtc.ToString("O"),
                        rec.LastSeenUtc.ToString("O"),
                        rec.Count.ToString(),
                        string.Join(",", mods).Replace("\t", " "),
                        kv.Key.Replace("\n", "/n")
                    });
                    viewLines.Add(row);
                }
                string viewPath = Path.Combine(idxDir, "en_provenance_view.tsv");
                File.WriteAllLines(viewPath + ".tmp", viewLines, new UTF8Encoding(false));
                ReplaceAtomically(viewPath);

                // 2) recent: last_seen_utc DESC
                var recentLines = new List<string>();
                recentLines.Add(header);
                foreach (var kv in _map
                             .OrderByDescending(k => k.Value.LastSeenUtc)
                             .ThenBy(k => k.Key, StringComparer.Ordinal))
                {
                    var rec = kv.Value;
                    var mods = rec.Mods.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    string row = string.Join("\t", new[] {
                        rec.FirstSeenUtc.ToString("O"),
                        rec.LastSeenUtc.ToString("O"),
                        rec.Count.ToString(),
                        string.Join(",", mods).Replace("\t", " "),
                        kv.Key.Replace("\n", "/n")
                    });
                    recentLines.Add(row);
                }
                string recentPath = Path.Combine(idxDir, "en_provenance_recent.tsv");
                File.WriteAllLines(recentPath + ".tmp", recentLines, new UTF8Encoding(false));
                ReplaceAtomically(recentPath);

                _logInfo("[Provenance] views updated: " + viewPath + " , " + recentPath);
            }
            catch (Exception ex)
            {
                _logWarn("[Provenance] write views failed: " + ex.Message);
            }
        }
    }
}

/* Key Methods Present
   - Init
   - GetIndexPath
   - Register
   - FlushNow
   - RebuildFromPerMod
   - (private) Add / DebouncedFlush / Load_NoThrow / ReplaceAtomically
   - (private) WriteViews_NoThrow   // ★追加
*/
