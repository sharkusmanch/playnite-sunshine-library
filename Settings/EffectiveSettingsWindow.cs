using Playnite.SDK;
using SunshineLibrary.Models;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// Read-only dialog showing the fully-resolved streaming settings for a game,
    /// with per-field source attribution (built-in / global / host / per-game).
    /// Opened from the right-click menu ("View effective streaming settings…").
    /// </summary>
    public class EffectiveSettingsWindow
    {
        private readonly IPlayniteAPI api;
        private readonly string gameName;
        private readonly string hostLabel;
        private readonly List<FieldProvenance> provenance;
        private readonly string composedCommandLine;
        private readonly bool displayProbeSucceeded;
        private Window _dialog;

        public EffectiveSettingsWindow(
            IPlayniteAPI api,
            string gameName,
            string hostLabel,
            List<FieldProvenance> provenance,
            string composedCommandLine,
            bool displayProbeSucceeded)
        {
            this.api = api;
            this.gameName = gameName;
            this.hostLabel = hostLabel;
            this.provenance = provenance;
            this.composedCommandLine = composedCommandLine;
            this.displayProbeSucceeded = displayProbeSucceeded;
        }

        public void ShowDialog(Window owner)
        {
            _dialog = api.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
            });
            _dialog.Owner = owner;
            _dialog.Title = string.Format(L("LOC_SunshineLibrary_EffectiveSettingsDialog_Title"), gameName);
            _dialog.Width = 680;
            _dialog.Height = 720;
            _dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _dialog.ResizeMode = ResizeMode.CanResizeWithGrip;
            _dialog.Content = Build();
            _dialog.ShowDialog();
        }

        private UIElement Build()
        {
            var outer = new DockPanel { LastChildFill = true };
            outer.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

            // ── Footer: Close button ─────────────────────────────────────────────
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 10, 16, 10),
            };
            var closeBtn = new Button
            {
                Content = L("LOC_SunshineLibrary_EffectiveSettings_Close"),
                MinWidth = 100,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true,
                IsDefault = true,
            };
            closeBtn.Click += (_, __) => _dialog.Close();
            footer.Children.Add(closeBtn);
            DockPanel.SetDock(footer, Dock.Bottom);
            outer.Children.Add(footer);

            // ── Scrollable content ───────────────────────────────────────────────
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16),
            };

            var content = new StackPanel();

            // Display-unavailable banner
            if (!displayProbeSucceeded)
            {
                var banner = new Border
                {
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 12),
                };
                banner.SetResourceReference(Border.BorderBrushProperty, "TextBrushDarker");
                banner.Child = new TextBlock
                {
                    Text = L("LOC_SunshineLibrary_EffectiveSettings_DisplayUnavailable"),
                    TextWrapping = TextWrapping.Wrap,
                };
                content.Children.Add(banner);
            }

            // Host sub-header
            var hostSubHeader = new TextBlock
            {
                Text = string.Format(L("LOC_SunshineLibrary_EffectiveSettings_HostLabel"), this.hostLabel),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10),
            };
            hostSubHeader.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            content.Children.Add(hostSubHeader);

            // Provenance table
            content.Children.Add(BuildProvenanceGrid());

            // Command line heading + copy button
            var cmdHeader = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 16, 0, 6),
            };
            cmdHeader.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_EffectiveSettings_CommandLine"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            });
            var copyBtn = new Button
            {
                Content = L("LOC_SunshineLibrary_EffectiveSettings_Copy"),
                Padding = new Thickness(10, 3, 10, 3),
            };
            copyBtn.Click += (_, __) => System.Windows.Clipboard.SetText(composedCommandLine);
            cmdHeader.Children.Add(copyBtn);
            content.Children.Add(cmdHeader);

            // Command line TextBox (read-only, selectable, monospace)
            var cmdBox = new TextBox
            {
                Text = composedCommandLine,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 11,
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            content.Children.Add(cmdBox);

            scroll.Content = content;
            outer.Children.Add(scroll);
            return outer;
        }

        private Grid BuildProvenanceGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(175) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(85) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;

            // Column headers
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(grid, row, 0, MakeHeaderCell(L("LOC_SunshineLibrary_EffectiveSettings_ColField")));
            AddCell(grid, row, 1, MakeHeaderCell(L("LOC_SunshineLibrary_EffectiveSettings_ColValue")));
            AddCell(grid, row, 2, MakeHeaderCell(L("LOC_SunshineLibrary_EffectiveSettings_ColSource")));
            AddCell(grid, row, 3, MakeHeaderCell(L("LOC_SunshineLibrary_EffectiveSettings_ColNote")));
            row++;

            // Separator
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var sep = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 2, 0, 4) };
            sep.SetResourceReference(Border.BorderBrushProperty, "TextBrushDarker");
            Grid.SetRow(sep, row);
            Grid.SetColumnSpan(sep, 4);
            grid.Children.Add(sep);
            row++;

            foreach (var entry in provenance)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                if (entry.IsSection)
                {
                    var heading = new TextBlock
                    {
                        Text = entry.Label,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 13,
                        Margin = new Thickness(0, 10, 0, 3),
                    };
                    Grid.SetRow(heading, row);
                    Grid.SetColumnSpan(heading, 4);
                    grid.Children.Add(heading);
                }
                else
                {
                    // Col 0: field label
                    var lbl = new TextBlock
                    {
                        Text = entry.Label,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 2, 8, 2),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    AddCell(grid, row, 0, lbl);

                    // Col 1: resolved value
                    var val = new TextBlock
                    {
                        Text = entry.ResolvedValue,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 2, 8, 2),
                        TextWrapping = TextWrapping.Wrap,
                    };
                    AddCell(grid, row, 1, val);

                    // Col 2: source badge
                    AddCell(grid, row, 2, MakeBadge(entry.Source));

                    // Col 3: runtime note
                    if (!string.IsNullOrEmpty(entry.RuntimeNote))
                    {
                        var note = new TextBlock
                        {
                            Text = entry.RuntimeNote,
                            FontStyle = FontStyles.Italic,
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(4, 2, 0, 2),
                            TextWrapping = TextWrapping.Wrap,
                        };
                        note.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
                        AddCell(grid, row, 3, note);
                    }
                }

                row++;
            }

            return grid;
        }

        private static void AddCell(Grid grid, int row, int col, UIElement element)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, col);
            grid.Children.Add(element);
        }

        private static TextBlock MakeHeaderCell(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 2),
            };
            return tb;
        }

        private static Border MakeBadge(OverrideSource source)
        {
            string text;
            Color bg;
            switch (source)
            {
                case OverrideSource.Global:
                    text = L("LOC_SunshineLibrary_EffectiveSettings_Source_Global");
                    bg = Color.FromRgb(0x4A, 0x7F, 0xA5);
                    break;
                case OverrideSource.Host:
                    text = L("LOC_SunshineLibrary_EffectiveSettings_Source_Host");
                    bg = Color.FromRgb(0x4A, 0x8A, 0x4A);
                    break;
                case OverrideSource.PerGame:
                    text = L("LOC_SunshineLibrary_EffectiveSettings_Source_PerGame");
                    bg = Color.FromRgb(0xA0, 0x70, 0x40);
                    break;
                default:
                    text = L("LOC_SunshineLibrary_EffectiveSettings_Source_BuiltIn");
                    bg = Color.FromRgb(0x88, 0x88, 0x88);
                    break;
            }

            return new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 2, 5, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2),
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    FontSize = 10,
                },
            };
        }

        private static string L(string key)
        {
            var s = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }
    }
}
