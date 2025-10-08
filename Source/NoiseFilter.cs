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

        // ===== NumericMotionGuard =====
        // 文字列から「数字の並び」を # に畳んだ “形(shape)” を作り、
        // 同じ shape が短時間に繰り返されるなら「動的値」と見なして一定時間ミュートする。
        private struct DynState { public int Count; public long FirstMs; public long MutedUntil; }
        private static readonly Dictionary<string, DynState> _dyn = new Dictionary<string, DynState>(StringComparer.Ordinal);
        private const int DYN_WINDOW_MS = 800;    // この時間内に…
        private const int DYN_THRESHOLD = 3;      // 同じ shape が3回以上出たら
        private const int DYN_MUTE_MS = 3000;     // 3秒ミュート
        private static readonly Regex _rxDigits = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex _rxMostlyNums = new Regex(@"^[\d\s\.\,\+\-:%/()＝=→<>％]+$", RegexOptions.Compiled);

        public static void Init(Config cfg)
        {
            _minLen = Math.Max(0, cfg.MinLength);
            try { _rx = new Regex(string.IsNullOrWhiteSpace(cfg.ExcludePatterns) ? @"^\s*$" : cfg.ExcludePatterns, RegexOptions.Compiled); }
            catch { _rx = new Regex(@"^\s*$", RegexOptions.Compiled); }

            _white.Clear(); _black.Clear();

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

            // リセット
            _lastScreen = ""; _suppressed = 0; _lastLogMs = 0;
            _dyn.Clear();
        }

        public static bool IsNoise(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            if (s.Length < _minLen) return true;

            // 1) 正規表現の即時除外（空白/URL/数字だけ/記号だけ/…）
            if (_rx.IsMatch(s)) return true;

            // 2) 「数字まみれ」っぽいものは即ノイズ扱い（%や : 区切りのみ等）
            if (_rxMostlyNums.IsMatch(s)) return true;

            // 3) 動的値ガード：数字を # に潰した shape を見る
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

        // ===== helpers =====

        private static void CountAndMaybeLog(string listKind, string screen)
        {
            _excludedCount++;

            if (!_logExcludedScreens) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_lastScreen == screen)
            {
                if (now - _lastLogMs < LOG_WINDOW_MS)
                {
                    _suppressed++;
                    return;
                }
                else
                {
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
                if (_suppressed > 0 && !string.IsNullOrEmpty(_lastScreen))
                    ModInitializer.LogInfo($"Excluded({listKind}): {_lastScreen} x{_suppressed + 1}");
                _lastScreen = screen;
                _suppressed = 0;
                _lastLogMs = now;
                ModInitializer.LogInfo($"Excluded({listKind}): {screen}");
            }
        }

        private static bool IsDynamicNumeric(string s)
        {
            // 数字が1つも無いなら対象外
            if (!_rxDigits.IsMatch(s)) return false;

            // 形(shape)を作る：連続する数字を # に潰す（例）"HP: 123/200" → "HP: #/#"
            string shape = _rxDigits.Replace(s, "#").Trim();

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_dyn.TryGetValue(shape, out var st))
            {
                // 既にミュート中ならノイズ
                if (now < st.MutedUntil) return true;

                // 窓内カウント
                if (now - st.FirstMs <= DYN_WINDOW_MS)
                {
                    st.Count++;
                    if (st.Count >= DYN_THRESHOLD)
                    {
                        st.MutedUntil = now + DYN_MUTE_MS;
                        _dyn[shape] = st;
                        return true; // しきい値到達でノイズ
                    }
                    _dyn[shape] = st;
                }
                else
                {
                    // 窓を開き直す
                    st.Count = 1;
                    st.FirstMs = now;
                    st.MutedUntil = 0;
                    _dyn[shape] = st;
                }
            }
            else
            {
                _dyn[shape] = new DynState { Count = 1, FirstMs = now, MutedUntil = 0 };
            }

            // まだノイズ確定ではない
            return false;
        }
    }
}
