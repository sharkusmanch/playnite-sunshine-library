namespace SunshineLibrary.Models
{
    /// <summary>
    /// Which streaming-server flavor a host runs. Sunshine (LizardByte upstream)
    /// and Apollo (ClassicOldSong fork) share the admin API surface but Apollo
    /// adds endpoints (OTP, client management, virtual display) and guarantees
    /// stable per-app uuids that Sunshine doesn't.
    /// </summary>
    public enum ServerType
    {
        Unknown,
        Sunshine,
        Apollo
    }
}
