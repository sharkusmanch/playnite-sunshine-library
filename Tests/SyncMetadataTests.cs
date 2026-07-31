using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using SunshineLibrary.Models;
using SunshineLibrary.Services;
using System;
using System.Collections.Generic;
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

        // --- configurable platform ------------------------------------------------

        [TestMethod]
        public void BuildMeta_NoOptions_KeepsBuiltinPlatformSpec()
        {
            var meta = NewService().BuildMeta(Host(), App(), fromCache: false, options: null);

            var platform = meta.Platforms.Single() as MetadataSpecProperty;
            Assert.IsNotNull(platform, "an unconfigured platform stays a built-in specification");
            Assert.AreEqual("pc_windows", platform.Id);
        }

        [TestMethod]
        public void BuildMeta_BlankPlatform_FallsBackToBuiltinSpec()
        {
            foreach (var blank in new[] { null, "", "   " })
            {
                var options = new LibraryMetadataOptions { PlatformName = blank };
                var meta = NewService().BuildMeta(Host(), App(), fromCache: false, options);

                var platform = meta.Platforms.Single() as MetadataSpecProperty;
                Assert.IsNotNull(platform, $"blank platform '{blank ?? "<null>"}' should fall back");
                Assert.AreEqual("pc_windows", platform.Id);
            }
        }

        [TestMethod]
        public void BuildMeta_ConfiguredPlatform_MatchedByName()
        {
            var options = new LibraryMetadataOptions { PlatformName = "  Steam Deck  " };
            var meta = NewService().BuildMeta(Host(), App(), fromCache: false, options);

            var platform = meta.Platforms.Single() as MetadataNameProperty;
            Assert.IsNotNull(platform, "a configured platform is matched by name");
            Assert.AreEqual("Steam Deck", platform.Name, "surrounding whitespace is trimmed");
        }

        // --- configurable tags ------------------------------------------------------

        [TestMethod]
        public void BuildMeta_ConfiguredTags_AreApplied()
        {
            var options = new LibraryMetadataOptions { Tags = new[] { "Streamed", "Remote" } };
            var meta = NewService().BuildMeta(Host(), App(), fromCache: false, options);

            var names = meta.Tags.OfType<MetadataNameProperty>().Select(t => t.Name).ToList();
            CollectionAssert.Contains(names, "Streamed");
            CollectionAssert.Contains(names, "Remote");
        }

        [TestMethod]
        public void BuildMeta_ConfiguredTags_CoexistWithHostDerivedTags()
        {
            var app = new RemoteApp
            {
                Name = "Hades",
                StableId = "sha-hades",
                PluginName = "Steam",
                Categories = new List<string> { "Roguelike" },
            };
            var options = new LibraryMetadataOptions { Tags = new[] { "Streamed" } };

            var meta = NewService().BuildMeta(Host(), app, fromCache: true, options);

            var names = meta.Tags.OfType<MetadataNameProperty>().Select(t => t.Name).ToList();
            CollectionAssert.Contains(names, "Streamed", "configured tag");
            CollectionAssert.Contains(names, "Steam", "host library-source tag survives");
            CollectionAssert.Contains(names, "Roguelike", "host category tag survives");
            Assert.IsTrue(names.Any(n => n.IndexOf("offline", StringComparison.OrdinalIgnoreCase) >= 0),
                "offline marker survives");
        }

        [TestMethod]
        public void CleanTags_TrimsDropsBlanksAndDeduplicates()
        {
            var options = new LibraryMetadataOptions
            {
                Tags = new[] { " Streamed ", "", "   ", "streamed", "Remote", null, "Remote" },
            };

            CollectionAssert.AreEqual(
                new[] { "Streamed", "Remote" },
                options.CleanTags().ToList(),
                "trimmed, blank-free, first-spelling-wins de-duplication");
        }

        [TestMethod]
        public void CleanTags_NullList_YieldsNothing()
        {
            Assert.AreEqual(0, new LibraryMetadataOptions().CleanTags().Count());
        }

        // --- "is anything configured?" gate for the apply-to-existing pass -----------

        [TestMethod]
        public void IsConfigured_FalseWhenNothingSet()
        {
            Assert.IsFalse(new LibraryMetadataOptions().IsConfigured);
            Assert.IsFalse(new LibraryMetadataOptions { PlatformName = "  " }.IsConfigured);
            Assert.IsFalse(new LibraryMetadataOptions { Tags = new string[0] }.IsConfigured);
            Assert.IsFalse(
                new LibraryMetadataOptions { PlatformName = "", Tags = new[] { "", "  ", null } }.IsConfigured,
                "blank-only tags do not count as configuration");
        }

        [TestMethod]
        public void IsConfigured_TrueWhenEitherIsSet()
        {
            Assert.IsTrue(new LibraryMetadataOptions { PlatformName = "Steam Deck" }.IsConfigured);
            Assert.IsTrue(new LibraryMetadataOptions { Tags = new[] { "Streamed" } }.IsConfigured);
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
