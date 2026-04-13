using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Models;
using SunshineLibrary.Services;
using System;
using System.IO;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class OverrideStoreTests
    {
        private string tempDir;

        [TestInitialize]
        public void Setup()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "SunshineLibraryTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }

        [TestMethod]
        public void Set_Get_RoundTrip()
        {
            var s1 = new OverrideStore(tempDir);
            s1.Set("host1:gameA", new StreamOverrides { BitrateKbps = 20000, Hdr = HdrMode.On });

            // New instance reads from disk
            var s2 = new OverrideStore(tempDir);
            var back = s2.TryGet("host1:gameA");
            Assert.IsNotNull(back);
            Assert.AreEqual(20000, back.BitrateKbps);
            Assert.AreEqual(HdrMode.On, back.Hdr);
        }

        [TestMethod]
        public void SetNull_Removes()
        {
            var s = new OverrideStore(tempDir);
            s.Set("k", new StreamOverrides { BitrateKbps = 1000 });
            Assert.IsNotNull(s.TryGet("k"));

            s.Set("k", null);
            Assert.IsNull(s.TryGet("k"));

            var s2 = new OverrideStore(tempDir);
            Assert.IsNull(s2.TryGet("k"));
        }

        [TestMethod]
        public void Clear_WipesAll()
        {
            var s = new OverrideStore(tempDir);
            s.Set("a", new StreamOverrides { BitrateKbps = 1 });
            s.Set("b", new StreamOverrides { BitrateKbps = 2 });

            s.Clear();
            Assert.IsNull(s.TryGet("a"));
            Assert.IsNull(s.TryGet("b"));
        }

        [TestMethod]
        public void CorruptFile_ReturnsEmpty_DoesNotThrow()
        {
            File.WriteAllText(Path.Combine(tempDir, "overrides.json"), "{ not valid json");
            var s = new OverrideStore(tempDir);
            Assert.IsNull(s.TryGet("anything"));
        }

        [TestMethod]
        public void MultipleKeys_Independent()
        {
            var s = new OverrideStore(tempDir);
            s.Set("a", new StreamOverrides { Hdr = HdrMode.On });
            s.Set("b", new StreamOverrides { Hdr = HdrMode.Off });

            var s2 = new OverrideStore(tempDir);
            Assert.AreEqual(HdrMode.On, s2.TryGet("a").Hdr);
            Assert.AreEqual(HdrMode.Off, s2.TryGet("b").Hdr);
        }
    }
}
