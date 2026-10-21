using System;

namespace ClaudeCodeVS.Bridge
{
    /// <summary>Pure text helpers behind openFile's startText/endText selection (no VS dependencies, unit tested).</summary>
    internal static class TextLocator
    {
        /// <summary>
        /// Finds startText and, optionally, the first endText after it. Returns false if startText is absent.
        /// With toEndOfLine the range is extended to the end of the line containing its end.
        /// </summary>
        public static bool TryFindRange(string text, string startText, string endText, bool toEndOfLine, out int start, out int end)
        {
            start = end = 0;
            if (string.IsNullOrEmpty(startText)) return false;
            var s = text.IndexOf(startText, StringComparison.Ordinal);
            if (s < 0) return false;
            var e = s + startText.Length;
            if (!string.IsNullOrEmpty(endText))
            {
                var ei = text.IndexOf(endText, e, StringComparison.Ordinal);
                if (ei >= 0) e = ei + endText.Length;
            }
            if (toEndOfLine)
            {
                var nl = text.IndexOf('\n', e);
                e = nl < 0 ? text.Length : (nl > 0 && text[nl - 1] == '\r' ? nl - 1 : nl);
            }
            start = s; end = e;
            return true;
        }

        /// <summary>1-based line and column of a string index; "\r\n" counts as a single line break.</summary>
        public static void LineCol(string text, int index, out int line, out int col)
        {
            line = 1; col = 1;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n') { line++; col = 1; }
                else if (text[i] != '\r') col++;
            }
        }
    }
}
