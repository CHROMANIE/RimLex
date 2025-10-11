using System.Text;

namespace RimLex
{
    public static class Normalizer
    {
        // Collapse consecutive whitespace characters into a single space and trim the result.
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                    continue;
                }

                prevSpace = false;
                sb.Append(c);
            }

            return sb.ToString().Trim();
        }
    }
}
