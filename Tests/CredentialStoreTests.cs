using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Services;
using System;
using System.IO;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class CredentialStoreTests
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
        public void Save_Then_Load_RoundTrips()
        {
            var store = new CredentialStore(tempDir);
            var id = Guid.NewGuid();

            store.Save(id, "admin", "s3cr3t");
            var loaded = store.TryLoad(id);

            Assert.IsTrue(loaded.HasValue);
            Assert.AreEqual("admin", loaded.Value.User);
            Assert.AreEqual("s3cr3t", loaded.Value.Password);
        }

        [TestMethod]
        public void TryLoad_Missing_ReturnsNull()
        {
            var store = new CredentialStore(tempDir);
            Assert.IsNull(store.TryLoad(Guid.NewGuid()));
        }

        [TestMethod]
        public void Delete_Removes_BlobFromDisk()
        {
            var store = new CredentialStore(tempDir);
            var id = Guid.NewGuid();
            store.Save(id, "u", "p");

            Assert.IsTrue(store.TryLoad(id).HasValue);
            store.Delete(id);

            Assert.IsNull(store.TryLoad(id));
        }

        [TestMethod]
        public void DeleteAll_Removes_EveryBlob()
        {
            var store = new CredentialStore(tempDir);
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            store.Save(a, "u1", "p1");
            store.Save(b, "u2", "p2");

            store.DeleteAll();

            Assert.IsNull(store.TryLoad(a));
            Assert.IsNull(store.TryLoad(b));
            var credsDir = Path.Combine(tempDir, "creds");
            Assert.AreEqual(0, Directory.GetFiles(credsDir, "*.dat").Length);
        }

        [TestMethod]
        public void TryLoad_CorruptFile_ReturnsNull_AndDeletesIt()
        {
            var store = new CredentialStore(tempDir);
            var id = Guid.NewGuid();
            var credsDir = Path.Combine(tempDir, "creds");
            Directory.CreateDirectory(credsDir);
            var path = Path.Combine(credsDir, id.ToString() + ".dat");
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF });

            var loaded = store.TryLoad(id);

            Assert.IsNull(loaded);
            Assert.IsFalse(File.Exists(path), "corrupt blob should be cleared");
        }

        [TestMethod]
        public void Save_EmptyStrings_RoundTrips()
        {
            var store = new CredentialStore(tempDir);
            var id = Guid.NewGuid();

            store.Save(id, "", "");
            var loaded = store.TryLoad(id);

            Assert.IsTrue(loaded.HasValue);
            Assert.AreEqual("", loaded.Value.User);
            Assert.AreEqual("", loaded.Value.Password);
        }
    }
}
