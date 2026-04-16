namespace SunshineLibrary.Models
{
    /// <summary>
    /// Which streaming-server flavor a host runs. Sunshine (LizardByte upstream)
    /// and Apollo (ClassicOldSong fork) share the admin API surface but Apollo
    /// adds endpoints (OTP, client management, virtual display) and guarantees
    /// stable per-app uuids that Sunshine doesn't.
    /// Vibepollo (Nonary/Vibepollo) is an Apollo fork that additionally exposes
    /// Playnite library metadata via /api/playnite/* endpoints.
    /// </summary>
    public enum ServerType
    {
        Unknown  = 0,
        Sunshine = 1,
        Apollo   = 2,
        Vibepollo = 3,
    }
}
