using Playnite.SDK;
using SunshineLibrary.Models;
using SunshineLibrary.Services.Hosts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// Modal dialog for adding or editing a <see cref="HostConfig"/>. Uses
    /// <c>PlayniteApi.Dialogs.CreateWindow</c> so the chrome and theming match
    /// whatever Playnite theme the user has active. Test Connection runs the
    /// step-by-step probe inline; on first-connect or pin mismatch opens
    /// <see cref="CertPinConfirmationWindow"/> before committing.
    /// </summary>
    public class AddEditHostWindow
    {
        public HostConfig Result { get; private set; }

        private readonly IPlayniteAPI api;
        private readonly HostConfig working;
        private readonly bool isNew;
        private readonly StreamOverrides globalFallback;

        private Window dialog;

        private TextBox labelBox;
        private TextBox addressBox;
        private TextBox portBox;
        private TextBox userBox;
        private PasswordBox passwordBox;
        private CheckBox enabledBox;
        private TextBox excludedBox;
        private ComboBox autoRemoveCombo;
        private TextBlock statusText;
        private TextBlock statusDetail;
        private Button testBtn;
        private Button okBtn;
        private string lastObservedFingerprint;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="api">Playnite API for themed dialogs.</param>
        /// <param name="existing">Host to edit, or null to add a new host.</param>
        /// <param name="globalFallback">
        /// The effective overrides ABOVE the host layer — used by the Defaults tab's
        /// fallback hints so the user can see what each Inherit field would resolve to.
        /// Callers should pass <c>StreamOverrides.BuiltinDefault.MergedWith(settings.GlobalOverrides)</c>.
        /// Null is treated as builtin-only.
        /// </param>
        public AddEditHostWindow(IPlayniteAPI api, HostConfig existing, StreamOverrides globalFallback = null)
        {
            this.api = api;
            this.globalFallback = globalFallback ?? StreamOverrides.BuiltinDefault;
            isNew = existing == null;
            working = existing != null
                ? CloneHost(existing)
                : new HostConfig
                {
                    Id = Guid.NewGuid(),
                    Label = string.Empty,
                    Address = string.Empty,
                    Port = 47990,
                    ExcludedAppNames = new List<string>(PseudoAppFilter.DefaultsFor(ServerType.Sunshine)),
                    Defaults = new StreamOverrides(),
                };
            if (working.Defaults == null) working.Defaults = new StreamOverrides();
        }

        public bool ShowDialog(Window owner)
        {
            dialog = api.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
            });
            dialog.Owner = owner;
            dialog.Title = Localize(isNew ? "LOC_SunshineLibrary_HostDialog_TitleAdd" : "LOC_SunshineLibrary_HostDialog_TitleEdit");
            dialog.Width = 620;
            dialog.Height = 720;
            dialog.MinWidth = 520;
            dialog.MinHeight = 520;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ResizeMode = ResizeMode.CanResizeWithGrip;
            dialog.Content = BuildBody();
            return dialog.ShowDialog() == true;
        }

        private UIElement BuildBody()
        {
            // Docked layout: scrollable content on top, fixed buttons at bottom.
            var outer = new DockPanel { LastChildFill = true };
            // TextElement.Foreground is inheritable — setting it once on the root
            // makes every child TextBlock/CheckBox/Button content use the active
            // Playnite theme's text color instead of WPF's default black.
            outer.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

            // --- fixed footer (docked bottom) ---
            var buttonBar = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(16, 10, 16, 10),
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            okBtn = new Button
            {
                Content = Localize("LOC_SunshineLibrary_Common_Ok"),
                MinWidth = 100,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
            };
            okBtn.Click += OnOk;
            var cancelBtn = new Button
            {
                Content = Localize("LOC_SunshineLibrary_Common_Cancel"),
                MinWidth = 100,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true,
            };
            cancelBtn.Click += (_, __) => { dialog.DialogResult = false; dialog.Close(); };
            buttons.Children.Add(okBtn);
            buttons.Children.Add(cancelBtn);
            buttonBar.Child = buttons;
            DockPanel.SetDock(buttonBar, Dock.Bottom);
            outer.Children.Add(buttonBar);

            // --- tabbed body ---
            var tabs = new TabControl { Margin = new Thickness(8) };
            tabs.Items.Add(new TabItem
            {
                Header = Localize("LOC_SunshineLibrary_HostDialog_Tab_Host"),
                Content = BuildHostTab(),
            });
            tabs.Items.Add(new TabItem
            {
                Header = Localize("LOC_SunshineLibrary_HostDialog_Tab_Defaults"),
                Content = BuildDefaultsTab(),
            });
            outer.Children.Add(tabs);
            return outer;
        }

        private UIElement BuildHostTab()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16),
            };
            var root = new StackPanel();

            root.Children.Add(FieldRow(Localize("LOC_SunshineLibrary_HostField_Label"),
                labelBox = Bound(new TextBox(), working.Label, s => working.Label = s)));
            root.Children.Add(FieldRow(Localize("LOC_SunshineLibrary_HostField_Address"),
                addressBox = Bound(new TextBox(), working.Address, s => working.Address = s)));
            root.Children.Add(FieldRow(Localize("LOC_SunshineLibrary_HostField_Port"),
                portBox = Bound(new TextBox(), working.Port.ToString(),
                    s => { if (int.TryParse(s, out var p)) working.Port = p; })));
            root.Children.Add(FieldRow(Localize("LOC_SunshineLibrary_HostField_AdminUser"),
                userBox = Bound(new TextBox(), working.AdminUser, s => working.AdminUser = s)));

            passwordBox = new PasswordBox();
            if (!string.IsNullOrEmpty(working.AdminPassword)) passwordBox.Password = working.AdminPassword;
            root.Children.Add(FieldRow(Localize("LOC_SunshineLibrary_HostField_AdminPassword"), passwordBox));

            // Test Connection section — directly under credentials so it's where users look.
            var testPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 16) };
            testBtn = new Button
            {
                Content = Localize("LOC_SunshineLibrary_HostDialog_TestConnection"),
                MinWidth = 180,
                Padding = new Thickness(16, 6, 16, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                FontWeight = FontWeights.SemiBold,
            };
            testBtn.Click += OnTestConnection;
            testPanel.Children.Add(testBtn);
            statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            statusDetail = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
            };
            statusDetail.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            testPanel.Children.Add(statusText);
            testPanel.Children.Add(statusDetail);
            root.Children.Add(testPanel);

            // Enabled checkbox with explicit label (CheckBox.Content can render dim under some Playnite themes)
            var enabledRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 4),
            };
            enabledBox = new CheckBox
            {
                IsChecked = working.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            enabledBox.Checked += (_, __) => working.Enabled = true;
            enabledBox.Unchecked += (_, __) => working.Enabled = false;
            enabledRow.Children.Add(enabledBox);
            enabledRow.Children.Add(new TextBlock
            {
                Text = Localize("LOC_SunshineLibrary_HostField_Enabled"),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            });
            root.Children.Add(enabledRow);
            var enabledHelp = new TextBlock
            {
                Text = Localize("LOC_SunshineLibrary_HostField_Enabled_Help"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(24, 0, 0, 8),
            };
            enabledHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            root.Children.Add(enabledHelp);

            root.Children.Add(new TextBlock
            {
                Text = Localize("LOC_SunshineLibrary_HostField_ExcludedAppNames"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 2),
            });
            excludedBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 80,
                Text = working.ExcludedAppNames != null ? string.Join("\n", working.ExcludedAppNames) : string.Empty,
            };
            root.Children.Add(excludedBox);
            var excludedHelp = new TextBlock
            {
                Text = Localize("LOC_SunshineLibrary_HostField_ExcludedAppNames_Help"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 8),
            };
            excludedHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            root.Children.Add(excludedHelp);

            root.Children.Add(new TextBlock
            {
                Text = Localize("LOC_SunshineLibrary_HostField_AutoRemoveOrphans"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 2),
            });
            autoRemoveCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
            autoRemoveCombo.Items.Add(new ComboBoxItem { Content = Localize("LOC_SunshineLibrary_HostField_AutoRemoveOrphans_Inherit") });
            autoRemoveCombo.Items.Add(new ComboBoxItem { Content = Localize("LOC_SunshineLibrary_HostField_AutoRemoveOrphans_Delete") });
            autoRemoveCombo.Items.Add(new ComboBoxItem { Content = Localize("LOC_SunshineLibrary_HostField_AutoRemoveOrphans_Keep") });
            autoRemoveCombo.SelectedIndex = working.AutoRemoveOrphanedGames == null ? 0 : working.AutoRemoveOrphanedGames == true ? 1 : 2;
            root.Children.Add(autoRemoveCombo);
            var autoRemoveHelp = new TextBlock
            {
                Text = Localize("LOC_SunshineLibrary_HostField_AutoRemoveOrphans_Help"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 8),
            };
            autoRemoveHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            root.Children.Add(autoRemoveHelp);

            scroll.Content = root;
            return scroll;
        }

        private UIElement BuildDefaultsTab()
        {
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(16),
            };
            var root = new StackPanel();
            root.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

            var heading = new TextBlock
            {
                Text = string.Format(Localize("LOC_SunshineLibrary_HostDialog_DefaultsHeading"),
                    string.IsNullOrWhiteSpace(working.Label) ? Localize("LOC_SunshineLibrary_HostDialog_DefaultsHeadingUnnamed") : working.Label),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            };
            root.Children.Add(heading);
            var help = new TextBlock
            {
                Text = Localize("LOC_SunshineLibrary_HostDialog_DefaultsHelp"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 12),
            };
            help.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushDarker");
            root.Children.Add(help);

            // Fallback shown to the user is what Inherit would resolve to:
            // the global layer merged on top of the built-in defaults.
            var editor = new StreamOverridesEditor(working.Defaults, globalFallback);
            root.Children.Add(editor.Build());

            scroll.Content = root;
            return scroll;
        }

        private void OnTestConnection(object sender, RoutedEventArgs e)
        {
            if (!PullFromFormIntoWorking(out var validationMsg))
            {
                SetStatus(validationMsg, Brushes.Tomato);
                return;
            }

            testBtn.IsEnabled = false;
            statusDetail.Text = string.Empty;

            TestConnectionService.Outcome outcome = null;
            Exception caught = null;

            // Use Playnite's global progress overlay so the user gets a real spinner +
            // step-by-step status text + cancel button instead of a frozen dialog.
            var options = new GlobalProgressOptions(
                Localize("LOC_SunshineLibrary_HostDialog_Testing"), cancelable: true)
            {
                IsIndeterminate = false,
            };

            api.Dialogs.ActivateGlobalProgress(progressContext =>
            {
                progressContext.ProgressMaxValue = 6; // steps in TestConnectionService
                var probe = new TestConnectionService();
                var progress = new Progress<TestConnectionService.StepResult>(step =>
                {
                    progressContext.CurrentProgressValue++;
                    progressContext.Text = string.Format(
                        Localize("LOC_SunshineLibrary_HostDialog_TestStepStatus"),
                        step.Step, step.Message);
                    // Mirror to the inline status box on the UI thread for post-test reference.
                    dialog.Dispatcher.BeginInvoke(new Action(() => UpdateStepUi(step)));
                });
                try
                {
                    outcome = probe.RunAsync(working, progress, progressContext.CancelToken)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex) { caught = ex; }
            }, options);

            testBtn.IsEnabled = true;

            if (caught != null)
            {
                SetStatus(caught.Message, Brushes.Tomato);
                return;
            }
            if (outcome == null)
            {
                SetStatus(Localize("LOC_SunshineLibrary_HostDialog_TestCancelled"), null);
                return;
            }

            lastObservedFingerprint = outcome.ObservedSpkiSha256;

            if (outcome.Success)
            {
                working.ServerType = outcome.DetectedServerType;
                SetStatus(string.Format(Localize("LOC_SunshineLibrary_HostDialog_TestOk"),
                    outcome.DetectedServerType, outcome.AppCount), Brushes.MediumSeaGreen);
            }
            else
            {
                var last = outcome.Steps.LastOrDefault();
                SetStatus(string.Format(Localize("LOC_SunshineLibrary_HostDialog_TestFailedAt"),
                    last?.Step, last?.Message), Brushes.Tomato);
            }
        }

        private void UpdateStepUi(TestConnectionService.StepResult step)
        {
            var icon = step.Ok ? "✓" : "✗";
            var line = $"[{icon}] {step.Step}: {step.Message}";
            statusDetail.Text = string.IsNullOrEmpty(statusDetail.Text) ? line : statusDetail.Text + "\n" + line;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (!PullFromFormIntoWorking(out var validationMsg))
            {
                SetStatus(validationMsg, Brushes.Tomato);
                return;
            }

            // Decide whether a pin-confirmation dialog is needed.
            var observed = lastObservedFingerprint;
            var needsConfirm = false;
            if (string.IsNullOrEmpty(working.CertFingerprintSpkiSha256))
            {
                needsConfirm = true; // first-connect pin
            }
            else if (!string.IsNullOrEmpty(observed) &&
                     !string.Equals(observed, working.CertFingerprintSpkiSha256, StringComparison.OrdinalIgnoreCase))
            {
                needsConfirm = true; // re-pin on mismatch
            }

            if (needsConfirm)
            {
                if (string.IsNullOrEmpty(observed))
                {
                    SetStatus(Localize("LOC_SunshineLibrary_HostDialog_RunTestFirst"), Brushes.Tomato);
                    return;
                }
                var old = string.IsNullOrEmpty(working.CertFingerprintSpkiSha256) ? null : working.CertFingerprintSpkiSha256;
                var url = $"https://{working.Address}:{working.Port}/";
                var pin = new CertPinConfirmationWindow(api, working.Label, url, observed, old, null, null);
                if (!pin.ShowDialog(dialog))
                {
                    SetStatus(Localize("LOC_SunshineLibrary_HostDialog_PinDeclined"), Brushes.Tomato);
                    return;
                }
                working.CertFingerprintSpkiSha256 = observed;
            }

            Result = working;
            dialog.DialogResult = true;
            dialog.Close();
        }

        private bool PullFromFormIntoWorking(out string error)
        {
            error = null;
            working.Label = (labelBox.Text ?? string.Empty).Trim();
            working.Address = (addressBox.Text ?? string.Empty).Trim();
            if (!int.TryParse(portBox.Text, out var port) || port <= 0 || port > 65535)
            {
                error = Localize("LOC_SunshineLibrary_Validation_PortRange");
                return false;
            }
            working.Port = port;
            working.AdminUser = (userBox.Text ?? string.Empty).Trim();
            working.AdminPassword = passwordBox.Password ?? string.Empty;
            working.Enabled = enabledBox.IsChecked == true;
            working.AutoRemoveOrphanedGames = autoRemoveCombo.SelectedIndex == 0
                ? (bool?)null
                : autoRemoveCombo.SelectedIndex == 1;

            var excluded = (excludedBox.Text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            working.ExcludedAppNames = excluded;

            if (string.IsNullOrWhiteSpace(working.Label))
            {
                error = Localize("LOC_SunshineLibrary_Validation_LabelRequired");
                return false;
            }
            if (string.IsNullOrWhiteSpace(working.Address))
            {
                error = Localize("LOC_SunshineLibrary_Validation_AddressRequired");
                return false;
            }
            return true;
        }

        private void SetStatus(string text, Brush color)
        {
            statusText.Text = text;
            if (color != null) statusText.Foreground = color;
            else statusText.ClearValue(TextBlock.ForegroundProperty);
        }

        private static TextBox Bound(TextBox box, string initial, Action<string> setter)
        {
            box.Text = initial ?? string.Empty;
            box.TextChanged += (_, __) => setter(box.Text);
            return box;
        }

        private static FrameworkElement FieldRow(string label, FrameworkElement input)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(input);
            return panel;
        }

        private static HostConfig CloneHost(HostConfig source)
        {
            return new HostConfig
            {
                Id = source.Id,
                Label = source.Label,
                Address = source.Address,
                Port = source.Port,
                AdminUser = source.AdminUser,
                AdminPassword = source.AdminPassword,
                CertFingerprintSpkiSha256 = source.CertFingerprintSpkiSha256,
                ServerType = source.ServerType,
                ServerVersion = source.ServerVersion,
                MacAddress = source.MacAddress,
                Enabled = source.Enabled,
                ExcludedAppNames = source.ExcludedAppNames != null ? new List<string>(source.ExcludedAppNames) : new List<string>(),
                Defaults = CloneOverrides(source.Defaults),
                AutoRemoveOrphanedGames = source.AutoRemoveOrphanedGames,
            };
        }

        private static StreamOverrides CloneOverrides(StreamOverrides s)
        {
            if (s == null) return new StreamOverrides();
            return new StreamOverrides
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
                ShowStats = s.ShowStats,
                ExtraArgs = s.ExtraArgs,
            };
        }

        private static string Localize(string key)
        {
            var s = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }
    }
}
