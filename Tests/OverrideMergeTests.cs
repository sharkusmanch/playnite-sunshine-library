using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SunshineLibrary.Models;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class OverrideMergeTests
    {
        [TestMethod]
        public void Null_True_False_RoundTripDistinctly()
        {
            foreach (bool? v in new bool?[] { null, true, false })
            {
                var o = new StreamOverrides { Yuv444 = v };
                var json = JsonConvert.SerializeObject(o, StreamOverrides.JsonSettings);
                var back = JsonConvert.DeserializeObject<StreamOverrides>(json, StreamOverrides.JsonSettings);
                Assert.AreEqual(v, back.Yuv444, $"failed for {v}");
            }
        }

        [TestMethod]
        public void Merge_NullFallsThrough()
        {
            var a = new StreamOverrides { BitrateKbps = 10000 };
            var b = new StreamOverrides { /* null everywhere */ };
            var merged = a.MergedWith(b);
            Assert.AreEqual(10000, merged.BitrateKbps);
        }

        [TestMethod]
        public void Merge_RightWinsWhenSet()
        {
            var a = new StreamOverrides { BitrateKbps = 10000 };
            var b = new StreamOverrides { BitrateKbps = 25000 };
            var merged = a.MergedWith(b);
            Assert.AreEqual(25000, merged.BitrateKbps);
        }
    }
}
