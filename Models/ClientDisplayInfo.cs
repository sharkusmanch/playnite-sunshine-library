namespace SunshineLibrary.Models
{
    /// <summary>Snapshot of the client's current display at launch time, used to resolve Auto overrides.</summary>
    public class ClientDisplayInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int RefreshHz { get; set; }
        public bool HdrEnabled { get; set; }

        public string AsResolution => $"{Width}x{Height}";

        public static ClientDisplayInfo Unknown => new ClientDisplayInfo
        {
            Width = 0,
            Height = 0,
            RefreshHz = 0,
            HdrEnabled = false,
        };

        public bool IsKnown => Width > 0 && Height > 0;
    }
}
