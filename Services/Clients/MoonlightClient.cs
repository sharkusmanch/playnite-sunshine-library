namespace SunshineLibrary.Services.Clients
{
    /// <summary>
    /// Upstream moonlight-qt from moonlight-stream/moonlight-qt. Thin subclass — all
    /// CLI, locator, and launch logic lives on <see cref="MoonlightCompatibleClient"/>
    /// so the Apollo-ecosystem forks (Artemis, StreamLight, etc.) can share it.
    /// </summary>
    public sealed class MoonlightClient : MoonlightCompatibleClient
    {
        public const string ClientId = "moonlight-qt";

        public override string Id => ClientId;
        public override string DisplayName => "Moonlight";

        protected override ClientLocatorConfig LocatorConfig => new ClientLocatorConfig
        {
            // Official installer ships moonlight-qt.exe; scoop's `moonlight` app packages
            // it as Moonlight.exe with a lowercase moonlight.exe shim; some winget builds
            // also use Moonlight.exe. Probe all forms.
            ExeNames = new[] { "moonlight-qt.exe", "Moonlight.exe", "moonlight.exe" },
            InstallDirNames = new[] { "Moonlight Game Streaming" },
            ScoopAppNames = new[] { "moonlight", "moonlight-qt" },
            WingetPackagePatterns = new[] { "Moonlight*", "*Moonlight*" },
        };
    }
}
