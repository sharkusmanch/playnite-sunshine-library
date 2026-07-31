using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Models;
using SunshineLibrary.Services;
using System;
using System.Linq;

namespace SunshineLibrary.Tests
{
    /// <summary>
    /// Shape of the <c>GameMetadata</c> handed to Playnite for a streamed app.
    /// </summary>
    [TestClass]
    public class SyncMetadataTests
    {
        private static SyncService NewService() => new SyncService(Guid.NewGuid(), null);

        private static HostConfig Host(string label = "ALLY", ServerType type = ServerType.Sunshine) =>
            new HostConfig { Id = Guid.NewGuid(), Label = label, Address = "host.local", ServerType = type };

        private static RemoteApp App(string name = "Hades") =>
            new RemoteApp { Name = name, StableId = "sha-hades", Index = 0 };

        [TestMethod]
        public void BuildMeta_ReportsNoLocalInstallFootprint()
        {
            var meta = NewService().BuildMeta(Host(), App(), fromCache: false);

            // Playnite's size scanner measures InstallDirectory (or Roms) and nothing
            // else, so an empty directory guarantees it can never attribute a local
            // folder's size to a game that only exists on a remote host.
            Assert.AreEqual(string.Empty, meta.InstallDirectory);
            Assert.AreEqual((ulong?)0, meta.InstallSize);
        }

        [TestMethod]
        public void BuildMeta_FromCache_AlsoReportsNoLocalInstallFootprint()
        {
            var meta = NewService().BuildMeta(Host(), App(), fromCache: true);

            Assert.AreEqual(string.Empty, meta.InstallDirectory);
            Assert.AreEqual((ulong?)0, meta.InstallSize);
        }

        [TestMethod]
        public void BuildMeta_KeepsIdentityAndInstallState()
        {
            var host = Host();
            var meta = NewService().BuildMeta(host, App(), fromCache: false);

            Assert.AreEqual($"{host.Id}:sha-hades", meta.GameId);
            Assert.AreEqual("Hades", meta.Name);
            Assert.IsTrue(meta.IsInstalled, "streamed apps present on the host are 'installed'");
        }

        [TestMethod]
        public void BuildMeta_FromCache_TagsOffline()
        {
            var meta = NewService().BuildMeta(Host(), App(), fromCache: true);

            Assert.IsTrue(
                meta.Tags.Any(t => t.ToString().IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0),
                "cache-fallback entries carry the offline tag");
        }

        [TestMethod]
        public void BuildMeta_LiveFetch_HasNoOfflineTag()
        {
            var meta = NewService().BuildMeta(Host(), App(), fromCache: false);

            var hasOffline = meta.Tags != null && meta.Tags.Any(
                t => t.ToString().IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.IsFalse(hasOffline);
        }

        [TestMethod]
        public void BuildMeta_SourceNamesTheHost()
        {
            var sunshine = NewService().BuildMeta(Host("ALLY"), App(), fromCache: false);
            var vibepollo = NewService().BuildMeta(Host("DEN", ServerType.Vibepollo), App(), fromCache: false);

            Assert.AreEqual("Sunshine: ALLY", sunshine.Source.ToString());
            Assert.AreEqual("Vibepollo: DEN", vibepollo.Source.ToString());
        }
    }
}
