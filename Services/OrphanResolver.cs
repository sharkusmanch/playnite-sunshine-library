using System;
using System.Collections.Generic;

namespace SunshineLibrary.Services
{
    /// <summary>
    /// A game identity as the orphan logic sees it: the composite "hostId:appStableId"
    /// GameId plus the display name. Lets the decision rules be exercised without a
    /// live Playnite database.
    /// </summary>
    public class GameRef
    {
        public string GameId { get; set; }
        public string Name { get; set; }

        public GameRef() { }

        public GameRef(string gameId, string name)
        {
            GameId = gameId;
            Name = name;
        }
    }

    /// <summary>An existing game whose GameId should be rewritten to a newly-yielded one.</summary>
    public class Rebind
    {
        public string OldGameId { get; set; }
        public string NewGameId { get; set; }
    }

    /// <summary>
    /// Pure decision rules for "which games no longer exist upstream" and "which
    /// yielded apps are actually renames of games we already have". Kept free of
    /// PlayniteApi so the rules can be tested directly — the plugin only maps
    /// database rows in and results back out.
    /// </summary>
    public static class OrphanResolver
    {
        /// <summary>Returns the host portion of a "hostId:appStableId" GameId, or null if malformed.</summary>
        public static string HostIdOf(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            var parts = gameId.Split(new[] { ':' }, 2);
            return parts.Length == 2 ? parts[0] : null;
        }

        /// <summary>
        /// Decides which of <paramref name="existingGameIds"/> are confirmed orphans.
        ///
        /// A game is an orphan only when we have positive evidence it is gone:
        ///   - its host is no longer configured at all (removed from settings), or
        ///   - its host synced LIVE this pass and did not yield the game.
        ///
        /// Everything else is "couldn't determine" and is left alone:
        ///   - host configured but disabled — the user turned it off, the apps are still
        ///     there. Deleting them would destroy playtime on a settings toggle.
        ///   - host offline / auth-broken / served from cache — can't distinguish
        ///     "removed" from "unreachable".
        ///   - host synced live but yielded NOTHING (<paramref name="emptyYieldHostIds"/>) —
        ///     a config reset or an over-broad exclusion filter looks identical to
        ///     "every app was deleted", and the destructive reading is never the safe one.
        /// </summary>
        public static HashSet<string> ResolveOrphans(
            IEnumerable<string> existingGameIds,
            ICollection<string> yieldedGameIds,
            ICollection<string> liveHostIds,
            ICollection<string> configuredHostIds,
            ICollection<string> emptyYieldHostIds)
        {
            var orphans = new HashSet<string>(StringComparer.Ordinal);
            if (existingGameIds == null) return orphans;

            foreach (var gameId in existingGameIds)
            {
                var hostId = HostIdOf(gameId);
                if (hostId == null) continue;

                if (configuredHostIds == null || !configuredHostIds.Contains(hostId))
                {
                    orphans.Add(gameId); // host removed from settings entirely
                    continue;
                }

                if (liveHostIds == null || !liveHostIds.Contains(hostId)) continue;
                if (emptyYieldHostIds != null && emptyYieldHostIds.Contains(hostId)) continue;

                if (yieldedGameIds == null || !yieldedGameIds.Contains(gameId))
                {
                    orphans.Add(gameId);
                }
            }

            return orphans;
        }

        /// <summary>
        /// Server identity is unstable: Apollo rotates `uuid` when an app is removed and
        /// re-added; Sunshine's `sha256(name|cmd)` changes when the launch cmd is edited.
        /// Either edit would otherwise create a duplicate game and orphan the original's
        /// playtime and overrides.
        ///
        /// So: for each yielded GameId we don't already have, look for an existing game on
        /// the same host with the same name (case-insensitive) and rewrite it to the new ID.
        ///
        /// Only games that are themselves orphaned this pass are eligible. A game whose
        /// GameId is still in the yield is alive, and rebinding it would hand its playtime
        /// to a different app while leaving its own ID to be re-imported as a duplicate —
        /// which is exactly what happens when one host has two apps sharing a name.
        ///
        /// Known gap: if the user renames in Playnite AND the app is re-added server-side,
        /// the name no longer matches and we create a duplicate. Acceptable for v1.
        /// </summary>
        public static List<Rebind> ResolveRebinds(
            IEnumerable<GameRef> existingGames,
            IEnumerable<GameRef> yieldedGames)
        {
            var rebinds = new List<Rebind>();
            if (existingGames == null || yieldedGames == null) return rebinds;

            var existingIds = new HashSet<string>(StringComparer.Ordinal);
            var existingList = new List<GameRef>();
            foreach (var g in existingGames)
            {
                if (g == null || string.IsNullOrEmpty(g.GameId)) continue;
                existingIds.Add(g.GameId);
                existingList.Add(g);
            }

            var yieldedList = new List<GameRef>();
            var yieldedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in yieldedGames)
            {
                if (g == null || string.IsNullOrEmpty(g.GameId)) continue;
                yieldedIds.Add(g.GameId);
                yieldedList.Add(g);
            }

            // Index rebind candidates by (hostId, lowercased name). Only games absent from
            // this pass's yield qualify — see the guard rationale above. Allocating the
            // index once is cheap; scanning per yielded app would be O(N*M).
            var candidates = new Dictionary<string, GameRef>(StringComparer.Ordinal);
            foreach (var g in existingList)
            {
                if (yieldedIds.Contains(g.GameId)) continue;
                var key = CandidateKey(g);
                // First one wins — two orphans sharing a name on one host are already
                // ambiguous and we can't auto-resolve which is which.
                if (key != null && !candidates.ContainsKey(key)) candidates[key] = g;
            }

            foreach (var meta in yieldedList)
            {
                if (existingIds.Contains(meta.GameId)) continue; // exact match wins

                var key = CandidateKey(meta);
                GameRef orphan;
                if (key == null || !candidates.TryGetValue(key, out orphan)) continue;

                rebinds.Add(new Rebind { OldGameId = orphan.GameId, NewGameId = meta.GameId });

                // Keep the indices honest so a second yielded app with the same name on
                // the same host doesn't re-bind to the orphan we just consumed.
                candidates.Remove(key);
                existingIds.Remove(orphan.GameId);
                existingIds.Add(meta.GameId);
            }

            return rebinds;
        }

        private static string CandidateKey(GameRef g)
        {
            if (g == null || string.IsNullOrEmpty(g.Name)) return null;
            var hostId = HostIdOf(g.GameId);
            return hostId == null ? null : hostId + ":" + g.Name.ToLowerInvariant();
        }
    }
}
