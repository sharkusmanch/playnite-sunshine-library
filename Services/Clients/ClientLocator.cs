using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SunshineLibrary.Services.Clients
{
    /// <summary>
    /// Install-location hints for a specific streaming client (Moonlight, Artemis,
    /// StreamLight, etc.). Consumed by <see cref="ClientLocator"/> to probe PATH,
    /// Scoop, Winget, and standard installer directories without duplicating the
    /// probing logic per client.
    /// </summary>
    public class ClientLocatorConfig
    {
        /// <summary>
        /// Executable file names to probe, in preference order. Multiple names handle
        /// distribution-specific naming: the official Moonlight installer ships
        /// <c>moonlight-qt.exe</c> but scoop's `moonlight` bucket packages it as
        /// <c>Moonlight.exe</c> with a lowercase <c>moonlight.exe</c> shim.
        /// </summary>
        public string[] ExeNames { get; set; } = Array.Empty<string>();

        /// <summary>Primary exe name — used as display fallback when no install is found.</summary>
        public string PrimaryExeName => ExeNames != null && ExeNames.Length > 0 ? ExeNames[0] : null;

        /// <summary>Subdirectory names under %ProgramFiles% / %LocalAppData%\Programs (e.g. "Moonlight Game Streaming", "Artemis").</summary>
        public string[] InstallDirNames { get; set; } = Array.Empty<string>();

        /// <summary>Scoop app bucket names (e.g. "moonlight", "moonlight-qt"). Both user and global roots are probed.</summary>
        public string[] ScoopAppNames { get; set; } = Array.Empty<string>();

        /// <summary>Winget package-name glob patterns (e.g. "Moonlight*", "*Moonlight*").</summary>
        public string[] WingetPackagePatterns { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Searches well-known and discoverable install locations for a streaming client
    /// executable. Used by both the client's default-path probe and the Settings UI
    /// "Auto-detect" button. Returned paths are deduplicated (case-insensitive) and
    /// ordered highest-priority first: PATH → Scoop user → Scoop global → Winget
    /// user-scope → Program Files → LocalAppData\Programs.
    /// </summary>
    public static class ClientLocator
    {
        public static IReadOnlyList<string> Locate(ClientLocatorConfig config)
        {
            if (config == null || config.ExeNames == null || config.ExeNames.Length == 0)
                return Array.Empty<string>();

            var found = new List<string>();
            var exeNames = config.ExeNames;

            void TryAdd(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate)) return;
                try
                {
                    if (!File.Exists(candidate)) return;
                    var resolved = Path.GetFullPath(candidate);
                    if (!found.Any(p => string.Equals(p, resolved, StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(resolved);
                    }
                }
                catch { /* path with bogus chars, permissions, etc. — skip */ }
            }

            // 1. Scoop user apps — checked BEFORE PATH because Scoop adds shim wrappers to PATH
            // that inherit the wrong working directory. The actual app exe (apps/<name>/current/)
            // must be preferred so the process launches with its own directory as working dir.
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            var scoopGlobalRoot = Environment.GetEnvironmentVariable("SCOOP_GLOBAL")
                                  ?? @"C:\ProgramData\scoop";
            if (!string.IsNullOrEmpty(userProfile))
            {
                var scoopUserRoot = Path.Combine(userProfile, "scoop");
                foreach (var app in config.ScoopAppNames)
                    foreach (var name in exeNames)
                        TryAdd(Path.Combine(scoopUserRoot, "apps", app, "current", name));
            }

            // 2. Scoop global apps
            foreach (var app in config.ScoopAppNames)
                foreach (var name in exeNames)
                    TryAdd(Path.Combine(scoopGlobalRoot, "apps", app, "current", name));

            // 3. PATH (may include Scoop shims or manually added entries)
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var rawDir in pathEnv.Split(Path.PathSeparator))
            {
                var dir = rawDir?.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var name in exeNames) TryAdd(Path.Combine(dir, name));
            }

            // 4. Scoop shims (fallback for shims not in PATH)
            if (!string.IsNullOrEmpty(userProfile))
            {
                var scoopUserRoot = Path.Combine(userProfile, "scoop");
                foreach (var name in exeNames) TryAdd(Path.Combine(scoopUserRoot, "shims", name));
            }
            foreach (var name in exeNames) TryAdd(Path.Combine(scoopGlobalRoot, "shims", name));

            // 4. Winget user-scope packages
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(localAppData))
            {
                var wingetRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(wingetRoot))
                {
                    foreach (var pattern in config.WingetPackagePatterns)
                    {
                        IEnumerable<string> pkgDirs;
                        try
                        {
                            pkgDirs = Directory.EnumerateDirectories(wingetRoot, pattern, SearchOption.TopDirectoryOnly);
                        }
                        catch { continue; }

                        foreach (var pkg in pkgDirs)
                            foreach (var name in exeNames)
                            {
                                IEnumerable<string> matches;
                                try
                                {
                                    matches = Directory.EnumerateFiles(pkg, name, SearchOption.AllDirectories);
                                }
                                catch { continue; }
                                foreach (var exe in matches) TryAdd(exe);
                            }
                    }
                }

                // 5a. Standard installer location under LocalAppData\Programs
                foreach (var dirName in config.InstallDirNames)
                    foreach (var name in exeNames)
                    {
                        TryAdd(Path.Combine(localAppData, "Programs", dirName, name));
                    }
            }

            // 5b. Standard installer locations under Program Files (64- and 32-bit)
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            foreach (var dirName in config.InstallDirNames)
                foreach (var name in exeNames)
                {
                    if (!string.IsNullOrEmpty(programFiles))
                        TryAdd(Path.Combine(programFiles, dirName, name));
                    if (!string.IsNullOrEmpty(programFilesX86))
                        TryAdd(Path.Combine(programFilesX86, dirName, name));
                }

            return found;
        }
    }
}
