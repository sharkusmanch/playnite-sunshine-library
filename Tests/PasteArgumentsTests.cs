using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Services.Clients;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class PasteArgumentsTests
    {
        [TestMethod]
        public void Simple_NoSpecialChars_NoQuoting()
        {
            Assert.AreEqual("stream host app", PasteArguments.Build("stream", "host", "app"));
        }

        [TestMethod]
        public void Whitespace_IsQuoted()
        {
            Assert.AreEqual("stream host \"My Game\"", PasteArguments.Build("stream", "host", "My Game"));
        }

        [TestMethod]
        public void InternalQuote_IsEscaped()
        {
            Assert.AreEqual("\"say \\\"hi\\\"\"", PasteArguments.Build("say \"hi\""));
        }

        [TestMethod]
        public void TrailingBackslash_InQuotedArg_IsDoubled()
        {
            // Arg: path\  (one backslash)  →  "path\\"  (two) because it precedes the closing quote
            Assert.AreEqual("\"a path\\\\\"", PasteArguments.Build("a path\\"));
        }

        [TestMethod]
        public void BackslashesBeforeInternalQuote_AreDoubled()
        {
            // Arg: a\\"b   →  "a\\\\\"b"
            Assert.AreEqual("\"a\\\\\\\\\\\"b\"", PasteArguments.Build("a\\\\\"b"));
        }

        [TestMethod]
        public void EmptyString_IsEmptyQuotes()
        {
            Assert.AreEqual("\"\"", PasteArguments.Build(""));
        }

        [TestMethod]
        public void Unicode_PassesThroughIfNoWhitespace()
        {
            Assert.AreEqual("東京", PasteArguments.Build("東京"));
        }

        [TestMethod]
        public void Tab_TriggersQuoting()
        {
            Assert.AreEqual("\"a\tb\"", PasteArguments.Build("a\tb"));
        }
    }
}
