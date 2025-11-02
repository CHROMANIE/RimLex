// RimLex ProvenanceIndex.cs v0.10.0-rc6a (v4 step-1)
// New file: Persistent provenance index for English key-shapes.
// Purpose: Dict/Index/en_provenance.tsv ← key_shape → {mods, first_seen, last_seen, count}
// Notes:
// - Atomic write, lock-protected.
// - Minimal dependencies; no changes to existing pipeline required until wired.
// - DateTime stored as UTC ISO8601 ("yyyy-MM-ddTHH:mm:ssZ").
//
// Delivery protocol: one file, full text. Key Methods listed at end.
// Stability: does not modify existing collection/option UI.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RimLex
{
    public sealed class ProvenanceIndex
    {
        // Singleton (lazy)
        private static readonly Lazy<ProvenanceIndex> _inst = new Lazy<ProvenanceIndex>(() => new ProvenanceIndex());
        public static ProvenanceIndex Instance => _inst.Value;

        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private string _indexPath;
        private bool _dirty;
        private DateTime _lastFlushUtc = DateTime.MinValue;
        private int _debounceMs = 500;

        private ProvenanceIndex() { }

        // Data model
        public sealed class Entry
        {
            public string KeyShape;
            public HashSet<string> Mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public DateTime FirstSeenUtc;
            public DateTime LastSeenUtc;
            public long Count;

            public static Entry Create(string keyShape, string mod, DateTime nowUtc)
            {
                var e = new Entry
                {
                    KeyShape = keyShape ?? "",
                    FirstSeenUtc = nowUtc,
                    LastSeenUtc = nowUtc,
                    Count = 1
                };
                if (!string.IsNullOrEmpty(mod)) e.Mods.Add(mod);
                return e;
            }
        }

        // Regex for normalization/shaping (mirrors rc6a behavior loosely)
        private static readonly Regex RxSlashNLoose = new Regex(@"/\s*n", RegexOptions.Compiled);
        private static readonly Regex RxNumbers = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex RxWhitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex RxCjk = new Regex(@"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]", RegexOptions.Compiled);

        // Initialize with default path: <ModDir>/Dict/Index/en_provenance.tsv
        public void Initialize(string indexPath = null, int debounceMs = 500)
        {
            lock (_lock)
            {
                _debounceMs = Math.Max(100, debounceMs);
                if (string.IsNullOrEmpty(indexPath))
                {
                    try
                    {
                        var md = (ModInitializer.ModDir ?? "").Replace('\\', '/');
                        _indexPath = Path.Combine(md, "Dict", "Index", "en_provenance.tsv");
                    }
                    catch
                    {
                        _indexPath = "en_provenance.tsv"; // fallback (cwd)
                    }
                }
                else
                {
                    _indexPath = indexPath;
                }

                try { Directory.CreateDirectory(Path.GetDirectoryName(_indexPath) ?? "."); } catch { }
                LoadNoThrow();
            }
        }

        // Normalize & shape to key_shape (loosely mirrors Normalizer rc6a)
        public static string ToKeyShape(string s)
        {
            s = s ?? "";
            // unify \r\n
            s = s.Replace("\r\n", "\n");
            // literal "\n" -> newline
            s = s.Replace("\\n", "\n");
            // "/ n" variations -> newline
            s = RxSlashNLoose.Replace(s, "\n");
            // collapse whitespace
            s = RxWhitespace.Replace(s, " ").Trim();
            // skip if CJK-dominant (do not shape; return as-is)
            try
            {
                int cjk = RxCjk.Matches(s).Count;
                if (cjk >= Math.Max(1, s.Length / 3)) return s; // treat as non-English
            }
            catch { }
            // replace numbers with '#'
            s = RxNumbers.Replace(s, "#");
            return s;
        }

        public void Update(string keyShape, string mod, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(keyShape)) return;
            utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            lock (_lock)
            {
                if (!_map.TryGetValue(keyShape, out var e))
                {
                    e = Entry.Create(keyShape, mod, utcNow);
                    _map[keyShape] = e;
                }
                else
                {
                    if (!string.IsNullOrEmpty(mod)) e.Mods.Add(mod);
                    e.LastSeenUtc = utcNow;
                    e.Count++;
                }
                _dirty = true;
            }
        }

        public void FlushIfDue(bool force = false)
        {
            lock (_lock)
            {
                if (!_dirty && !force) return;
                var now = DateTime.UtcNow;
                if (!force && (now - _lastFlushUtc).TotalMilliseconds < _debounceMs) return;
                SaveNoThrow();
                _lastFlushUtc = now;
                _dirty = false;
            }
        }

        public void RebuildFromPerMod(string perModRoot)
        {
            if (string.IsNullOrEmpty(perModRoot) || !Directory.Exists(perModRoot))
            {
                ModInitializer.LogWarn("ProvenanceIndex: PerMod root not found: " + perModRoot);
                return;
            }
            lock (_lock)
            {
                _map.Clear();
                _dirty = true;

                foreach (var modDir in Directory.GetDirectories(perModRoot))
                {
                    string modName = Path.GetFileName(modDir);
                    // Prefer TSV (rich), fall back to texts_en.txt
                    var tsv = Path.Combine(modDir, "strings_en.tsv");
                    var txt = Path.Combine(modDir, "texts_en.txt");
                    if (File.Exists(tsv))
                    {
                        foreach (var line in SafeReadLines(tsv))
                        {
                            // Expect: timestamp_utc\tmod\tsource\tscope\ttext
                            var parts = SplitTsv(line, 5);
                            if (parts.Length < 5) continue;
                            var text = parts[4] ?? "";
                            var ks = ToKeyShape(text);
                            if (string.IsNullOrWhiteSpace(ks)) continue;
                            Update(ks, modName, DateTime.UtcNow);
                        }
                    }
                    else if (File.Exists(txt))
                    {
                        foreach (var raw in SafeReadLines(txt))
                        {
                            var ks = ToKeyShape(raw);
                            if (string.IsNullOrWhiteSpace(ks)) continue;
                            Update(ks, modName, DateTime.UtcNow);
                        }
                    }
                }
                SaveNoThrow();
                ModInitializer.LogInfo("ProvenanceIndex: rebuild complete. entries=" + _map.Count);
            }
        }

        private void LoadNoThrow()
        {
            try
            {
                if (!File.Exists(_indexPath)) return;
                int n = 0;
                foreach (var line in SafeReadLines(_indexPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("#")) continue;
                    var parts = SplitTsv(line, 5);
                    if (parts.Length < 5) continue;
                    var e = new Entry
                    {
                        KeyShape = parts[0],
                        FirstSeenUtc = ParseUtc(parts[2]),
                        LastSeenUtc = ParseUtc(parts[3]),
                        Count = ParseLong(parts[4])
                    };
                    foreach (var m in (parts[1] ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        e.Mods.Add(m.Trim());
                    _map[e.KeyShape] = e;
                    n++;
                }
                ModInitializer.LogInfo("ProvenanceIndex: loaded entries=" + n);
            }
            catch (Exception ex)
            {
                ModInitializer.LogWarn("ProvenanceIndex: load failed: " + ex.Message);
            }
        }

        private void SaveNoThrow()
        {
            try
            {
                var dir = Path.GetDirectoryName(_indexPath) ?? ".";
                Directory.CreateDirectory(dir);

                var tmp = _indexPath + ".tmp";
                using (var sw = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                {
                    sw.WriteLine("# RimLex en_provenance.tsv");
                    sw.WriteLine("# columns: key_shape\tmods(semicolon)\tfirst_seen_utc\tlast_seen_utc\tcount");
                    foreach (var e in _map.Values.OrderBy(v => v.KeyShape, StringComparer.Ordinal))
                    {
                        var mods = string.Join(";", e.Mods.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                        var line = string.Join("\t", new[]
                        {
                            e.KeyShape,
                            mods,
                            ToIsoUtc(e.FirstSeenUtc),
                            ToIsoUtc(e.LastSeenUtc),
                            e.Count.ToString()
                        });
                        sw.WriteLine(line);
                    }
                }
                // Atomic replace
                if (File.Exists(_indexPath))
                {
                    try
                    {
                        File.Replace(tmp, _indexPath, _indexPath + ".bak");
                    }
                    catch
                    {
                        File.Delete(_indexPath);
                        File.Move(tmp, _indexPath);
                    }
                }
                else
                {
                    File.Move(tmp, _indexPath);
                }
            }
            catch (Exception ex)
            {
                ModInitializer.LogWarn("ProvenanceIndex: save failed: " + ex.Message);
            }
        }

        // Utilities

        private static IEnumerable<string> SafeReadLines(string path)
        {
            using (var sr = new StreamReader(path, new UTF8Encoding(false)))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                    yield return line;
            }
        }

        private static string[] SplitTsv(string line, int expected)
        {
            if (line == null) return Array.Empty<string>();
            var parts = line.Split('\t');
            if (parts.Length < expected) return parts;
            return parts;
        }

        private static DateTime ParseUtc(string s)
        {
            if (DateTime.TryParse(s, out var dt))
                return DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc);
            return DateTime.UtcNow;
        }

        private static long ParseLong(string s)
        {
            if (long.TryParse(s, out var v)) return v;
            return 0;
        }

        private static string ToIsoUtc(DateTime dt)
        {
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }
    }
}

// Key Methods Present:
// - Initialize(string indexPath = null, int debounceMs = 500)
// - ToKeyShape(string s)
// - Update(string keyShape, string mod, DateTime utcNow)
// - FlushIfDue(bool force = false)
// - RebuildFromPerMod(string perModRoot)
