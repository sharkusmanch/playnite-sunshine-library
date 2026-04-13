using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Models;
using SunshineLibrary.Services;
using System;
using System.Collections.Generic;
using System.IO;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class AppCacheTests
    {
        private string tempDir;

        [TestInitialize]
        public void Init()
        {
            tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort */ }
        }

        [TestMethod]
        public void Save_Then_Load_RoundTrips_ThreeApps()
        {
            var cache = new AppCache(tempDir);
            var id = Guid.NewGuid();
            var apps = new List<RemoteApp>
            {
                new RemoteApp { Name = "Desktop",      StableId = "sha-desktop", Index = 0 },
                new RemoteApp { Name = "Steam Big Pic", StableId = "sha-steam",  Index = 1 },
                new RemoteApp { Name = "Firefox",       StableId = "sha-ffx",    Index = 2 },
            };

            cache.Save(id, apps);
            var loaded = cache.TryLoad(id);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(3, loaded.Count);
            for (int i = 0; i < apps.Count; i++)
            {
                Assert.AreEqual(apps[i].Name,     loaded[i].Name);
                Assert.AreEqual(apps[i].StableId, loaded[i].StableId);
                Assert.AreEqual(apps[i].Index,    loaded[i].Index);
            }
        }

        [TestMethod]
        public void TryLoad_Missing_ReturnsNull()
        {
            var cache = new AppCache(tempDir);
            Assert.IsNull(cache.TryLoad(Guid.NewGuid()));
        }

        [TestMethod]
        public void TryLoad_CorruptFile_ReturnsNull_NotThrows()
        {
            var cache = new AppCache(tempDir);
            var id = Guid.NewGuid();
            var cacheDir = Path.Combine(tempDir, "cache");
            Directory.CreateDirectory(cacheDir);
            var path = Path.Combine(cacheDir, id.ToString() + ".json");
            File.WriteAllText(path, "{ this is not valid json at all ]]]");

            var loaded = cache.TryLoad(id);

            Assert.IsNull(loaded);
            Assert.IsFalse(File.Exists(path), "corrupt cache should be cleared");
        }

        [TestMethod]
        public void Save_EmptyList_RoundTrips()
        {
            var cache = new AppCache(tempDir);
            var id = Guid.NewGuid();

            cache.Save(id, new List<RemoteApp>());
            var loaded = cache.TryLoad(id);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(0, loaded.Count);
        }

        [TestMethod]
        public void Save_Creates_CacheDir()
        {
            var cache = new AppCache(tempDir);
            var id = Guid.NewGuid();
            var cacheDir = Path.Combine(tempDir, "cache");

            Assert.IsFalse(Directory.Exists(cacheDir));
            cache.Save(id, new List<RemoteApp>());
            Assert.IsTrue(Directory.Exists(cacheDir));
        }
    }
}
