using Playnite.SDK;
using SunshineLibrary.Models;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// Per-game streaming override dialog (PLAN §10). Thin wrapper around
    /// <see cref="StreamOverridesEditor"/> — adds the modal chrome, OK/Cancel/Clear
    /// buttons, and result semantics.
    /// </summary>
    public class GameOverridesWindow
    {
        public StreamOverrides Result { get; private set; }
        public bool CleanClear { get; private set; }

        private readonly IPlayniteAPI api;
        private readonly string gameName;
        private readonly StreamOverrides working;
        private readonly StreamOverrides effectiveFallback;

        private Window dialog;

        public GameOverridesWindow(IPlayniteAPI api, string gameName, StreamOverrides current, StreamOverrides effectiveFallback)
        {
            this.api = api;
            this.gameName = gameName;
            this.effectiveFallback = effectiveFallback ?? StreamOverrides.BuiltinDefault;
            working = Clone(current ?? new StreamOverrides());
        }

        public bool ShowDialog(Window owner)
        {
            dialog = api.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
            });
            dialog.Owner = owner;
            dialog.Title = string.Format(L("LOC_SunshineLibrary_OverrideDialog_Title"), gameName);
            dialog.Width = 620;
            dialog.Height = 620;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ResizeMode = ResizeMode.CanResizeWithGrip;
            dialog.Content = Build();
            return dialog.ShowDialog() == true;
        }

        private UIElement Build()
        {
            var outer = new DockPanel { LastChildFill = true };
            outer.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

            // --- footer (docked bottom) ---
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 10, 16, 10),
            };
            var clearBtn = new Button
            {
                Content = L("LOC_SunshineLibrary_OverrideDialog_ClearAll"),
                MinWidth = 160,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 12, 0),
            };
            clearBtn.Click += (_, __) =>
            {
                CleanClear = true;
                Result = null;
                dialog.DialogResult = true;
                dialog.Close();
            };
            var okBtn = new Button
            {
                Content = L("LOC_SunshineLibrary_Common_Ok"),
                MinWidth = 100,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
            };
            okBtn.Click += (_, __) =>
            {
                Result = IsEmpty(working) ? null : working;
                dialog.DialogResult = true;
                dialog.Close();
            };
            var cancelBtn = new Button
            {
                Content = L("LOC_SunshineLibrary_Common_Cancel"),
                MinWidth = 100,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true,
            };
            cancelBtn.Click += (_, __) => { dialog.DialogResult = false; dialog.Close(); };
            buttons.Children.Add(clearBtn);
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);
            DockPanel.SetDock(buttons, Dock.Bottom);
            outer.Children.Add(buttons);

            // --- scrollable body ---
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16),
            };
            var editor = new StreamOverridesEditor(working, effectiveFallback);
            scroll.Content = editor.Build();
            outer.Children.Add(scroll);
            return outer;
        }

        private static bool IsEmpty(StreamOverrides o)
        {
            return o.ResolutionMode == null && string.IsNullOrEmpty(o.ResolutionStatic)
                && o.FpsMode == null && o.FpsStatic == null
                && o.Hdr == null
                && o.BitrateKbps == null
                && string.IsNullOrEmpty(o.VideoCodec)
                && string.IsNullOrEmpty(o.DisplayMode)
                && string.IsNullOrEmpty(o.AudioConfig)
                && o.Yuv444 == null && o.FramePacing == null && o.GameOptimization == null && o.PerformanceOverlay == null
                && o.VSync == null && string.IsNullOrEmpty(o.VideoDecoder)
                && o.AudioOnHost == null && o.MuteOnFocusLoss == null && o.KeepAwake == null
                && string.IsNullOrEmpty(o.CaptureSystemKeys)
                && string.IsNullOrEmpty(o.ExtraArgs);
        }

        private static StreamOverrides Clone(StreamOverrides s) => new StreamOverrides
        {
            ResolutionMode = s.ResolutionMode,
            ResolutionStatic = s.ResolutionStatic,
            FpsMode = s.FpsMode,
            FpsStatic = s.FpsStatic,
            Hdr = s.Hdr,
            BitrateKbps = s.BitrateKbps,
            VideoCodec = s.VideoCodec,
            DisplayMode = s.DisplayMode,
            AudioConfig = s.AudioConfig,
            Yuv444 = s.Yuv444,
            FramePacing = s.FramePacing,
            GameOptimization = s.GameOptimization,
            PerformanceOverlay = s.PerformanceOverlay,
            VSync = s.VSync,
            VideoDecoder = s.VideoDecoder,
            AudioOnHost = s.AudioOnHost,
            MuteOnFocusLoss = s.MuteOnFocusLoss,
            KeepAwake = s.KeepAwake,
            CaptureSystemKeys = s.CaptureSystemKeys,
            ExtraArgs = s.ExtraArgs,
        };

        private static string L(string key)
        {
            var s = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }
    }
}
