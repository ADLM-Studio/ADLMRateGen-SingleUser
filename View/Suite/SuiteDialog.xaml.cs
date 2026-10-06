using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ADLMRateGen.View.Suite
{
    /// <summary>
    /// The suite's dialog, in place of MessageBox. Modal over the main window,
    /// drawn in our skin, worded by us.
    /// </summary>
    public partial class SuiteDialog : Window
    {
        public enum Tone { Info, Success, Warning, Danger }

        private SuiteDialog() => InitializeComponent();

        /// <summary>Ask a yes/no question. Returns true for the primary answer.</summary>
        public static bool Ask(string title, string message, string yes, string no = "Cancel",
                               Tone tone = Tone.Info, IEnumerable<(string Label, string Value)>? rows = null)
            => Open(title, message, yes, no, tone, rows);

        /// <summary>Tell the customer something; one button.</summary>
        public static void Tell(string title, string message, Tone tone = Tone.Info, string ok = "Close")
            => Open(title, message, ok, null, tone, null);

        private static bool Open(string title, string message, string yes, string? no, Tone tone,
                                 IEnumerable<(string Label, string Value)>? rows)
        {
            var d = new SuiteDialog();
            d.Head.Text = title;
            d.Body.Text = message;
            d.Yes.Content = yes;
            if (no == null) d.No.Visibility = Visibility.Collapsed; else d.No.Content = no;

            if (rows != null)
            {
                d.Rows.ItemsSource = rows.Select(r => new { r.Label, r.Value }).ToList();
                d.Rows.Visibility = Visibility.Visible;
            }

            var (icon, colour) = tone switch
            {
                Tone.Success => ("Ic.Saved", "SxAction"),
                Tone.Warning => ("Ic.Help", "SxAccent"),
                Tone.Danger => ("Ic.Signout", "SxAccent"),
                _ => ("Ic.Help", "SxAction")
            };
            d.MarkIcon.Tag = d.FindResource(icon);
            d.MarkIcon.SetResourceReference(ForegroundProperty, colour);
            if (tone == Tone.Danger) d.Yes.Background = (Brush)d.FindResource("SxAccent");

            // Cover the owner exactly, so the veil sits over the app and nothing else.
            var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                        ?? Application.Current?.MainWindow;
            if (owner != null && owner != d && owner.IsVisible)
            {
                d.Owner = owner;
                if (owner.WindowState == WindowState.Maximized)
                {
                    d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    d.Width = owner.ActualWidth; d.Height = owner.ActualHeight;
                    d.WindowState = WindowState.Maximized;
                }
                else
                {
                    d.WindowStartupLocation = WindowStartupLocation.Manual;
                    d.Left = owner.Left; d.Top = owner.Top;
                    d.Width = owner.ActualWidth; d.Height = owner.ActualHeight;
                }
            }
            else
            {
                d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                d.Width = 560; d.Height = 360;
            }
            return d.ShowDialog() == true;
        }

        private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
