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

        private static string _lastScreen = string.Empty;

        private struct DynState
        {
            public int Count;
            public long FirstMs;
            public long MutedUntil;
        }

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
            try
            {
                _rx = new Regex(string.IsNullOrWhiteSpace(cfg.ExcludePatterns) ? @"^\s*$" : cfg.ExcludePatterns, RegexOptions.Compiled);
            }
            catch
            {
                _rx = new Regex(@"^\s*$", RegexOptions.Compiled);
            }

            _white.Clear();
            _black.Clear();

            void Fill(string csv, HashSet<string> set)
            {
                if (string.IsNullOrWhiteSpace(csv)) return;
                foreach (var part in csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    set.Add(part.Trim());
            }

            Fill("EditWindow_Log,Page_ModsConfig", _black);
            Fill(cfg.IncludedWindows, _white);
            Fill(cfg.ExcludedWindows, _black);

            _logExcludedScreens = cfg.LogExcludedScreens;
            ResetCounters();
        }

        public static bool IsNoise(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            if (text.Length < _minLen) return true;
            if (_rx.IsMatch(text)) return true;
            if (RxMostlyNums.IsMatch(text)) return true;
            if (IsCjkDominant(text)) return true;
            if (IsDynamicNumeric(text)) return true;
            return false;
        }

        public static bool IsScreenExcluded(out string screenName)
        {
            screenName = null;
            try
            {
                var ws = Find.WindowStack;
                if (ws != null && ws.Windows != null && ws.Windows.Count > 0)
                    screenName = ws.Windows[ws.Windows.Count - 1]?.GetType()?.Name ?? string.Empty;
                else
                    screenName = Find.UIRoot?.GetType()?.Name ?? string.Empty;

                if (string.IsNullOrEmpty(screenName)) return false;

                if (_white.Count > 0 && !_white.Contains(screenName))
                {
                    CountAndMaybeLog("whitelist", screenName);
                    return true;
                }

                if (_black.Contains(screenName))
                {
                    CountAndMaybeLog("blacklist", screenName);
                    return true;
                }
            }
            catch
            {
            }

            _lastScreen = string.Empty;
            return false;
        }

        private static void CountAndMaybeLog(string kind, string screen)
        {
            if (string.Equals(_lastScreen, screen, StringComparison.Ordinal))
                return;

            _excludedCount++;
            _lastScreen = screen;

            if (_logExcludedScreens)
                ModInitializer.LogInfo($"Excluded({kind}): {screen}");
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static void ResetCounters()
        {
            _excludedCount = 0;
            _lastScreen = string.Empty;
            _dyn.Clear();
        }

        private static bool IsDynamicNumeric(string text)
        {
            if (!RxDigits.IsMatch(text)) return false;

            string shape = RxDigits.Replace(text, "#").Trim();
            long now = NowMs();

            if (_dyn.TryGetValue(shape, out var state))
            {
                if (now < state.MutedUntil) return true;

                if (now - state.FirstMs <= DYN_WINDOW_MS)
                {
                    state.Count++;
                    if (state.Count >= DYN_THRESHOLD)
                    {
                        state.MutedUntil = now + DYN_MUTE_MS;
                        _dyn[shape] = state;
                        return true;
                    }

                    _dyn[shape] = state;
                }
                else
                {
                    state.Count = 1;
                    state.FirstMs = now;
                    state.MutedUntil = 0;
                    _dyn[shape] = state;
                }
            }
            else
            {
                _dyn[shape] = new DynState { Count = 1, FirstMs = now, MutedUntil = 0 };
            }

            return false;
        }

        private static bool IsCjkDominant(string text)
        {
            int cjk = 0;
            int total = 0;

            foreach (var ch in text)
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
