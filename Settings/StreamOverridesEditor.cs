using Playnite.SDK;
using SunshineLibrary.Models;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// Reusable WPF form for editing a <see cref="StreamOverrides"/>. Same surface used by
    /// the per-game right-click dialog, per-host defaults, and the global "Streaming Defaults"
    /// tab in addon settings. Mutates the passed-in <paramref name="working"/> reference live
    /// (no explicit Commit step) so the enclosing flow — dialog OK or settings EndEdit — just
    /// reads the current values.
    ///
    /// The <paramref name="effectiveFallback"/> is an already-merged preview of "what this
    /// override would resolve to if every field were Inherit." For per-game: global + host.
    /// For host defaults: global. For global defaults: built-in (Auto for res/fps/HDR).
    /// </summary>
    public class StreamOverridesEditor
    {
        private readonly StreamOverrides working;
        private readonly StreamOverrides effectiveFallback;

        // All field labels are the same width so controls start at a consistent column.
        private const double LabelWidth = 145;

        public StreamOverridesEditor(StreamOverrides working, StreamOverrides effectiveFallback)
        {
            this.working = working;
            this.effectiveFallback = effectiveFallback ?? StreamOverrides.BuiltinDefault;
        }

        public UIElement Build()
        {
            var root = new StackPanel();
            root.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

            root.Children.Add(Heading(L("LOC_SunshineLibrary_OverrideDialog_Group_Display")));
            root.Children.Add(BuildResolutionRow());
            root.Children.Add(BuildFpsRow());
            root.Children.Add(BuildHdrRow());

            root.Children.Add(Heading(L("LOC_SunshineLibrary_OverrideDialog_Group_Encoding")));
            root.Children.Add(BuildBitrateRow());
            root.Children.Add(BuildCodecRow());
            root.Children.Add(BuildScalarBoolRow(
                L("LOC_SunshineLibrary_OverrideField_Yuv444"),
                working.Yuv444, v => working.Yuv444 = v, effectiveFallback.Yuv444));

            root.Children.Add(Heading(L("LOC_SunshineLibrary_OverrideDialog_Group_Performance")));
            root.Children.Add(BuildScalarBoolRow(
                L("LOC_SunshineLibrary_OverrideField_FramePacing"),
                working.FramePacing, v => working.FramePacing = v, effectiveFallback.FramePacing));
            root.Children.Add(BuildScalarBoolRow(
                L("LOC_SunshineLibrary_OverrideField_GameOptimization"),
                working.GameOptimization, v => working.GameOptimization = v, effectiveFallback.GameOptimization));
            root.Children.Add(BuildScalarBoolRow(
                L("LOC_SunshineLibrary_OverrideField_ShowStats"),
                working.ShowStats, v => working.ShowStats = v, effectiveFallback.ShowStats));

            root.Children.Add(Heading(L("LOC_SunshineLibrary_OverrideDialog_Group_Output")));
            root.Children.Add(BuildDisplayModeRow());
            root.Children.Add(BuildAudioRow());

            root.Children.Add(Heading(L("LOC_SunshineLibrary_OverrideDialog_Group_Advanced")));
            root.Children.Add(BuildExtraArgsRow());

            return root;
        }

        // ─── Row builders ──────────────────────────────────────────────────────

        private FrameworkElement BuildResolutionRow()
        {
            var modeCombo = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
            modeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Inherit"), Tag = ResolutionMode.Inherit });
            modeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Auto"), Tag = ResolutionMode.Auto });
            modeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Static"), Tag = ResolutionMode.Static });
            SelectByTag(modeCombo, working.ResolutionMode ?? ResolutionMode.Inherit);

            var staticBox = new TextBox
            {
                Width = 120,
                Text = working.ResolutionStatic ?? string.Empty,
                ToolTip = "1920x1080",
                IsEnabled = working.ResolutionMode == ResolutionMode.Static,
            };

            modeCombo.SelectionChanged += (_, __) =>
            {
                var m = (ResolutionMode)((ComboBoxItem)modeCombo.SelectedItem).Tag;
                working.ResolutionMode = m == ResolutionMode.Inherit ? (ResolutionMode?)null : m;
                staticBox.IsEnabled = m == ResolutionMode.Static;
                if (m != ResolutionMode.Static) working.ResolutionStatic = null;
            };
            staticBox.TextChanged += (_, __) =>
            {
                working.ResolutionStatic = string.IsNullOrWhiteSpace(staticBox.Text) ? null : staticBox.Text.Trim();
            };

            var controls = HorizontalRow();
            controls.Children.Add(modeCombo);
            controls.Children.Add(staticBox);
            return FieldBlock(L("LOC_SunshineLibrary_OverrideField_Resolution"), controls, FormatResolutionFallback());
        }

        private FrameworkElement BuildFpsRow()
        {
            var modeCombo = new ComboBox { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
            modeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Inherit"), Tag = FpsMode.Inherit });
            modeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Auto"), Tag = FpsMode.Auto });
            modeCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Static"), Tag = FpsMode.Static });
            SelectByTag(modeCombo, working.FpsMode ?? FpsMode.Inherit);

            var staticBox = new TextBox
            {
                Width = 80,
                Text = working.FpsStatic?.ToString() ?? string.Empty,
                IsEnabled = working.FpsMode == FpsMode.Static,
            };

            modeCombo.SelectionChanged += (_, __) =>
            {
                var m = (FpsMode)((ComboBoxItem)modeCombo.SelectedItem).Tag;
                working.FpsMode = m == FpsMode.Inherit ? (FpsMode?)null : m;
                staticBox.IsEnabled = m == FpsMode.Static;
                if (m != FpsMode.Static) working.FpsStatic = null;
            };
            staticBox.TextChanged += (_, __) =>
            {
                working.FpsStatic = int.TryParse(staticBox.Text, out var fps) ? fps : (int?)null;
            };

            var controls = HorizontalRow();
            controls.Children.Add(modeCombo);
            controls.Children.Add(staticBox);
            return FieldBlock(L("LOC_SunshineLibrary_OverrideField_Fps"), controls, FormatFpsFallback());
        }

        private FrameworkElement BuildHdrRow()
        {
            var combo = new ComboBox { Width = 160 };
            combo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Inherit"), Tag = HdrMode.Inherit });
            combo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Auto"), Tag = HdrMode.Auto });
            combo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Hdr_On"), Tag = HdrMode.On });
            combo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Hdr_Off"), Tag = HdrMode.Off });
            SelectByTag(combo, working.Hdr ?? HdrMode.Inherit);
            combo.SelectionChanged += (_, __) =>
            {
                var m = (HdrMode)((ComboBoxItem)combo.SelectedItem).Tag;
                working.Hdr = m == HdrMode.Inherit ? (HdrMode?)null : m;
            };
            return FieldBlock(L("LOC_SunshineLibrary_OverrideField_Hdr"), combo, FormatHdrFallback());
        }

        private FrameworkElement BuildBitrateRow()
        {
            var overrideBox = new CheckBox
            {
                Content = L("LOC_SunshineLibrary_Override_OverrideField"),
                IsChecked = working.BitrateKbps.HasValue,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var valueBox = new TextBox
            {
                Width = 100,
                Text = working.BitrateKbps?.ToString() ?? string.Empty,
                IsEnabled = working.BitrateKbps.HasValue,
            };
            var unitLabel = new TextBlock
            {
                Text = " Kbps",
                VerticalAlignment = VerticalAlignment.Center,
            };

            overrideBox.Checked += (_, __) =>
            {
                valueBox.IsEnabled = true;
                if (int.TryParse(valueBox.Text, out var v)) working.BitrateKbps = v;
            };
            overrideBox.Unchecked += (_, __) =>
            {
                valueBox.IsEnabled = false;
                working.BitrateKbps = null;
            };
            valueBox.TextChanged += (_, __) =>
            {
                if (overrideBox.IsChecked == true)
                    working.BitrateKbps = int.TryParse(valueBox.Text, out var v) ? v : (int?)null;
            };

            var controls = HorizontalRow();
            controls.Children.Add(overrideBox);
            controls.Children.Add(valueBox);
            controls.Children.Add(unitLabel);

            var hint = effectiveFallback.BitrateKbps.HasValue
                ? string.Format(L("LOC_SunshineLibrary_Override_Fallback_Bitrate"), effectiveFallback.BitrateKbps)
                : L("LOC_SunshineLibrary_Override_Fallback_BitrateAuto");
            return FieldBlock(L("LOC_SunshineLibrary_OverrideField_Bitrate"), controls, hint);
        }

        private FrameworkElement BuildCodecRow()
        {
            var combo = BuildStringEnumCombo(working.VideoCodec, new[] { "auto", "H.264", "HEVC", "AV1" });
            combo.SelectionChanged += (_, __) => working.VideoCodec = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            return FieldBlock(
                L("LOC_SunshineLibrary_OverrideField_VideoCodec"), combo,
                FormatFallback("LOC_SunshineLibrary_Override_Fallback_Codec", effectiveFallback.VideoCodec));
        }

        private FrameworkElement BuildDisplayModeRow()
        {
            var combo = BuildStringEnumCombo(working.DisplayMode, new[] { "fullscreen", "windowed", "borderless" });
            combo.SelectionChanged += (_, __) => working.DisplayMode = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            return FieldBlock(
                L("LOC_SunshineLibrary_OverrideField_DisplayMode"), combo,
                FormatFallback("LOC_SunshineLibrary_Override_Fallback_DisplayMode", effectiveFallback.DisplayMode));
        }

        private FrameworkElement BuildAudioRow()
        {
            var combo = BuildStringEnumCombo(working.AudioConfig, new[] { "stereo", "5.1-surround", "7.1-surround" });
            combo.SelectionChanged += (_, __) => working.AudioConfig = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
            return FieldBlock(
                L("LOC_SunshineLibrary_OverrideField_AudioConfig"), combo,
                FormatFallback("LOC_SunshineLibrary_Override_Fallback_AudioConfig", effectiveFallback.AudioConfig));
        }

        private FrameworkElement BuildScalarBoolRow(string label, bool? currentValue, Action<bool?> setter, bool? fallback)
        {
            var overrideBox = new CheckBox
            {
                Content = L("LOC_SunshineLibrary_Override_OverrideField"),
                IsChecked = currentValue.HasValue,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var valueCombo = new ComboBox { Width = 80, IsEnabled = currentValue.HasValue };
            valueCombo.Items.Add(new ComboBoxItem { Content = "On", Tag = true });
            valueCombo.Items.Add(new ComboBoxItem { Content = "Off", Tag = false });
            valueCombo.SelectedIndex = currentValue == true ? 0 : 1;

            overrideBox.Checked += (_, __) =>
            {
                valueCombo.IsEnabled = true;
                setter((bool)((ComboBoxItem)valueCombo.SelectedItem).Tag);
            };
            overrideBox.Unchecked += (_, __) =>
            {
                valueCombo.IsEnabled = false;
                setter(null);
            };
            valueCombo.SelectionChanged += (_, __) =>
            {
                if (overrideBox.IsChecked == true)
                    setter((bool)((ComboBoxItem)valueCombo.SelectedItem).Tag);
            };

            var controls = HorizontalRow();
            controls.Children.Add(overrideBox);
            controls.Children.Add(valueCombo);

            var hint = fallback.HasValue
                ? string.Format(L("LOC_SunshineLibrary_Override_Fallback_Bool"), fallback.Value ? "On" : "Off")
                : null;
            return FieldBlock(label, controls, hint);
        }

        private FrameworkElement BuildExtraArgsRow()
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };

            // Label above the text box — the field name is too long for the shared label column.
            outer.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_OverrideField_ExtraArgs"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3),
            });

            var box = new TextBox
            {
                Text = working.ExtraArgs ?? string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            box.TextChanged += (_, __) =>
            {
                working.ExtraArgs = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();
            };
            outer.Children.Add(box);

            var help = new TextBlock
            {
                Text = L("LOC_SunshineLibrary_OverrideField_ExtraArgs_Help"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
            };
            help.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            outer.Children.Add(help);
            return outer;
        }

        // ─── Fallback hint formatting ──────────────────────────────────────────

        private string FormatResolutionFallback()
        {
            if (effectiveFallback.ResolutionMode == ResolutionMode.Auto) return L("LOC_SunshineLibrary_Override_Fallback_Auto");
            if (effectiveFallback.ResolutionMode == ResolutionMode.Static)
                return string.Format(L("LOC_SunshineLibrary_Override_Fallback_Static"), effectiveFallback.ResolutionStatic);
            return null;
        }

        private string FormatFpsFallback()
        {
            if (effectiveFallback.FpsMode == FpsMode.Auto) return L("LOC_SunshineLibrary_Override_Fallback_Auto");
            if (effectiveFallback.FpsMode == FpsMode.Static && effectiveFallback.FpsStatic.HasValue)
                return string.Format(L("LOC_SunshineLibrary_Override_Fallback_Static"), effectiveFallback.FpsStatic);
            return null;
        }

        private string FormatHdrFallback()
        {
            if (effectiveFallback.Hdr == HdrMode.Auto) return L("LOC_SunshineLibrary_Override_Fallback_Auto");
            if (effectiveFallback.Hdr == HdrMode.On) return string.Format(L("LOC_SunshineLibrary_Override_Fallback_Static"), "On");
            if (effectiveFallback.Hdr == HdrMode.Off) return string.Format(L("LOC_SunshineLibrary_Override_Fallback_Static"), "Off");
            return null;
        }

        private string FormatFallback(string key, string value)
            => string.IsNullOrEmpty(value)
                ? null  // no inherited value to show — suppress hint
                : string.Format(L(key), value);

        // ─── Layout helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Creates a field block: label left-aligned in a fixed-width column, control(s) to
        /// the right, and a hint line indented to start under the control.
        /// </summary>
        private FrameworkElement FieldBlock(string labelText, UIElement control, string hint)
        {
            var outer = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            outer.Children.Add(LabeledControlRow(labelText, control));
            if (!string.IsNullOrEmpty(hint)) outer.Children.Add(FallbackHint(hint));
            return outer;
        }

        private static StackPanel LabeledControlRow(string labelText, UIElement control)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = labelText,
                Width = LabelWidth,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(control);
            return row;
        }

        private static StackPanel HorizontalRow() => new StackPanel { Orientation = Orientation.Horizontal };

        private static TextBlock Heading(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 12, 0, 6),
        };

        private TextBlock FallbackHint(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(LabelWidth, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            return tb;
        }

        private static ComboBox BuildStringEnumCombo(string current, string[] values)
        {
            var combo = new ComboBox { Width = 160 };
            combo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Override_Mode_Inherit"), Tag = (string)null });
            int sel = 0;
            for (int i = 0; i < values.Length; i++)
            {
                combo.Items.Add(new ComboBoxItem { Content = values[i], Tag = values[i] });
                if (string.Equals(values[i], current, StringComparison.OrdinalIgnoreCase)) sel = i + 1;
            }
            combo.SelectedIndex = sel;
            return combo;
        }

        private static void SelectByTag<T>(ComboBox combo, T tag)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem it && Equals(it.Tag, tag))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private static string L(string key)
        {
            var s = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }
    }
}
