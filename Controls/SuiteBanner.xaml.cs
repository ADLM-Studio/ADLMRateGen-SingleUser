using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace ADLMRateGen.Controls
{
    /// <summary>
    /// The suite's welcome banner. Three messages, because one wears out, and
    /// each one is a real thing a RateGen customer could do next. Each link
    /// opens a page that exists on adlmstudio.net.
    /// </summary>
    public partial class SuiteBanner : UserControl
    {
        private sealed record Slide(string[] Parts, string Cta, string Url);

        // Parts alternate plain, bold, plain... - the web banner's <b> runs.
        private static readonly Slide[] Slides =
        {
            new(new[] { "Extracting your quantities just got a lot easier. Try our ", "Revit", " and ", "PlanSwift", " plugins today." },
                "Try now", "https://adlmstudio.net/products"),
            new(new[] { "Two products or more take ", "5% off", ", and a year up front takes ", "10%", ". They stack." },
                "Work out a price", "https://adlmstudio.net/quote"),
            new(new[] { "Built a rate you are proud of? ", "Beyond BIM", " teaches the method behind the library." },
                "Look at the courses", "https://adlmstudio.net/learn"),
        };

        public static readonly DependencyProperty FirstNameProperty =
            DependencyProperty.Register(nameof(FirstName), typeof(string), typeof(SuiteBanner),
                new PropertyMetadata("there", (d, _) => ((SuiteBanner)d).Paint()));

        public static readonly DependencyProperty HideCommandProperty =
            DependencyProperty.Register(nameof(HideCommand), typeof(ICommand), typeof(SuiteBanner));

        public string FirstName
        {
            get => (string)GetValue(FirstNameProperty);
            set => SetValue(FirstNameProperty, value);
        }

        /// <summary>Run when the × is pressed; the host decides what hiding means.</summary>
        public ICommand? HideCommand
        {
            get => (ICommand?)GetValue(HideCommandProperty);
            set => SetValue(HideCommandProperty, value);
        }

        private int _at;
        private readonly DispatcherTimer _rotate = new() { Interval = TimeSpan.FromSeconds(9) };
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

        public SuiteBanner()
        {
            InitializeComponent();
            _rotate.Tick += (_, __) => Go(_at + 1);
            _clock.Tick += (_, __) => When.Text = Stamp(DateTime.Now);
            SizeChanged += (_, e) =>
            {
                Art.Width = Math.Max(0, Card.ActualWidth * 0.66);
                // Below 900px: no artwork, no badge, tighter padding, smaller greeting.
                bool compact = e.NewSize.Width < 900;
                Art.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                Badge.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                Words.Margin = compact ? new Thickness(20, 22, 44, 34) : new Thickness(84, 26, 30, 26);
                Hello.FontSize = compact ? 22 : 27;
            };
            Loaded += (_, __) => { Paint(); _rotate.Start(); _clock.Start(); };
            Unloaded += (_, __) => { _rotate.Stop(); _clock.Stop(); };
            // Hovering holds the message still so it can be read.
            MouseEnter += (_, __) => _rotate.Stop();
            MouseLeave += (_, __) => { if (IsLoaded) _rotate.Start(); };
        }

        private void Go(int i)
        {
            _at = ((i % Slides.Length) + Slides.Length) % Slides.Length;
            Paint();
        }

        private void Paint()
        {
            if (Msg == null) return;
            var s = Slides[_at];
            When.Text = Stamp(DateTime.Now);
            Who.Text = string.IsNullOrWhiteSpace(FirstName) ? "there" : FirstName;
            Msg.Inlines.Clear();
            for (int k = 0; k < s.Parts.Length; k++)
            {
                var run = new Run(s.Parts[k]);
                if (k % 2 == 1) { run.FontWeight = FontWeights.SemiBold; run.Foreground = System.Windows.Media.Brushes.White; }
                Msg.Inlines.Add(run);
            }
            Cta.Content = s.Cta;
            Dots.ItemsSource = Slides.Select((_, k) => new { Index = k, On = k == _at, Label = "Message " + (k + 1) }).ToList();
        }

        /// <summary>"Wednesday, 30th September 2026 | 7:35pm WAT". The zone is named
        /// only when the PC is on West Africa Time, so it is never wrong.</summary>
        internal static string Stamp(DateTime now)
        {
            int d = now.Day;
            string nth = (d % 100 is > 3 and < 21) ? "th" : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            int h = now.Hour % 12 == 0 ? 12 : now.Hour % 12;
            string zone = TimeZoneInfo.Local.GetUtcOffset(now) == TimeSpan.FromHours(1) ? " WAT" : "";
            var en = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
            return now.ToString("dddd", en) + ", " + d + nth + " " + now.ToString("MMMM yyyy", en) +
                   " | " + h + ":" + now.ToString("mm", en) + (now.Hour < 12 ? "am" : "pm") + zone;
        }

        private void Cta_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(Slides[_at].Url) { UseShellExecute = true }); }
            catch { /* no browser registered: nothing useful to add */ }
        }

        private void Dot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: int k }) { Go(k); _rotate.Stop(); _rotate.Start(); }
        }

        private void Hide_Click(object sender, RoutedEventArgs e)
        {
            if (HideCommand?.CanExecute(null) == true) HideCommand.Execute(null);
        }
    }
}
