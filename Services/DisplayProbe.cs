using Playnite.SDK;
using SunshineLibrary.Models;
using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace SunshineLibrary.Services
{
    /// <summary>
    /// Best-effort probe of the client's current display: resolution, refresh rate, HDR.
    /// Used to resolve `Auto` StreamOverrides at launch time. Never throws — any failure
    /// yields ClientDisplayInfo.Unknown and the corresponding moonlight flag is omitted.
    /// </summary>
    public static class DisplayProbe
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static ClientDisplayInfo Detect()
        {
            try
            {
                var screen = ResolveTargetScreen();
                if (screen == null) return ClientDisplayInfo.Unknown;

                // Screen.Bounds gives physical pixels in a DPI-aware process (which Playnite is).
                // This is more reliable than EnumDisplaySettingsEx, which can fail silently in
                // settings-dialog contexts. Refresh rate still comes from EnumDisplaySettingsEx
                // but falls back to 0 rather than blocking the whole result.
                int width = screen.Bounds.Width;
                int height = screen.Bounds.Height;
                if (width <= 0 || height <= 0) return ClientDisplayInfo.Unknown;

                int refresh = TryGetRefreshHz(screen.DeviceName);
                bool hdr = TryGetHdrEnabled(screen.DeviceName);
                return new ClientDisplayInfo
                {
                    Width = width,
                    Height = height,
                    RefreshHz = refresh,
                    HdrEnabled = hdr,
                };
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "DisplayProbe: detection failed, returning Unknown.");
            }
            return ClientDisplayInfo.Unknown;
        }

        // --- device resolution ---------------------------------------------------

        private static System.Windows.Forms.Screen ResolveTargetScreen()
        {
            // Prefer the display that contains Playnite's main window; fall back to primary.
            try
            {
                var mw = Application.Current?.MainWindow;
                if (mw != null && mw.IsVisible)
                {
                    var src = PresentationSource.FromVisual(mw);
                    if (src?.CompositionTarget != null)
                    {
                        var topLeft = mw.PointToScreen(new Point(0, 0));
                        var screen = System.Windows.Forms.Screen.FromPoint(
                            new System.Drawing.Point((int)topLeft.X, (int)topLeft.Y));
                        if (screen != null) return screen;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "DisplayProbe: could not resolve Playnite window's display, falling back to primary.");
            }
            return System.Windows.Forms.Screen.PrimaryScreen;
        }

        // --- Win32 EnumDisplaySettings for refresh rate --------------------------

        private const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, int dwFlags);

        private static int TryGetRefreshHz(string deviceName)
        {
            try
            {
                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
                    return dm.dmDisplayFrequency;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "DisplayProbe: could not read refresh rate.");
            }
            return 0;
        }

        // --- Win32 DisplayConfigGetDeviceInfo for HDR (Win10 1803+) --------------

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO_BLOB
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            // Union payload is 48 bytes; we don't need to unpack for HDR query.
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            public byte[] payload;
        }

        private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags; // bit 0: advancedColorSupported; bit 1: advancedColorEnabled; bit 2: wideColorEnforced; bit 3: advancedColorForceDisabled
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPaths,
            [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numModes,
            [Out] DISPLAYCONFIG_MODE_INFO_BLOB[] modes, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME req);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO req);

        private static bool TryGetHdrEnabled(string deviceName)
        {
            try
            {
                uint numPaths = 0, numModes = 0;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes) != 0) return false;

                var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                var modes = new DISPLAYCONFIG_MODE_INFO_BLOB[numModes];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0) return false;

                for (int i = 0; i < numPaths; i++)
                {
                    var src = paths[i].sourceInfo;

                    var nameReq = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                            size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME)),
                            adapterId = src.adapterId,
                            id = src.id,
                        }
                    };
                    if (DisplayConfigGetDeviceInfo(ref nameReq) != 0) continue;

                    if (!string.Equals(nameReq.viewGdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) continue;

                    var colorReq = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                            size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO)),
                            adapterId = paths[i].targetInfo.adapterId,
                            id = paths[i].targetInfo.id,
                        }
                    };
                    if (DisplayConfigGetDeviceInfo(ref colorReq) != 0) continue;

                    // bit 1 (0x2) = advancedColorEnabled
                    return (colorReq.flags & 0x2) != 0;
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "DisplayProbe: HDR detection failed.");
            }
            return false;
        }
    }
}
