using System.Text;
using System.Text.RegularExpressions;

namespace RimLex
{
    /// <summary>
    /// Provides normalization utilities for RimLex. Text is normalized so that runtime collection
    /// and dictionary lookup can share the same ruleset.
    /// </summary>
    public static class Normalizer
    {
        // Accept loose variants such as "/ n" and collapse them into "\n"
        private static readonly Regex RxSlashNLoose = new Regex(@"/\s*n", RegexOptions.Compiled);
        // Literal "\n" sequences are treated as actual newlines
        private static readonly Regex RxBackslashN = new Regex(@"\\n", RegexOptions.Compiled);

        /// <summary>
        /// Normalizes incoming text for dictionary keys.
        /// - Normalizes CRLF / literal "\n" / loose "/n" into a unified newline token
        /// - Collapses consecutive whitespace to a single space
        /// - Trims trailing spaces around newline boundaries
        /// - Returns a trimmed string (TranslatorHub performs additional canonicalization)
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string normalized = text.Replace("\r\n", "\n");
            normalized = RxBackslashN.Replace(normalized, "\n");
            normalized = RxSlashNLoose.Replace(normalized, "\n");

            var sb = new StringBuilder(normalized.Length + 8);
            bool previousWasSpace = false;

            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];

                if (c == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                        sb.Length--;

                    sb.Append("/n");
                    previousWasSpace = false;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (!previousWasSpace)
                    {
                        sb.Append(' ');
                        previousWasSpace = true;
                    }
                    continue;
                }

                previousWasSpace = false;
                sb.Append(c);
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Converts the normalized "/n" tokens back into "\n" for UI rendering.
        /// </summary>
        public static string ReifyNewlines(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string normalized = RxSlashNLoose.Replace(text, "/n");
            normalized = RxBackslashN.Replace(normalized, "\n");
            return normalized.Replace("/n", "\n");
        }
    }
}
