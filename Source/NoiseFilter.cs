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

        // 統計
        private static int _excludedCount = 0;
        public static int ExcludedCount => _excludedCount;

        // ログ抑制用
        private static string _lastScreen = "";
        private static int _suppressed = 0;
        private static long _lastLogMs = 0;
        private const int LOG_WINDOW_MS = 500; // 0.5秒ごとにまとめて1行

        public static void Init(Config cfg)
        {
            _minLen = Math.Max(0, cfg.MinLength);
            try { _rx = new Regex(string.IsNullOrWhiteSpace(cfg.ExcludePatterns) ? @"^\s*$" : cfg.ExcludePatterns, RegexOptions.Compiled); }
            catch { _rx = new Regex(@"^\s*$", RegexOptions.Compiled); }

            _white.Clear();
            _black.Clear();

            void Fill(string csv, HashSet<string> set)
            {
                if (string.IsNullOrWhiteSpace(csv)) return;
                var parts = csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts) set.Add(p.Trim());
            }

            Fill(cfg.IncludedWindows, _white);
            Fill(cfg.ExcludedWindows, _black);

            _logExcludedScreens = cfg.LogExcludedScreens;
            _excludedCount = 0;

            // ログ抑制リセット
            _lastScreen = "";
            _suppressed = 0;
            _lastLogMs = 0;
        }

        public static bool IsNoise(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            if (s.Length < _minLen) return true;
            if (_rx.IsMatch(s)) return true;
            return false;
        }

        public static bool IsScreenExcluded(out string screenName)
        {
            screenName = null;
            try
            {
                var ws = Find.WindowStack;
                if (ws != null && ws.Windows != null && ws.Windows.Count > 0)
                {
                    var top = ws.Windows[ws.Windows.Count - 1];
                    screenName = top?.GetType()?.Name ?? "";
                }
                else
                {
                    var rt = Find.UIRoot;
                    screenName = rt?.GetType()?.Name ?? "";
                }

                if (string.IsNullOrEmpty(screenName)) return false;

                // ホワイトがあればその他は弾く
                if (_white.Count > 0 && !_white.Contains(screenName))
                {
                    CountAndMaybeLog("whitelist", screenName);
                    return true;
                }

                // ブラックに入っていれば弾く
                if (_black.Contains(screenName))
                {
                    CountAndMaybeLog("blacklist", screenName);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void CountAndMaybeLog(string listKind, string screen)
        {
            _excludedCount++;

            if (!_logExcludedScreens) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_lastScreen == screen)
            {
                // 同じ画面。時間窓内ならカウントだけ増やし、窓を越えたら1行で吐く
                if (now - _lastLogMs < LOG_WINDOW_MS)
                {
                    _suppressed++;
                    return;
                }
                else
                {
                    // 窓越え：まとめて出力
                    if (_suppressed > 0)
                        ModInitializer.LogInfo($"Excluded({listKind}): {screen} x{_suppressed + 1}");
                    else
                        ModInitializer.LogInfo($"Excluded({listKind}): {screen}");
                    _suppressed = 0;
                    _lastLogMs = now;
                }
            }
            else
            {
                // 画面が変わった：前のまとめを閉じて、新規1行
                if (_suppressed > 0 && !string.IsNullOrEmpty(_lastScreen))
                    ModInitializer.LogInfo($"Excluded({listKind}): {_lastScreen} x{_suppressed + 1}");
                _lastScreen = screen;
                _suppressed = 0;
                _lastLogMs = now;
                ModInitializer.LogInfo($"Excluded({listKind}): {screen}");
            }
        }
    }
}
