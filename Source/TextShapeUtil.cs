using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace RimLex
{
    internal static class TextShapeUtil
    {
        private static readonly Regex RxSlashNLoose = new Regex("\\s*/\\s*n\\s*", RegexOptions.Compiled);
        private static readonly Regex RxNumbers = new Regex("\\d+(?:\\.\\d+)?", RegexOptions.Compiled);

        internal sealed class ShapeParts
        {
            public string Shape;
            public List<string> Numbers;
        }

        public static string NormalizeForKey(string s)
        {
            string working = (s ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
            working = working.Replace("\\n", "\n");
            working = RxSlashNLoose.Replace(working, "\n");
            return working;
        }

        public static ShapeParts MakeShape(string normalized)
        {
            var numbers = new List<string>();
            string shaped = RxNumbers.Replace(normalized ?? string.Empty, m =>
            {
                numbers.Add(m.Value);
                return "#";
            });
            return new ShapeParts { Shape = shaped, Numbers = numbers };
        }

        public static string ForTsv(string s)
        {
            if (s == null) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\t': sb.Append(' '); break;
                    case '\r': sb.Append(' '); break;
                    case '\n': sb.Append("/n"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }
    }
}
