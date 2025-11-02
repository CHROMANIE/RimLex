using System;
using System.Text;
using System.Text.RegularExpressions;

namespace RimLex
{
    public static class Normalizer
    {
        // "/n" ゆらぎ（"/ n" など）を拾う
        private static readonly Regex RxSlashNLoose = new Regex(@"/\s*n", RegexOptions.Compiled);
        // 文字列リテラルの "\n"
        private static readonly Regex RxBackslashN = new Regex(@"\\n", RegexOptions.Compiled);

        /// <summary>
        /// 英文キーの正規化（辞書照合用）。
        /// 仕様：
        /// - CRLF/\\n/"/n"（ゆらぎ含む）を一度「実改行 \\n」に統一 → キー表現として "/n" に戻す
        /// - 改行以外の連続空白は 1 個に圧縮
        /// - 改行の前後の余分な空白は除去
        /// - 末尾は Trim
        /// ※ 数字の # 形状化は TranslatorHub 側の既存処理に委ねる（ここでは変更しない）
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // 1) 改行表記を実改行に統一
            s = s.Replace("\r\n", "\n");
            s = RxBackslashN.Replace(s, "\n");
            s = RxSlashNLoose.Replace(s, "\n");

            // 2) 改行を保持しつつ、その他の空白は圧縮
            var sb = new StringBuilder(s.Length + 8);
            bool prevSpace = false;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                if (c == '\n')
                {
                    // 改行直前の空白は削る
                    if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                        sb.Length--;

                    // キー表現は "/n"
                    sb.Append("/n");
                    prevSpace = false;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                    continue;
                }

                prevSpace = false;
                sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// 表示用に "/n"（ゆらぎ含む）を実改行 \n へ復元。
        /// ※ 置換後の UI 表示に使用。
        /// </summary>
        public static string ReifyNewlines(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // ゆらぎをまず "/n" に寄せる
            s = RxSlashNLoose.Replace(s, "/n");
            // 文字列リテラルの "\n" も実改行へ
            s = RxBackslashN.Replace(s, "\n");
            // 最後に "/n" → 実改行
            return s.Replace("/n", "\n");
        }
    }
}
