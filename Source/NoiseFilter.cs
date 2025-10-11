// RimLex NoiseFilter.cs v0.10.0-rc4 @2025-10-09 08:05
// 変更: 除外カウンタをデバウンス（同一画面は ~0.5s 以内はカウントしない）。
//       既定ブラックリストは EditWindow_Log, Page_ModsConfig（前回と同じ）。

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Verse;

namespace RimLex
{
    public static class NoiseFilter
    {
        private static int _minLen = 2;
        private static Regex _rx = new Regex(@"^\s*$", RegexOptions.Compiled);
        private static readonly HashSet<string> _white = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _black = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _logExcludedScreens = true;

        private static int _excludedCount = 0;
        public static int ExcludedCount => _excludedCount;

        private static string _lastScreen = "";
        private static int _suppressed = 0;
        private static long _lastLogMs = 0;
        private const int LOG_WINDOW_MS = 500;

        private struct DynState { public int Count; public long FirstMs; public long MutedUntil; }
        private static readonly Dictionary<string, DynState> _dyn = new Dictionary<string, DynState>(StringComparer.Ordinal);
        private const int DYN_WINDOW_MS = 800;
        private const int DYN_THRESHOLD = 3;
        private const int DYN_MUTE_MS = 3000;

        private static readonly Regex RxDigits = new Regex(@"\d+(?:\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex RxMostlyNums = new Regex(@"^[\d\.\,\+\-:%/()\s＝=→<>％]+$", RegexOptions.Compiled);
        private static readonly Regex RxCjk = new Regex(@"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]", RegexOptions.Compiled);

        public static void Init(Config cfg)
        {
            _minLen = Math.Max(0, cfg.MinLength);
            try { _rx = new Regex(string.IsNullOrWhiteSpace(cfg.ExcludePatterns) ? @"^\s*$" : cfg.ExcludePatterns, RegexOptions.Compiled); }
            catch { _rx = new Regex(@"^\s*$", RegexOptions.Compiled); }

            _white.Clear(); _black.Clear();

            void Fill(string csv, HashSet<string> set)
            {
                if (string.IsNullOrWhiteSpace(csv)) return;
                foreach (var p in csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    set.Add(p.Trim());
            }

            Fill("EditWindow_Log,Page_ModsConfig", _black);
            Fill(cfg.IncludedWindows, _white);
            Fill(cfg.ExcludedWindows, _black);

            _logExcludedScreens = cfg.LogExcludedScreens;
            _excludedCount = 0;

            _lastScreen = ""; _suppressed = 0; _lastLogMs = 0;
            _dyn.Clear();
        }

        public static bool IsNoise(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            if (s.Length < _minLen) return true;

            if (_rx.IsMatch(s)) return true;
            if (RxMostlyNums.IsMatch(s)) return true;
            if (IsCjkDominant(s)) return true;

            if (IsDynamicNumeric(s)) return true;

            return false;
        }

        public static bool IsScreenExcluded(out string screenName)
        {
            screenName = null;
            try
            {
                var ws = Find.WindowStack;
                if (ws != null && ws.Windows != null && ws.Windows.Count > 0)
                    screenName = ws.Windows[ws.Windows.Count - 1]?.GetType()?.Name ?? "";
                else
                    screenName = Find.UIRoot?.GetType()?.Name ?? "";

                if (string.IsNullOrEmpty(screenName)) return false;

                if (_white.Count > 0 && !_white.Contains(screenName))
                { CountAndMaybeLog("whitelist", screenName); return true; }

                if (_black.Contains(screenName))
                { CountAndMaybeLog("blacklist", screenName); return true; }
            }
            catch { }
            return false;
        }

        private static void CountAndMaybeLog(string kind, string screen)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // デバウンス: 同一画面が短時間連打される場合はカウントも抑制
            if (_lastScreen == screen && (now - _lastLogMs) < LOG_WINDOW_MS)
            {
                _suppressed++;
                return;
            }

            int added = 1;
            string message = null;

            // 直前の抑制分をまとめて確定
            if (_suppressed > 0 && !string.IsNullOrEmpty(_lastScreen))
            {
                added = _suppressed + 1;
                if (_logExcludedScreens)
                    message = $"Excluded({kind}): {_lastScreen} x{added}";
                _suppressed = 0;
            }
            else if (_logExcludedScreens)
            {
                message = $"Excluded({kind}): {screen}";
            }

            _excludedCount += added;
            if (message != null) ModInitializer.LogInfo(message);

            _lastScreen = screen;
            _lastLogMs = now;
        }

        private static bool IsDynamicNumeric(string s)
        {
            if (!RxDigits.IsMatch(s)) return false;

            string shape = RxDigits.Replace(s, "#").Trim();

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_dyn.TryGetValue(shape, out var st))
            {
                if (now < st.MutedUntil) return true;

                if (now - st.FirstMs <= DYN_WINDOW_MS)
                {
                    st.Count++;
                    if (st.Count >= DYN_THRESHOLD)
                    {
                        st.MutedUntil = now + DYN_MUTE_MS;
                        _dyn[shape] = st;
                        return true;
                    }
                    _dyn[shape] = st;
                }
                else
                {
                    st.Count = 1; st.FirstMs = now; st.MutedUntil = 0;
                    _dyn[shape] = st;
                }
            }
            else
            {
                _dyn[shape] = new DynState { Count = 1, FirstMs = now, MutedUntil = 0 };
            }
            return false;
        }

        private static bool IsCjkDominant(string s)
        {
            int cjk = 0, total = 0;
            foreach (var ch in s)
            {
                if (char.IsControl(ch)) continue;
                total++;
                if (RxCjk.IsMatch(ch.ToString())) cjk++;
            }
            if (total == 0) return false;
            return (cjk * 100 / total) >= 30;
        }
    }
}
