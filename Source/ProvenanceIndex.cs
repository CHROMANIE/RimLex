using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RimLex
{
    internal static class ProvenanceIndex
    {
        private sealed class Entry
        {
            public HashSet<string> Mods = new HashSet<string>(StringComparer.Ordinal);
            public DateTime FirstSeenUtc;
            public DateTime LastSeenUtc;
            public long Count;
        }

        private sealed class Snapshot
        {
            public string Shape;
            public string[] Mods;
            public DateTime FirstSeenUtc;
            public DateTime LastSeenUtc;
            public long Count;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static Timer _flushTimer;
        private const int FLUSH_DELAY_MS = 1200;
        private static bool _dirty = false;
        private static bool _initialized = false;
        private static string _indexPath = string.Empty;
        private static Action<string> _logInfo = _ => { };
        private static Action<string> _logWarn = _ => { };
        private static Action _ioError = null;

        public static void Init(string dictPath, Action<string> logInfo, Action<string> logWarn, Action ioError)
        {
            lock (_lock)
            {
                _logInfo = logInfo ?? (_ => { });
                _logWarn = logWarn ?? (_ => { });
                _ioError = ioError;

                string dictDir = Path.GetDirectoryName(dictPath) ?? string.Empty;
                string indexDir = Path.Combine(dictDir, "Index");
                Directory.CreateDirectory(indexDir);
                _indexPath = Path.Combine(indexDir, "en_provenance.tsv");

                _flushTimer?.Dispose();
                _flushTimer = new Timer(_ => FlushSafe(), null, Timeout.Infinite, Timeout.Infinite);

                Load_NoThrow();
                _initialized = true;
            }
        }

        public static void Register(string shape, string modName)
        {
            if (string.IsNullOrEmpty(shape)) return;

            lock (_lock)
            {
                if (!_initialized) return;

                if (!_entries.TryGetValue(shape, out var entry))
                {
                    entry = new Entry
                    {
                        FirstSeenUtc = DateTime.UtcNow,
                        LastSeenUtc = DateTime.UtcNow,
                        Count = 0
                    };
                    _entries[shape] = entry;
                }

                entry.LastSeenUtc = DateTime.UtcNow;
                if (entry.Count == 0)
                    entry.FirstSeenUtc = entry.LastSeenUtc;

                entry.Count++;

                if (!string.IsNullOrWhiteSpace(modName))
                    entry.Mods.Add(modName.Trim());

                _dirty = true;
                ScheduleFlush();
            }
        }

        public static bool TryRebuildFromPerMod(string exportRoot, string perModSubdir, out int keyCount, out int modCount, out string error)
        {
            keyCount = 0;
            modCount = 0;
            error = null;

            try
            {
                var temp = new Dictionary<string, Entry>(StringComparer.Ordinal);
                var allMods = new HashSet<string>(StringComparer.Ordinal);

                string perModRoot = Path.Combine(exportRoot ?? string.Empty, string.IsNullOrEmpty(perModSubdir) ? "PerMod" : perModSubdir);
                if (Directory.Exists(perModRoot))
                {
                    foreach (var modDir in Directory.GetDirectories(perModRoot))
                    {
                        string folderMod = Path.GetFileName(modDir) ?? "";
                        string tsv = Path.Combine(modDir, "strings_en.tsv");
                        if (!File.Exists(tsv)) continue;

                        foreach (var raw in File.ReadAllLines(tsv, new UTF8Encoding(false)))
                        {
                            if (string.IsNullOrWhiteSpace(raw)) continue;
                            if (raw.StartsWith("timestamp_utc")) continue;

                            var parts = raw.Split('\t');
                            if (parts.Length < 5) continue;

                            DateTime seen = ParseTimestamp(parts[0]);
                            string mod = string.IsNullOrWhiteSpace(parts[1]) ? folderMod : parts[1];
                            string text = parts[4].Replace("/n", "\n");
                            string normalized = TextShapeUtil.NormalizeForKey(text);
                            var shape = TextShapeUtil.MakeShape(normalized);
                            if (string.IsNullOrEmpty(shape.Shape)) continue;

                            if (!temp.TryGetValue(shape.Shape, out var entry))
                            {
                                entry = new Entry
                                {
                                    FirstSeenUtc = seen,
                                    LastSeenUtc = seen,
                                    Count = 0
                                };
                                temp[shape.Shape] = entry;
                            }

                            entry.Count++;
                            if (seen < entry.FirstSeenUtc) entry.FirstSeenUtc = seen;
                            if (seen > entry.LastSeenUtc) entry.LastSeenUtc = seen;

                            string modNormalized = NormalizeModName(mod);
                            if (!string.IsNullOrEmpty(modNormalized))
                            {
                                entry.Mods.Add(modNormalized);
                                allMods.Add(modNormalized);
                            }
                        }
                    }
                }

                lock (_lock)
                {
                    _entries.Clear();
                    foreach (var kv in temp)
                        _entries[kv.Key] = kv.Value;
                    _dirty = true;
                }

                keyCount = temp.Count;
                modCount = allMods.Count;

                ScheduleFlush(0);
                _logInfo($"[ProvenanceIndex] rebuilt from PerMod: keys={keyCount}, mods={modCount}");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logWarn("[ProvenanceIndex] rebuild failed: " + ex);
                _ioError?.Invoke();
                return false;
            }
        }

        private static void Load_NoThrow()
        {
            try
            {
                _entries.Clear();
                if (!File.Exists(_indexPath)) return;

                foreach (var raw in File.ReadAllLines(_indexPath, new UTF8Encoding(false)))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    if (raw.StartsWith("#")) continue;
                    if (raw.StartsWith("key_shape")) continue;

                    var parts = raw.Split('\t');
                    if (parts.Length < 5) continue;

                    string shape = parts[0];
                    string modsCsv = parts[1];
                    DateTime first = ParseTimestamp(parts[2]);
                    DateTime last = ParseTimestamp(parts[3]);
                    long count = ParseLong(parts[4]);

                    var entry = new Entry
                    {
                        FirstSeenUtc = first,
                        LastSeenUtc = last,
                        Count = count > 0 ? count : 0
                    };

                    foreach (var mod in (modsCsv ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string norm = NormalizeModName(mod);
                        if (!string.IsNullOrEmpty(norm)) entry.Mods.Add(norm);
                    }

                    _entries[shape] = entry;
                }
            }
            catch (Exception ex)
            {
                _logWarn("[ProvenanceIndex] load failed: " + ex.Message);
            }
        }

        private static void FlushSafe()
        {
            Snapshot[] snapshot;
            lock (_lock)
            {
                if (!_initialized) return;
                if (!_dirty) return;

                snapshot = _entries
                    .Select(kv => new Snapshot
                    {
                        Shape = kv.Key,
                        Mods = kv.Value.Mods.OrderBy(m => m, StringComparer.Ordinal).ToArray(),
                        FirstSeenUtc = kv.Value.FirstSeenUtc,
                        LastSeenUtc = kv.Value.LastSeenUtc,
                        Count = kv.Value.Count
                    })
                    .ToArray();

                _dirty = false;
            }

            try
            {
                string tmp = _indexPath + ".tmp";
                using (var sw = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                {
                    sw.WriteLine("key_shape\tmods\tfirst_seen_utc\tlast_seen_utc\tcount");
                    foreach (var row in snapshot.OrderBy(s => s.Shape, StringComparer.Ordinal))
                    {
                        string modsCsv = string.Join(";", row.Mods ?? Array.Empty<string>());
                        sw.Write(row.Shape ?? string.Empty);
                        sw.Write('\t');
                        sw.Write(modsCsv);
                        sw.Write('\t');
                        sw.Write(row.FirstSeenUtc.ToString("O"));
                        sw.Write('\t');
                        sw.Write(row.LastSeenUtc.ToString("O"));
                        sw.Write('\t');
                        sw.WriteLine(row.Count.ToString(CultureInfo.InvariantCulture));
                    }
                }

                if (File.Exists(_indexPath)) File.Delete(_indexPath);
                File.Move(tmp, _indexPath);
            }
            catch (Exception ex)
            {
                _logWarn("[ProvenanceIndex] flush failed: " + ex.Message);
                _ioError?.Invoke();
                lock (_lock) { _dirty = true; }
            }
        }

        private static void ScheduleFlush(int dueTime = FLUSH_DELAY_MS)
        {
            try { _flushTimer?.Change(Math.Max(0, dueTime), Timeout.Infinite); }
            catch { }
        }

        private static DateTime ParseTimestamp(string raw)
        {
            if (DateTime.TryParse(raw, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                return dt.ToUniversalTime();
            return DateTime.UtcNow;
        }

        private static long ParseLong(string raw)
        {
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
            return 0;
        }

        private static string NormalizeModName(string mod)
        {
            if (string.IsNullOrWhiteSpace(mod)) return string.Empty;
            return mod.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
