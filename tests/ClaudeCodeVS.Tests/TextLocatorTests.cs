using ClaudeCodeVS.Bridge;
using Xunit;

namespace ClaudeCodeVS.Tests
{
    public class TextLocatorTests
    {
        private const string Src = "using System;\r\nclass A\r\n{\r\n    void Run() { }\r\n}\r\n";

        [Fact]
        public void Finds_start_text()
        {
            Assert.True(TextLocator.TryFindRange(Src, "class A", null, false, out var s, out var e));
            Assert.Equal("class A", Src.Substring(s, e - s));
        }

        [Fact]
        public void Extends_to_end_text_when_present_after_start()
        {
            Assert.True(TextLocator.TryFindRange(Src, "void Run()", "}", false, out var s, out var e));
            Assert.Equal("void Run() { }", Src.Substring(s, e - s));
        }

        [Fact]
        public void Missing_end_text_keeps_start_only()
        {
            Assert.True(TextLocator.TryFindRange(Src, "class A", "NOPE", false, out var s, out var e));
            Assert.Equal("class A", Src.Substring(s, e - s));
        }

        [Fact]
        public void Select_to_end_of_line_stops_before_crlf()
        {
            Assert.True(TextLocator.TryFindRange(Src, "void", null, true, out var s, out var e));
            Assert.Equal("void Run() { }", Src.Substring(s, e - s));
        }

        [Fact]
        public void Select_to_end_of_line_on_last_line_without_newline()
        {
            const string t = "a\nlast";
            Assert.True(TextLocator.TryFindRange(t, "la", null, true, out var s, out var e));
            Assert.Equal("last", t.Substring(s, e - s));
        }

        [Theory]
        [InlineData("zzz")]
        [InlineData("")]
        [InlineData(null)]
        public void Absent_or_empty_start_text_returns_false(string start)
        {
            Assert.False(TextLocator.TryFindRange(Src, start, null, false, out _, out _));
        }

        [Fact]
        public void LineCol_is_one_based_and_counts_crlf_as_one_break()
        {
            TextLocator.LineCol(Src, 0, out var l, out var c);
            Assert.Equal((1, 1), (l, c));
            TextLocator.LineCol(Src, Src.IndexOf("class"), out l, out c);
            Assert.Equal((2, 1), (l, c));
            TextLocator.LineCol(Src, Src.IndexOf("void"), out l, out c);
            Assert.Equal((4, 5), (l, c));
        }
    }
}
