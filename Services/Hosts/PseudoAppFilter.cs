using SunshineLibrary.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SunshineLibrary.Services.Hosts
{
    /// <summary>
    /// Default-excluded names for Sunshine/Apollo auto-entries plus per-host user list.
    /// Matches case-insensitively on exact name. See PLAN §11.
    /// </summary>
    public static class PseudoAppFilter
    {
        public static IReadOnlyList<string> DefaultsFor(ServerType flavor)
        {
            // Sunshine and Apollo both ship "Desktop" by default; Steam Big Picture and Terminate
            // are optional entries added by installer helpers. Seeded into HostConfig.ExcludedAppNames
            // when a host is added; users can remove defaults they want to keep.
            switch (flavor)
            {
                case ServerType.Apollo:
                case ServerType.Sunshine:
                    return new[] { "Desktop", "Steam Big Picture", "Terminate" };
                default:
                    return Array.Empty<string>();
            }
        }

        public static IEnumerable<RemoteApp> Apply(IEnumerable<RemoteApp> apps, HostConfig host)
        {
            if (apps == null) yield break;
            var set = new HashSet<string>(host.ExcludedAppNames ?? Enumerable.Empty<string>(),
                                          StringComparer.OrdinalIgnoreCase);
            foreach (var a in apps)
            {
                if (a?.Name == null) continue;
                if (set.Contains(a.Name)) continue;
                yield return a;
            }
        }
    }
}
