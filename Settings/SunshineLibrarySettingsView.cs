using Microsoft.Win32;
using Playnite.SDK;
using SunshineLibrary.Models;
using SunshineLibrary.Services.Clients;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// Settings UserControl. Code-behind WPF, consistent with ApolloSync's style
    /// (see ApolloSyncSettingsView.xaml.cs — UI is built programmatically so
    /// every string is a ResourceProvider lookup without XAML churn).
    /// </summary>
    public class SunshineLibrarySettingsView : UserControl
    {
        public SunshineLibrarySettingsView()
        {
            Loaded += (_, __) =>
            {
                if (DataContext is SunshineLibrarySettingsViewModel vm)
                {
                    Content = Build(vm);
                }
            };
        }

        private UIElement Build(SunshineLibrarySettingsViewModel vm)
        {
            var tabs = new TabControl { Margin = new Thickness(8) };
            tabs.Items.Add(new TabItem { Header = L("LOC_SunshineLibrary_Settings_Tab_Hosts"), Content = BuildHostsTab(vm) });
            tabs.Items.Add(new TabItem { Header = L("LOC_SunshineLibrary_Settings_Tab_Client"), Content = BuildClientTab(vm) });
            tabs.Items.Add(new TabItem { Header = L("LOC_SunshineLibrary_Settings_Tab_Defaults"), Content = BuildDefaultsTab(vm) });
            tabs.Items.Add(new TabItem { Header = L("LOC_SunshineLibrary_Settings_Tab_General"), Content = BuildGeneralTab(vm) });
            return tabs;
        }

        // --- Streaming Defaults tab ------------------------------------------------

        private UIElement BuildDefaultsTab(SunshineLibrarySettingsViewModel vm)
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(8),
            };
            var root = new StackPanel();
            root.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

            root.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Defaults_Heading"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            var helpText = new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Defaults_Help"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
            };
            helpText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            root.Children.Add(helpText);

            if (vm.Settings.GlobalOverrides == null)
            {
                vm.Settings.GlobalOverrides = new StreamOverrides();
            }

            // Built-in is the only layer above global, so the fallback preview shows builtin defaults.
            var editor = new StreamOverridesEditor(vm.Settings.GlobalOverrides, StreamOverrides.BuiltinDefault);
            root.Children.Add(editor.Build());

            var resetBtn = new Button
            {
                Content = L("LOC_SunshineLibrary_Defaults_ResetToBuiltin"),
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 16, 0, 8),
            };
            resetBtn.Click += (_, __) =>
            {
                var confirm = vm.Api.Dialogs.ShowMessage(
                    L("LOC_SunshineLibrary_Defaults_ResetConfirm"),
                    L("LOC_SunshineLibrary_Name"),
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (confirm != System.Windows.MessageBoxResult.Yes) return;

                // Replace GlobalOverrides and rebuild the tab so all controls reset their bindings.
                vm.Settings.GlobalOverrides = new StreamOverrides();
                // Force a tab-content refresh by re-invoking Build(); walks back up and rebuilds all tabs.
                if (DataContext is SunshineLibrarySettingsViewModel vmRef)
                {
                    Content = Build(vmRef);
                }
            };
            root.Children.Add(resetBtn);

            scroll.Content = root;
            return scroll;
        }

        // --- Hosts tab --------------------------------------------------------

        private UIElement BuildHostsTab(SunshineLibrarySettingsViewModel vm)
        {
            var panel = new DockPanel { Margin = new Thickness(8), LastChildFill = true };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var addBtn = MakeButton(L("LOC_SunshineLibrary_Hosts_Add"));
            var editBtn = MakeButton(L("LOC_SunshineLibrary_Hosts_Edit"));
            var removeBtn = MakeButton(L("LOC_SunshineLibrary_Hosts_Remove"));
            buttons.Children.Add(addBtn);
            buttons.Children.Add(editBtn);
            buttons.Children.Add(removeBtn);
            DockPanel.SetDock(buttons, Dock.Top);
            panel.Children.Add(buttons);

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                ItemsSource = vm.Hosts,
            };
            grid.Columns.Add(Col(L("LOC_SunshineLibrary_HostCol_Label"), nameof(HostConfig.Label), 200));
            grid.Columns.Add(Col(L("LOC_SunshineLibrary_HostCol_Address"), nameof(HostConfig.Address), 180));
            grid.Columns.Add(Col(L("LOC_SunshineLibrary_HostCol_Port"), nameof(HostConfig.Port), 60));
            grid.Columns.Add(Col(L("LOC_SunshineLibrary_HostCol_Flavor"), nameof(HostConfig.ServerType), 90));
            grid.Columns.Add(Col(L("LOC_SunshineLibrary_HostCol_Enabled"), nameof(HostConfig.Enabled), 70));
            panel.Children.Add(grid);

            void DoEdit(HostConfig selected)
            {
                var fallback = StreamOverrides.BuiltinDefault.MergedWith(vm.Settings.GlobalOverrides);
                var dlg = new AddEditHostWindow(vm.Api, selected, fallback);
                if (dlg.ShowDialog(Window.GetWindow(this)) && dlg.Result != null)
                {
                    var idx = vm.Settings.Hosts.FindIndex(h => h.Id == selected.Id);
                    if (idx >= 0) vm.Settings.Hosts[idx] = dlg.Result;
                    var oidx = vm.Hosts.IndexOf(selected);
                    if (oidx >= 0) vm.Hosts[oidx] = dlg.Result;
                }
            }

            addBtn.Click += (_, __) =>
            {
                var fallback = StreamOverrides.BuiltinDefault.MergedWith(vm.Settings.GlobalOverrides);
                var dlg = new AddEditHostWindow(vm.Api, null, fallback);
                if (dlg.ShowDialog(Window.GetWindow(this)) && dlg.Result != null)
                {
                    if (vm.Settings.Hosts == null) vm.Settings.Hosts = new System.Collections.Generic.List<HostConfig>();
                    vm.Settings.Hosts.Add(dlg.Result);
                    vm.Hosts.Add(dlg.Result);
                }
            };
            editBtn.Click += (_, __) =>
            {
                if (grid.SelectedItem is HostConfig selected) DoEdit(selected);
            };
            grid.MouseDoubleClick += (_, __) =>
            {
                if (grid.SelectedItem is HostConfig selected) DoEdit(selected);
            };
            removeBtn.Click += (_, __) =>
            {
                if (!(grid.SelectedItem is HostConfig selected)) return;
                var confirm = MessageBox.Show(
                    string.Format(L("LOC_SunshineLibrary_Hosts_RemoveConfirm"), selected.Label),
                    L("LOC_SunshineLibrary_Name"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm == MessageBoxResult.Yes)
                {
                    vm.Settings.Hosts.RemoveAll(h => h.Id == selected.Id);
                    vm.Hosts.Remove(selected);
                }
            };

            return panel;
        }

        // --- Client tab -------------------------------------------------------

        private UIElement BuildClientTab(SunshineLibrarySettingsViewModel vm)
        {
            var panel = new StackPanel { Margin = new Thickness(8) };

            panel.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Client_Heading"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });

            var pathRow = new StackPanel { Orientation = Orientation.Horizontal };
            var pathBox = new TextBox
            {
                Text = vm.Settings.Client?.GetPath(MoonlightClient.ClientId) ?? string.Empty,
                Width = 420,
                Margin = new Thickness(0, 0, 8, 0),
            };
            pathBox.TextChanged += (_, __) =>
            {
                if (vm.Settings.Client == null) vm.Settings.Client = new ClientSettings();
                vm.Settings.Client.SetPath(MoonlightClient.ClientId, pathBox.Text);
            };
            var browseBtn = MakeButton(L("LOC_SunshineLibrary_Client_Browse"));
            browseBtn.Click += (_, __) =>
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "moonlight-qt.exe|moonlight-qt.exe|Executables|*.exe|All files|*.*",
                    Title = L("LOC_SunshineLibrary_Client_Browse_Title"),
                };
                if (dlg.ShowDialog() == true) pathBox.Text = dlg.FileName;
            };
            var autoDetectBtn = MakeButton(L("LOC_SunshineLibrary_Client_AutoDetect"));
            autoDetectBtn.Click += (_, __) =>
            {
                // The Auto-detect button locates the currently-active client, which for
                // 0.1 is always Moonlight. When other clients land in M5 this should read
                // vm.Settings.Client.ActiveClientId and dispatch through the registry.
                var activeClient = new StreamClientRegistry()
                    .Resolve(vm.Settings.Client ?? new ClientSettings());
                var found = activeClient is MoonlightCompatibleClient compatible
                    ? ClientLocator.Locate(new ClientLocatorConfig
                    {
                        ExeNames = new[] { "moonlight-qt.exe", "Moonlight.exe", "moonlight.exe" },
                        InstallDirNames = new[] { "Moonlight Game Streaming" },
                        ScoopAppNames = new[] { "moonlight", "moonlight-qt" },
                        WingetPackagePatterns = new[] { "Moonlight*", "*Moonlight*" },
                    })
                    : new System.Collections.Generic.List<string>();
                if (found.Count == 0)
                {
                    vm.Api.Dialogs.ShowMessage(
                        L("LOC_SunshineLibrary_Client_AutoDetect_None"),
                        L("LOC_SunshineLibrary_Name"));
                    return;
                }
                if (found.Count == 1)
                {
                    pathBox.Text = found[0];
                    vm.Api.Dialogs.ShowMessage(
                        string.Format(L("LOC_SunshineLibrary_Client_AutoDetect_FoundOne"), found[0]),
                        L("LOC_SunshineLibrary_Name"));
                    return;
                }
                // Multiple — present the list and let the user pick.
                var pickItems = found.Select(p => new GenericItemOption(p, p)).Cast<GenericItemOption>().ToList();
                var picked = vm.Api.Dialogs.ChooseItemWithSearch(
                    pickItems,
                    query => pickItems,
                    null,
                    L("LOC_SunshineLibrary_Client_AutoDetect_Pick"));
                if (picked != null) pathBox.Text = picked.Name;
            };
            pathRow.Children.Add(pathBox);
            pathRow.Children.Add(browseBtn);
            pathRow.Children.Add(autoDetectBtn);
            panel.Children.Add(pathRow);

            panel.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Client_PathHelp"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 12),
            });

            var testBtn = MakeButton(L("LOC_SunshineLibrary_Client_Test"));
            var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            testBtn.Click += (_, __) =>
            {
                var client = new MoonlightClient();
                var availability = client.ProbeAvailability(vm.Settings.Client ?? new ClientSettings());
                if (availability.Installed)
                {
                    testResult.Text = string.Format(L("LOC_SunshineLibrary_Info_ClientInstalled"), availability.ExecutablePath);
                    testResult.Foreground = Brushes.MediumSeaGreen;
                }
                else
                {
                    testResult.Text = availability.UnavailableReason ?? L("LOC_SunshineLibrary_Error_ClientNotInstalled");
                    testResult.Foreground = Brushes.Tomato;
                }
            };
            panel.Children.Add(testBtn);
            panel.Children.Add(testResult);

            return panel;
        }

        // --- General tab ------------------------------------------------------

        private UIElement BuildGeneralTab(SunshineLibrarySettingsViewModel vm)
        {
            var panel = new StackPanel { Margin = new Thickness(8) };

            panel.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Settings_NotificationMode"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });

            var notifCombo = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
            notifCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Notif_Always"), Tag = NotificationMode.Always });
            notifCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Notif_OnUpdateOnly"), Tag = NotificationMode.OnUpdateOnly });
            notifCombo.Items.Add(new ComboBoxItem { Content = L("LOC_SunshineLibrary_Notif_Never"), Tag = NotificationMode.Never });
            notifCombo.SelectedIndex = notifCombo.Items
                .Cast<ComboBoxItem>()
                .Select((it, idx) => new { it, idx })
                .FirstOrDefault(x => (NotificationMode)x.it.Tag == vm.Settings.NotificationMode)?.idx ?? 0;
            notifCombo.SelectionChanged += (_, __) =>
            {
                if (notifCombo.SelectedItem is ComboBoxItem item && item.Tag is NotificationMode m)
                    vm.Settings.NotificationMode = m;
            };
            panel.Children.Add(notifCombo);

            panel.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Notif_Help"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 16),
            });

            // Orphan-removal opt-in
            var autoRemoveRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 4),
            };
            var autoRemoveBox = new CheckBox
            {
                IsChecked = vm.Settings.AutoRemoveOrphanedGames,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            autoRemoveBox.Checked += (_, __) => vm.Settings.AutoRemoveOrphanedGames = true;
            autoRemoveBox.Unchecked += (_, __) => vm.Settings.AutoRemoveOrphanedGames = false;
            autoRemoveRow.Children.Add(autoRemoveBox);
            autoRemoveRow.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Settings_AutoRemoveOrphans"),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            });
            panel.Children.Add(autoRemoveRow);
            panel.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Settings_AutoRemoveOrphans_Help"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                FontSize = 11,
                Margin = new Thickness(24, 2, 0, 12),
            });

            // Danger zone
            panel.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 12) });

            panel.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Settings_DangerZone"),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Tomato,
                Margin = new Thickness(0, 0, 0, 4),
            });

            var revokeBtn = new Button
            {
                Content = L("LOC_SunshineLibrary_Settings_RevokeAll"),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 220,
            };
            revokeBtn.Click += (_, __) =>
            {
                var confirm = MessageBox.Show(
                    L("LOC_SunshineLibrary_Settings_RevokeAllConfirm"),
                    L("LOC_SunshineLibrary_Name"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                vm.RevokeAllCredentials();
                MessageBox.Show(
                    L("LOC_SunshineLibrary_Settings_RevokeAllDone"),
                    L("LOC_SunshineLibrary_Name"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            };
            panel.Children.Add(revokeBtn);
            panel.Children.Add(new TextBlock
            {
                Text = L("LOC_SunshineLibrary_Settings_RevokeAllHelp"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
            });

            return panel;
        }

        // --- helpers ----------------------------------------------------------

        private static DataGridColumn Col(string header, string path, double width) =>
            new DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(path),
                Width = new DataGridLength(width),
            };

        private static Button MakeButton(string text) => new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 4, 12, 4),
        };

        private static string L(string key)
        {
            var s = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }
    }
}
