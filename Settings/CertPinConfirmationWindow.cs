using Playnite.SDK;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SunshineLibrary.Settings
{
    /// <summary>
    /// First-connect pin confirmation dialog (PLAN §13a flow 1) and re-pin dialog
    /// on mismatch (flow 3). Uses <c>PlayniteApi.Dialogs.CreateWindow</c> so the
    /// chrome inherits the active Playnite theme.
    /// </summary>
    public class CertPinConfirmationWindow
    {
        public bool Trusted { get; private set; }

        private readonly IPlayniteAPI api;
        private readonly string hostLabel;
        private readonly string hostUrl;
        private readonly string newFingerprint;
        private readonly string oldFingerprint;
        private readonly string subject;
        private readonly string notAfter;

        private Window dialog;

        public CertPinConfirmationWindow(
            IPlayniteAPI api,
            string hostLabel,
            string hostUrl,
            string newFingerprint,
            string oldFingerprint,   // null for first connect
            string subject,
            string notAfter)
        {
            this.api = api;
            this.hostLabel = hostLabel;
            this.hostUrl = hostUrl;
            this.newFingerprint = newFingerprint;
            this.oldFingerprint = oldFingerprint;
            this.subject = subject;
            this.notAfter = notAfter;
        }

        public bool ShowDialog(Window owner)
        {
            dialog = api.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
            });
            dialog.Owner = owner;
            dialog.Title = L("LOC_SunshineLibrary_CertDialog_Title");
            dialog.Width = 600;
            dialog.SizeToContent = SizeToContent.Height;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ResizeMode = ResizeMode.NoResize;
            dialog.Content = BuildBody();
            dialog.ShowDialog();
            return Trusted;
        }

        private UIElement BuildBody()
        {
            var root = new StackPanel { Margin = new Thickness(16) };
            root.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");

            root.Children.Add(new TextBlock
            {
                Text = string.Format(L("LOC_SunshineLibrary_CertDialog_Heading"), hostLabel),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
            });

            var body = string.IsNullOrEmpty(oldFingerprint)
                ? L("LOC_SunshineLibrary_CertDialog_BodyFirstConnect")
                : L("LOC_SunshineLibrary_CertDialog_BodyRepin");
            root.Children.Add(new TextBlock
            {
                Text = body,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            if (!string.IsNullOrEmpty(oldFingerprint))
            {
                root.Children.Add(FingerprintRow(
                    L("LOC_SunshineLibrary_CertDialog_OldFingerprint"),
                    oldFingerprint,
                    isDanger: false));
            }
            root.Children.Add(FingerprintRow(
                string.IsNullOrEmpty(oldFingerprint)
                    ? L("LOC_SunshineLibrary_CertDialog_Fingerprint")
                    : L("LOC_SunshineLibrary_CertDialog_NewFingerprint"),
                newFingerprint,
                isDanger: !string.IsNullOrEmpty(oldFingerprint)));

            if (!string.IsNullOrEmpty(subject))
            {
                root.Children.Add(KeyValueRow(L("LOC_SunshineLibrary_CertDialog_Subject"), subject));
            }
            if (!string.IsNullOrEmpty(notAfter))
            {
                root.Children.Add(KeyValueRow(L("LOC_SunshineLibrary_CertDialog_NotAfter"), notAfter));
            }

            var guidance = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 16),
            };
            guidance.Inlines.Add(new Run(L("LOC_SunshineLibrary_CertDialog_Guidance") + " "));
            guidance.Inlines.Add(new Run(hostUrl) { FontWeight = FontWeights.SemiBold });
            root.Children.Add(guidance);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var trustBtn = new Button
            {
                Content = string.IsNullOrEmpty(oldFingerprint)
                    ? L("LOC_SunshineLibrary_CertDialog_Trust")
                    : L("LOC_SunshineLibrary_CertDialog_Repin"),
                MinWidth = 110,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = false, // user must click — don't trust by pressing Enter
            };
            trustBtn.Click += (_, __) => { Trusted = true; dialog.Close(); };

            var cancelBtn = new Button
            {
                Content = L("LOC_SunshineLibrary_CertDialog_Cancel"),
                MinWidth = 110,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true,
            };
            cancelBtn.Click += (_, __) => { Trusted = false; dialog.Close(); };

            buttons.Children.Add(trustBtn);
            buttons.Children.Add(cancelBtn);
            root.Children.Add(buttons);

            return root;
        }

        private static FrameworkElement FingerprintRow(string label, string fingerprint, bool isDanger)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBox
            {
                Text = fingerprint ?? string.Empty,
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                Background = isDanger ? new SolidColorBrush(Color.FromRgb(0x5b, 0x22, 0x22)) : null,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
            });
            return panel;
        }

        private static FrameworkElement KeyValueRow(string label, string value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = label + " ", FontWeight = FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
            return row;
        }

        private static string L(string key)
        {
            var s = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(s) ? key : s;
        }
    }
}
