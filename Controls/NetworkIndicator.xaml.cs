#nullable disable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ADLMRateGen.Services;

namespace ADLMRateGen.Controls
{
    /// <summary>
    /// Header widget for <see cref="NetworkStatusService"/>. Self-contained: it
    /// starts the service on first load, listens for every probe, and repaints on
    /// the dispatcher, so a host page only has to place it and set Foreground.
    /// </summary>
    public partial class NetworkIndicator : UserControl
    {
        private static readonly SolidColorBrush Green = Frozen(0x22, 0xC5, 0x5E);
        private static readonly SolidColorBrush Amber = Frozen(0xF5, 0x9E, 0x0B);
        private static readonly SolidColorBrush Red = Frozen(0xEF, 0x44, 0x44);
        private static readonly SolidColorBrush Grey = Frozen(0x9C, 0xA3, 0xAF);

        public static readonly DependencyProperty ShowLabelProperty = DependencyProperty.Register(
            "ShowLabel", typeof(bool), typeof(NetworkIndicator),
            new PropertyMetadata(true, (d, e) => ((NetworkIndicator)d).Render()));

        /// <summary>Hide the word next to the bars where the header is short on room.</summary>
        public bool ShowLabel
        {
            get { return (bool)GetValue(ShowLabelProperty); }
            set { SetValue(ShowLabelProperty, value); }
        }

        public NetworkIndicator()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            NetworkStatusService.Instance.Changed += OnServiceChanged;
            NetworkStatusService.Instance.Start();
            Render();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            NetworkStatusService.Instance.Changed -= OnServiceChanged;
        }

        private void OnServiceChanged(object sender, EventArgs e)
        {
            if (Dispatcher.CheckAccess()) Render();
            else Dispatcher.BeginInvoke(new Action(Render));
        }

        private void OnClick(object sender, RoutedEventArgs e)
        {
            if (Label != null) Label.Text = "Checking";
            NetworkStatusService.Instance.CheckNow();
        }

        private void Render()
        {
            if (Bar1 == null) return;
            var svc = NetworkStatusService.Instance;
            int bars = svc.Bars;
            Brush lit;
            switch (svc.Level)
            {
                case NetworkSignalLevel.Excellent:
                case NetworkSignalLevel.Good: lit = Green; break;
                case NetworkSignalLevel.Fair: lit = Amber; break;
                case NetworkSignalLevel.Poor: lit = Red; break;
                default: lit = Grey; break;
            }
            if (svc.Status == NetworkStatus.CloudUnreachable) { lit = Amber; bars = 1; }

            Brush unlit = UnlitBrush();
            Bar1.Fill = bars >= 1 ? lit : unlit;
            Bar2.Fill = bars >= 2 ? lit : unlit;
            Bar3.Fill = bars >= 3 ? lit : unlit;
            Bar4.Fill = bars >= 4 ? lit : unlit;

            bool offline = svc.Status == NetworkStatus.NoInternet;
            OfflineSlash.Stroke = Red;
            OfflineSlash.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;

            Label.Text = svc.ShortLabel;
            Label.Visibility = ShowLabel ? Visibility.Visible : Visibility.Collapsed;
            Root.ToolTip = svc.Tooltip;
        }

        // The unlit bars are the label colour at low opacity, so they read as
        // "empty" on a white header and on a navy one alike.
        private Brush UnlitBrush()
        {
            var solid = Foreground as SolidColorBrush;
            var c = solid != null ? solid.Color : Grey.Color;
            var b = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
