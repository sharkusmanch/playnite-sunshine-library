using Playnite.SDK;
using SunshineLibrary.Models;
using SunshineLibrary.Services.Clients;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

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
        private readonly HostConfig _host;
        private readonly RemoteApp _app;

        private Window dialog;
        private StreamOverridesEditor _editor;
        private TextBox _previewBox;
        private TextBlock _previewHint;

        public GameOverridesWindow(IPlayniteAPI api, string gameName, StreamOverrides current, StreamOverrides effectiveFallback,
            HostConfig host = null, RemoteApp app = null)
        {
            this.api = api;
            this.gameName = gameName;
            this.effectiveFallback = effectiveFallback ?? StreamOverrides.BuiltinDefault;
            working = Clone(current ?? new StreamOverrides());
            _host = host;
            _app = app;
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
            dialog.Height = 700;
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

            // --- preview strip (docked bottom, above footer) ---
            var previewStrip = BuildPreviewStrip();
            DockPanel.SetDock(previewStrip, Dock.Bottom);
            outer.Children.Add(previewStrip);

            // --- scrollable body ---
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16),
            };
            _editor = new StreamOverridesEditor(working, effectiveFallback);
            _editor.OnWorkingChanged = RefreshPreview;
            scroll.Content = _editor.Build();
            outer.Children.Add(scroll);
            RefreshPreview();
            return outer;
        }

        private UIElement BuildPreviewStrip()
        {
            var border = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 8, 16, 8),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "TextBrushDarker");

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Preview_Label"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
            });

            _previewBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 11,
                MaxHeight = 72,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            panel.Children.Add(_previewBox);

            _previewHint = new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Preview_NoDisplayHint"),
                FontStyle = FontStyles.Italic,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            _previewHint.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            panel.Children.Add(_previewHint);

            border.Child = panel;
            return border;
        }

        private void RefreshPreview()
        {
            if (_previewBox == null) return;

            // Mirror MoonlightCompatibleClient.BuildLaunch: BuiltinDefault.MergedWith(effectiveFallback.MergedWith(working))
            var fullMerged = StreamOverrides.BuiltinDefault.MergedWith(effectiveFallback.MergedWith(working));

            var previewHost = _host ?? new HostConfig { Address = "<host>" };
            var previewApp = _app ?? new RemoteApp { Name = "<app>" };

            var args = MoonlightCompatibleClient.ComposeArgs(previewHost, previewApp, fullMerged, _editor.Display);
            _previewBox.Text = PasteArguments.Build(args);

            _previewHint.Visibility = _editor.Display.IsKnown ? Visibility.Collapsed : Visibility.Visible;
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
