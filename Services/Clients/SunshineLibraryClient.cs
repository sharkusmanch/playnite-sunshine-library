using Playnite.SDK;
using Playnite.SDK.Plugins;
using SunshineLibrary.Settings;
using System.Diagnostics;
using System.Linq;

namespace SunshineLibrary.Services.Clients
{
    /// <summary>
    /// Playnite's "client" button in the library header. We expose "Open host web UI"
    /// for the first configured host since Playnite only surfaces one entry here;
    /// multiple-host users still have per-host menu items elsewhere.
    /// </summary>
    public class SunshineLibraryClient : LibraryClient
    {
        private readonly SunshineLibrarySettingsViewModel settings;

        public SunshineLibraryClient(SunshineLibrarySettingsViewModel settings)
        {
            this.settings = settings;
        }

        public override string Icon => null; // inherits plugin icon
        public override bool IsInstalled => settings?.Settings?.Hosts?.Any(h => h != null && h.Enabled) == true;

        public override void Open()
        {
            var host = settings?.Settings?.Hosts?.FirstOrDefault(h => h != null && h.Enabled);
            if (host == null) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"https://{host.Address}:{host.Port}/",
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Default browser not available, or URL blocked — silent fail is acceptable here.
            }
        }
    }
}
