using Playnite.SDK;
using SunshineLibrary.Models;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// Multi-select bulk override editor (PLAN §10 Tier 1). Per-field "Apply" checkbox
    /// scopes which fields the user wants to push across all selected games. Fields
    /// left un-checked do not touch the target's existing values.
    ///
    /// To explicitly wipe overrides for selected games, use the "Clear overrides on
    /// selection" right-click menu item instead.
    /// </summary>
    public class BulkOverridesWindow
    {
        /// <summary>Fields the user ticked for bulk application plus the values they set.</summary>
        public BulkOverrideEdit Result { get; private set; }

        private readonly IPlayniteAPI api;
        private readonly int selectedCount;
        private Window dialog;

        // Apply-checkboxes per field.
        private CheckBox applyResolution, applyFps, applyHdr, applyBitrate;
        private CheckBox applyCodec, applyDisplayMode, applyAudio, applyYuv444;
        private CheckBox applyFramePacing, applyGameOpt, applyPerfOverlay;
        private CheckBox applyVSync, applyVideoDecoder, applyAudioOnHost;
        private CheckBox applyMuteOnFocusLoss, applyKeepAwake, applyCaptureSysKeys, applyExtraArgs;

        // Values.
        private ComboBox resModeCombo; private TextBox resStaticBox;
        private ComboBox fpsModeCombo; private TextBox fpsStaticBox;
        private ComboBox hdrCombo;
        private TextBox bitrateBox;
        private ComboBox codecCombo, displayModeCombo, audioCombo;
        private ComboBox yuv444Combo, framePacingCombo, gameOptCombo, perfOverlayCombo;
        private ComboBox vSyncCombo, videoDecoderCombo, audioOnHostCombo;
        private ComboBox muteOnFocusLossCombo, keepAwakeCombo, captureSysKeysCombo;
        private TextBox extraArgsBox;

        public BulkOverridesWindow(IPlayniteAPI api, int selectedCount)
        {
            this.api = api;
            this.selectedCount = selectedCount;
        }

        public bool ShowDialog(Window owner)
        {
            dialog = api.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
            });
            dialog.Owner = owner;
            dialog.Title = L("LOC_SunshineLibrary_BulkDialog_Title");
            dialog.Width = 620;
            dialog.Height = 620;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ResizeMode = ResizeMode.CanResizeWithGrip;
            dialog.Content = Build();
            return dialog.ShowDialog() == true;
        }

        private UIElement Build()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(16),
            };
            scroll.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            var root = new StackPanel();

            root.Children.Add(new TextBlock
            {
                Text = string.Format(L("LOC_SunshineLibrary_BulkDialog_Heading"), selectedCount),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            root.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_BulkDialog_Help"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 0, 0, 12),
            });

            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_Resolution"), out applyResolution, BuildResolutionControls()));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_Fps"), out applyFps, BuildFpsControls()));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_Hdr"), out applyHdr, BuildHdrControls()));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_Bitrate"), out applyBitrate, BuildBitrateControls()));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_VideoCodec"), out applyCodec, BuildCodecControls()));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_DisplayMode"), out applyDisplayMode, BuildDisplayModeControls()));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_AudioConfig"), out applyAudio, BuildAudioControls()));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_Yuv444"), out applyYuv444, BuildBoolControls(out yuv444Combo)));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_FramePacing"), out applyFramePacing, BuildBoolControls(out framePacingCombo)));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_GameOptimization"), out applyGameOpt, BuildBoolControls(out gameOptCombo)));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_ShowStats"), out applyPerfOverlay, BuildBoolControls(out perfOverlayCombo)));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_VSync"), out applyVSync, BuildBoolControls(out vSyncCombo)));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_VideoDecoder"), out applyVideoDecoder, BuildVideoDecoderControls()));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_AudioOnHost"), out applyAudioOnHost, BuildBoolControls(out audioOnHostCombo)));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_MuteOnFocusLoss"), out applyMuteOnFocusLoss, BuildBoolControls(out muteOnFocusLossCombo)));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_KeepAwake"), out applyKeepAwake, BuildBoolControls(out keepAwakeCombo)));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_CaptureSystemKeys"), out applyCaptureSysKeys, BuildCaptureSystemKeysControls()));
            root.Children.Add(TriStateRow(L("LOC_SunshineLibrary_OverrideField_ExtraArgs"), out applyExtraArgs, BuildExtraArgsControls()));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var okBtn = new Button
            {
                Content = string.Format(L("LOC_SunshineLibrary_BulkDialog_Apply"), selectedCount),
                MinWidth = 200,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
            };
            okBtn.Click += OnApply;
            var cancelBtn = new Button
            {
                Content = L("LOC_SunshineLibrary_Common_Cancel"),
                MinWidth = 100,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true,
            };
            cancelBtn.Click += (_, __) => { dialog.DialogResult = false; dialog.Close(); };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);
            root.Children.Add(buttons);

            scroll.Content = root;
            return scroll;
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var edit = new BulkOverrideEdit();

            if (applyResolution.IsChecked == true)
            {
                edit.SetResolution = true;
                var mode = (ResolutionMode)((ComboBoxItem)resModeCombo.SelectedItem).Tag;
                edit.ResolutionMode = mode == ResolutionMode.Inherit ? (ResolutionMode?)null : mode;
                edit.ResolutionStatic = mode == ResolutionMode.Static ? resStaticBox.Text?.Trim() : null;
            }
            if (applyFps.IsChecked == true)
            {
                edit.SetFps = true;
                var mode = (FpsMode)((ComboBoxItem)fpsModeCombo.SelectedItem).Tag;
                edit.FpsMode = mode == FpsMode.Inherit ? (FpsMode?)null : mode;
                if (mode == FpsMode.Static && int.TryParse(fpsStaticBox.Text, out var fps)) edit.FpsStatic = fps;
            }
            if (applyHdr.IsChecked == true)
            {
                edit.SetHdr = true;
                var hdr = (HdrMode)((ComboBoxItem)hdrCombo.SelectedItem).Tag;
                edit.Hdr = hdr == HdrMode.Inherit ? (HdrMode?)null : hdr;
            }
            if (applyBitrate.IsChecked == true)
            {
                edit.SetBitrate = true;
                if (int.TryParse(bitrateBox.Text, out var kbps)) edit.BitrateKbps = kbps;
            }
            if (applyCodec.IsChecked == true) { edit.SetVideoCodec = true; edit.VideoCodec = ReadStringEnum(codecCombo); }
            if (applyDisplayMode.IsChecked == true) { edit.SetDisplayMode = true; edit.DisplayMode = ReadStringEnum(displayModeCombo); }
            if (applyAudio.IsChecked == true) { edit.SetAudioConfig = true; edit.AudioConfig = ReadStringEnum(audioCombo); }
            if (applyYuv444.IsChecked == true) { edit.SetYuv444 = true; edit.Yuv444 = ReadBool(yuv444Combo); }
            if (applyFramePacing.IsChecked == true) { edit.SetFramePacing = true; edit.FramePacing = ReadBool(framePacingCombo); }
            if (applyGameOpt.IsChecked == true) { edit.SetGameOpt = true; edit.GameOptimization = ReadBool(gameOptCombo); }
            if (applyPerfOverlay.IsChecked == true) { edit.SetPerfOverlay = true; edit.PerformanceOverlay = ReadBool(perfOverlayCombo); }
            if (applyVSync.IsChecked == true) { edit.SetVSync = true; edit.VSync = ReadBool(vSyncCombo); }
            if (applyVideoDecoder.IsChecked == true) { edit.SetVideoDecoder = true; edit.VideoDecoder = ReadStringEnum(videoDecoderCombo); }
            if (applyAudioOnHost.IsChecked == true) { edit.SetAudioOnHost = true; edit.AudioOnHost = ReadBool(audioOnHostCombo); }
            if (applyMuteOnFocusLoss.IsChecked == true) { edit.SetMuteOnFocusLoss = true; edit.MuteOnFocusLoss = ReadBool(muteOnFocusLossCombo); }
            if (applyKeepAwake.IsChecked == true) { edit.SetKeepAwake = true; edit.KeepAwake = ReadBool(keepAwakeCombo); }
            if (applyCaptureSysKeys.IsChecked == true) { edit.SetCaptureSysKeys = true; edit.CaptureSystemKeys = ReadStringEnum(captureSysKeysCombo); }
            if (applyExtraArgs.IsChecked == true)
            {
                edit.SetExtraArgs = true;
                edit.ExtraArgs = string.IsNullOrWhiteSpace(extraArgsBox.Text) ? null : extraArgsBox.Text.Trim();
            }

            if (!edit.HasAnyField)
            {
                api.Dialogs.ShowMessage(
                    L("LOC_SunshineLibrary_BulkDialog_NothingSelected"),
                    L("LOC_SunshineLibrary_Name"));
                return;
            }

            Result = edit;
            dialog.DialogResult = true;
            dialog.Close();
        }

        // --- value control builders ------------------------------------------

        private UIElement BuildResolutionControls()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            resModeCombo = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
            resModeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Inherit"), Tag = ResolutionMode.Inherit });
            resModeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Auto"), Tag = ResolutionMode.Auto });
            resModeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Static"), Tag = ResolutionMode.Static });
            resModeCombo.SelectedIndex = 1; // Auto is the practical default for bulk
            resStaticBox = new TextBox { Width = 120 };
            row.Children.Add(resModeCombo);
            row.Children.Add(resStaticBox);
            return row;
        }

        private UIElement BuildFpsControls()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            fpsModeCombo = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
            fpsModeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Inherit"), Tag = FpsMode.Inherit });
            fpsModeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Auto"), Tag = FpsMode.Auto });
            fpsModeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Static"), Tag = FpsMode.Static });
            fpsModeCombo.SelectedIndex = 1;
            fpsStaticBox = new TextBox { Width = 80 };
            row.Children.Add(fpsModeCombo);
            row.Children.Add(fpsStaticBox);
            return row;
        }

        private UIElement BuildHdrControls()
        {
            hdrCombo = new ComboBox { Width = 160 };
            hdrCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Inherit"), Tag = HdrMode.Inherit });
            hdrCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Auto"), Tag = HdrMode.Auto });
            hdrCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Hdr_On"), Tag = HdrMode.On });
            hdrCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Hdr_Off"), Tag = HdrMode.Off });
            hdrCombo.SelectedIndex = 1;
            return hdrCombo;
        }

        private UIElement BuildBitrateControls()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            bitrateBox = new TextBox { Width = 100 };
            row.Children.Add(bitrateBox);
            row.Children.Add(new TextBlock { Text = " Kbps", VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        private UIElement BuildCodecControls() => codecCombo = BuildStringEnumCombo(new[] { "auto", "H.264", "HEVC", "AV1" });
        private UIElement BuildDisplayModeControls() => displayModeCombo = BuildStringEnumCombo(new[] { "fullscreen", "windowed", "borderless" });
        private UIElement BuildAudioControls() => audioCombo = BuildStringEnumCombo(new[] { "stereo", "5.1-surround", "7.1-surround" });
        private UIElement BuildVideoDecoderControls() => videoDecoderCombo = BuildStringEnumCombo(new[] { "auto", "software", "hardware" });
        private UIElement BuildCaptureSystemKeysControls() => captureSysKeysCombo = BuildStringEnumCombo(new[] { "never", "fullscreen", "always" });

        private UIElement BuildBoolControls(out ComboBox combo)
        {
            combo = new ComboBox { Width = 100 };
            combo.Items.Add(new ComboBoxItem { Content = "On", Tag = true });
            combo.Items.Add(new ComboBoxItem { Content = "Off", Tag = false });
            combo.SelectedIndex = 0;
            return combo;
        }

        private UIElement BuildExtraArgsControls()
        {
            extraArgsBox = new TextBox { Width = 400 };
            return extraArgsBox;
        }

        private static ComboBox BuildStringEnumCombo(string[] values)
        {
            var combo = new ComboBox { Width = 160 };
            foreach (var v in values) combo.Items.Add(new ComboBoxItem { Content = v, Tag = v });
            combo.SelectedIndex = 0;
            return combo;
        }

        // --- row layout ------------------------------------------------------

        private static FrameworkElement TriStateRow(string label, out CheckBox applyBox, UIElement valueControl)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            applyBox = new CheckBox
            {
                Content = string.Format(L("LOC_SunshineLibrary_BulkDialog_ApplyField"), label),
                Margin = new Thickness(0, 0, 0, 2),
                IsChecked = false,
            };
            panel.Children.Add(applyBox);
            var indented = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
            indented.Children.Add(valueControl);
            panel.Children.Add(indented);
            return panel;
        }

        private static bool? ReadBool(ComboBox combo) => (bool)((ComboBoxItem)combo.SelectedItem).Tag;
        private static string ReadStringEnum(ComboBox combo) => (string)((ComboBoxItem)combo.SelectedItem).Tag;

        private static string L(string key)
        {
            var s = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }
    }

    /// <summary>Descriptor produced by the bulk dialog. Has flag per field to distinguish "leave alone" from "set to null".</summary>
    public class BulkOverrideEdit
    {
        public bool SetResolution; public ResolutionMode? ResolutionMode; public string ResolutionStatic;
        public bool SetFps; public FpsMode? FpsMode; public int? FpsStatic;
        public bool SetHdr; public HdrMode? Hdr;
        public bool SetBitrate; public int? BitrateKbps;
        public bool SetVideoCodec; public string VideoCodec;
        public bool SetDisplayMode; public string DisplayMode;
        public bool SetAudioConfig; public string AudioConfig;
        public bool SetYuv444; public bool? Yuv444;
        public bool SetFramePacing; public bool? FramePacing;
        public bool SetGameOpt; public bool? GameOptimization;
        public bool SetPerfOverlay; public bool? PerformanceOverlay;
        public bool SetVSync; public bool? VSync;
        public bool SetVideoDecoder; public string VideoDecoder;
        public bool SetAudioOnHost; public bool? AudioOnHost;
        public bool SetMuteOnFocusLoss; public bool? MuteOnFocusLoss;
        public bool SetKeepAwake; public bool? KeepAwake;
        public bool SetCaptureSysKeys; public string CaptureSystemKeys;
        public bool SetExtraArgs; public string ExtraArgs;

        public bool HasAnyField =>
            SetResolution || SetFps || SetHdr || SetBitrate || SetVideoCodec ||
            SetDisplayMode || SetAudioConfig || SetYuv444 || SetFramePacing ||
            SetGameOpt || SetPerfOverlay || SetVSync || SetVideoDecoder ||
            SetAudioOnHost || SetMuteOnFocusLoss || SetKeepAwake || SetCaptureSysKeys ||
            SetExtraArgs;

        /// <summary>Apply selected fields to an existing override, returning the modified override.</summary>
        public StreamOverrides ApplyTo(StreamOverrides existing)
        {
            var o = existing != null ? CloneOverride(existing) : new StreamOverrides();
            if (SetResolution) { o.ResolutionMode = ResolutionMode; o.ResolutionStatic = ResolutionStatic; }
            if (SetFps) { o.FpsMode = FpsMode; o.FpsStatic = FpsStatic; }
            if (SetHdr) { o.Hdr = Hdr; }
            if (SetBitrate) { o.BitrateKbps = BitrateKbps; }
            if (SetVideoCodec) { o.VideoCodec = VideoCodec; }
            if (SetDisplayMode) { o.DisplayMode = DisplayMode; }
            if (SetAudioConfig) { o.AudioConfig = AudioConfig; }
            if (SetYuv444) { o.Yuv444 = Yuv444; }
            if (SetFramePacing) { o.FramePacing = FramePacing; }
            if (SetGameOpt) { o.GameOptimization = GameOptimization; }
            if (SetPerfOverlay) { o.PerformanceOverlay = PerformanceOverlay; }
            if (SetVSync) { o.VSync = VSync; }
            if (SetVideoDecoder) { o.VideoDecoder = VideoDecoder; }
            if (SetAudioOnHost) { o.AudioOnHost = AudioOnHost; }
            if (SetMuteOnFocusLoss) { o.MuteOnFocusLoss = MuteOnFocusLoss; }
            if (SetKeepAwake) { o.KeepAwake = KeepAwake; }
            if (SetCaptureSysKeys) { o.CaptureSystemKeys = CaptureSystemKeys; }
            if (SetExtraArgs) { o.ExtraArgs = ExtraArgs; }
            return o;
        }

        private static StreamOverrides CloneOverride(StreamOverrides s) => new StreamOverrides
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
    }
}
