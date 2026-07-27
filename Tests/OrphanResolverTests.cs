using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Models;
using SunshineLibrary.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class OrphanResolverTests
    {
        private const string HostA = "11111111-1111-1111-1111-111111111111";
        private const string HostB = "22222222-2222-2222-2222-222222222222";

        private static HashSet<string> Set(params string[] items) =>
            new HashSet<string>(items, StringComparer.Ordinal);

        private static HashSet<string> Resolve(
            IEnumerable<string> existing,
            IEnumerable<string> yielded = null,
            IEnumerable<string> live = null,
            IEnumerable<string> configured = null,
            IEnumerable<string> emptyYield = null)
        {
            return OrphanResolver.ResolveOrphans(
                existing,
                Set((yielded ?? Enumerable.Empty<string>()).ToArray()),
                Set((live ?? Enumerable.Empty<string>()).ToArray()),
                Set((configured ?? Enumerable.Empty<string>()).ToArray()),
                Set((emptyYield ?? Enumerable.Empty<string>()).ToArray()));
        }

        // --- ResolveOrphans -------------------------------------------------

        [TestMethod]
        public void Orphan_WhenHostRemovedFromSettings()
        {
            var orphans = Resolve(
                existing: new[] { HostA + ":app1" },
                configured: new[] { HostB });

            CollectionAssert.AreEquivalent(new[] { HostA + ":app1" }, orphans.ToList());
        }

        /// <summary>
        /// Regression: unchecking "Enabled" on a host must not orphan its games. The apps
        /// are still on the server; with AutoRemoveOrphanedGames on, treating them as
        /// orphans deletes playtime, covers and overrides on a settings toggle.
        ///
        /// The original bug was in the host set the caller passed in, so this drives the
        /// scope selection rather than hardcoding the ID list: HostScope.Configured is what
        /// the plugin feeds to ResolveOrphans. Swapping it for HostScope.Active — the bug —
        /// drops HostA from the configured set and fails this test.
        /// </summary>
        [TestMethod]
        public void NotOrphan_WhenHostConfiguredButDisabled()
        {
            var settingsHosts = new List<HostConfig>
            {
                new HostConfig { Id = Guid.Parse(HostA), Label = "living room", Enabled = false },
            };

            var orphans = OrphanResolver.ResolveOrphans(
                new[] { HostA + ":app1", HostA + ":app2" },
                Set(),                                  // disabled host never syncs, so nothing yielded
                Set(),                                  // ...and it is not live
                Set(HostScope.Configured(settingsHosts).Select(h => h.Id.ToString()).ToArray()),
                Set());

            Assert.AreEqual(0, orphans.Count, "disabling a host must not orphan its games");
        }

        [TestMethod]
        public void HostScope_ConfiguredKeepsDisabledHosts_ActiveDropsThem()
        {
            var hosts = new List<HostConfig>
            {
                new HostConfig { Id = Guid.Parse(HostA), Enabled = true },
                new HostConfig { Id = Guid.Parse(HostB), Enabled = false },
                null,
            };

            CollectionAssert.AreEquivalent(
                new[] { HostA, HostB },
                HostScope.Configured(hosts).Select(h => h.Id.ToString()).ToList());

            CollectionAssert.AreEquivalent(
                new[] { HostA },
                HostScope.Active(hosts).Select(h => h.Id.ToString()).ToList());
        }

        [TestMethod]
        public void HostScope_HandlesNullHostList()
        {
            Assert.AreEqual(0, HostScope.Configured(null).Count());
            Assert.AreEqual(0, HostScope.Active(null).Count());
        }

        [TestMethod]
        public void Orphan_WhenLiveHostDidNotYieldGame()
        {
            var orphans = Resolve(
                existing: new[] { HostA + ":app1", HostA + ":app2" },
                yielded: new[] { HostA + ":app1" },
                live: new[] { HostA },
                configured: new[] { HostA });

            CollectionAssert.AreEquivalent(new[] { HostA + ":app2" }, orphans.ToList());
        }

        [TestMethod]
        public void NotOrphan_WhenLiveHostYieldedGame()
        {
            var orphans = Resolve(
                existing: new[] { HostA + ":app1" },
                yielded: new[] { HostA + ":app1" },
                live: new[] { HostA },
                configured: new[] { HostA });

            Assert.AreEqual(0, orphans.Count);
        }

        /// <summary>
        /// Regression: a host that authenticates but returns an empty app list — config
        /// reset, or an ExcludedAppNames pattern that matched everything — looks exactly
        /// like "the user deleted every app". Pruning on that signal wipes the host.
        /// </summary>
        [TestMethod]
        public void NotOrphan_WhenLiveHostYieldedNothing()
        {
            var orphans = Resolve(
                existing: new[] { HostA + ":app1", HostA + ":app2" },
                yielded: new string[0],
                live: new[] { HostA },
                configured: new[] { HostA },
                emptyYield: new[] { HostA });

            Assert.AreEqual(0, orphans.Count);
        }

        /// <summary>
        /// The guard must not spill onto other hosts. Inputs mirror what the plugin actually
        /// produces: HostB is live and yielded something (so it is genuinely missing app9),
        /// while HostA is live and yielded nothing.
        /// </summary>
        [TestMethod]
        public void EmptyYieldGuard_IsScopedToTheAffectedHostOnly()
        {
            var orphans = Resolve(
                existing: new[] { HostA + ":app1", HostB + ":app9", HostB + ":app8" },
                yielded: new[] { HostB + ":app8" },
                live: new[] { HostA, HostB },
                configured: new[] { HostA, HostB },
                emptyYield: new[] { HostA });

            // HostA is protected by the guard; HostB synced live and genuinely lost app9.
            CollectionAssert.AreEquivalent(new[] { HostB + ":app9" }, orphans.ToList());
        }

        [TestMethod]
        public void NotOrphan_WhenHostServedFromCacheOrOffline()
        {
            // Configured and reachable-ish, but not in the live set (cache fallback).
            var orphans = Resolve(
                existing: new[] { HostA + ":app1" },
                yielded: new[] { HostA + ":app1" },
                configured: new[] { HostA });

            Assert.AreEqual(0, orphans.Count);
        }

        [TestMethod]
        public void MalformedGameIds_AreIgnored()
        {
            var orphans = Resolve(
                existing: new[] { "no-colon-here", "", null },
                configured: new[] { HostA });

            Assert.AreEqual(0, orphans.Count);
        }

        // --- ResolveRebinds -------------------------------------------------

        [TestMethod]
        public void Rebind_WhenAppIdChangedButNameMatches()
        {
            var rebinds = OrphanResolver.ResolveRebinds(
                new[] { new GameRef(HostA + ":old-uuid", "Cyberpunk 2077") },
                new[] { new GameRef(HostA + ":new-uuid", "Cyberpunk 2077") });

            Assert.AreEqual(1, rebinds.Count);
            Assert.AreEqual(HostA + ":old-uuid", rebinds[0].OldGameId);
            Assert.AreEqual(HostA + ":new-uuid", rebinds[0].NewGameId);
        }

        [TestMethod]
        public void NoRebind_WhenGameIdAlreadyMatches()
        {
            var rebinds = OrphanResolver.ResolveRebinds(
                new[] { new GameRef(HostA + ":app1", "Hades") },
                new[] { new GameRef(HostA + ":app1", "Hades") });

            Assert.AreEqual(0, rebinds.Count);
        }

        /// <summary>
        /// Regression: two apps sharing a name on one host. The existing game is still in
        /// the yield under its own ID, so it is alive — rebinding it would migrate its
        /// playtime to the wrong app AND leave its own ID to be re-imported as a duplicate.
        /// </summary>
        [TestMethod]
        public void NoRebind_WhenNameMatchIsStillLiveInThisYield()
        {
            var rebinds = OrphanResolver.ResolveRebinds(
                new[] { new GameRef(HostA + ":app1", "Desktop") },
                new[]
                {
                    new GameRef(HostA + ":app1", "Desktop"),  // still there
                    new GameRef(HostA + ":app2", "Desktop"),  // a second app with the same name
                });

            Assert.AreEqual(0, rebinds.Count);
        }

        [TestMethod]
        public void NoRebind_AcrossDifferentHosts()
        {
            var rebinds = OrphanResolver.ResolveRebinds(
                new[] { new GameRef(HostA + ":old", "Hollow Knight") },
                new[] { new GameRef(HostB + ":new", "Hollow Knight") });

            Assert.AreEqual(0, rebinds.Count);
        }

        [TestMethod]
        public void Rebind_NameMatchIsCaseInsensitive()
        {
            var rebinds = OrphanResolver.ResolveRebinds(
                new[] { new GameRef(HostA + ":old", "HOLLOW KNIGHT") },
                new[] { new GameRef(HostA + ":new", "hollow knight") });

            Assert.AreEqual(1, rebinds.Count);
        }

        [TestMethod]
        public void Rebind_ConsumesEachOrphanOnce()
        {
            // Two orphans share a name; two new IDs arrive. Only one rebind is defensible —
            // the pairing beyond the first is ambiguous.
            var rebinds = OrphanResolver.ResolveRebinds(
                new[]
                {
                    new GameRef(HostA + ":old1", "Steam Big Picture"),
                    new GameRef(HostA + ":old2", "Steam Big Picture"),
                },
                new[]
                {
                    new GameRef(HostA + ":new1", "Steam Big Picture"),
                    new GameRef(HostA + ":new2", "Steam Big Picture"),
                });

            Assert.AreEqual(1, rebinds.Count);
            Assert.AreEqual(HostA + ":new1", rebinds[0].NewGameId);
        }

        [TestMethod]
        public void NoRebind_WhenNoNameMatches()
        {
            var rebinds = OrphanResolver.ResolveRebinds(
                new[] { new GameRef(HostA + ":old", "Hades") },
                new[] { new GameRef(HostA + ":new", "Hades II") });

            Assert.AreEqual(0, rebinds.Count);
        }

        [TestMethod]
        public void Rebinds_HandleNullAndMalformedEntries()
        {
            var rebinds = OrphanResolver.ResolveRebinds(
                new[] { null, new GameRef(HostA + ":old", null), new GameRef("bad-id", "Hades") },
                new[] { null, new GameRef(HostA + ":new", "Hades") });

            Assert.AreEqual(0, rebinds.Count);
        }

        [TestMethod]
        public void HostIdOf_ParsesCompositeId()
        {
            Assert.AreEqual(HostA, OrphanResolver.HostIdOf(HostA + ":some:app:id"));
            Assert.IsNull(OrphanResolver.HostIdOf("no-colon"));
            Assert.IsNull(OrphanResolver.HostIdOf(null));
        }
    }
}
