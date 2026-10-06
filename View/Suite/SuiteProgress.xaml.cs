using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ADLMRateGen.View.Suite
{
    /// <summary>
    /// Named-step progress. Bind <see cref="Steps"/> to the stages of the work and
    /// <see cref="Step"/> to the index of the one under way (Steps.Count = done).
    /// </summary>
    public partial class SuiteProgress : UserControl
    {
        public static readonly DependencyProperty StepsProperty =
            DependencyProperty.Register(nameof(Steps), typeof(IList<string>), typeof(SuiteProgress),
                new PropertyMetadata(null, (d, _) => ((SuiteProgress)d).Paint(true)));

        public static readonly DependencyProperty StepProperty =
            DependencyProperty.Register(nameof(Step), typeof(int), typeof(SuiteProgress),
                new PropertyMetadata(0, (d, _) => ((SuiteProgress)d).Paint(true)));

        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(nameof(Message), typeof(string), typeof(SuiteProgress),
                new PropertyMetadata("", (d, _) => ((SuiteProgress)d).Paint(false)));

        public IList<string>? Steps { get => (IList<string>?)GetValue(StepsProperty); set => SetValue(StepsProperty, value); }
        public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }

        /// <summary>Free text: the label when there are no steps, the detail line when there are.</summary>
        public string Message { get => (string)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

        private readonly DispatcherTimer _creep = new() { Interval = TimeSpan.FromMilliseconds(120) };
        private DateTime _stepStarted = DateTime.Now;
        private double _pulse;

        public SuiteProgress()
        {
            InitializeComponent();
            _creep.Tick += (_, __) => Tick();
            Loaded += (_, __) => { Paint(true); _creep.Start(); };
            Unloaded += (_, __) => _creep.Stop();
            IsVisibleChanged += (_, __) => { if (IsVisible) { _stepStarted = DateTime.Now; _creep.Start(); } else _creep.Stop(); };
            SizeChanged += (_, __) => Tick();
        }

        private int Count => Steps?.Count ?? 0;

        private void Paint(bool stepChanged)
        {
            if (Label == null) return;
            if (stepChanged) _stepStarted = DateTime.Now;

            int n = Count, at = Math.Max(0, Math.Min(Step, n));
            if (n == 0)
            {
                // The host usually titles the work already; say nothing twice.
                Label.Text = Message ?? "";
                Label.Visibility = string.IsNullOrWhiteSpace(Message) ? Visibility.Collapsed : Visibility.Visible;
                Counter.Text = "";
                Detail.Visibility = Visibility.Collapsed;
                StepList.ItemsSource = null;
            }
            else
            {
                Label.Visibility = Visibility.Visible;
                Label.Text = at >= n ? "Done" : Steps![at];
                Counter.Text = at >= n ? "" : $"Step {at + 1} of {n}";
                Detail.Text = Message ?? "";
                Detail.Visibility = string.IsNullOrWhiteSpace(Message) || Message == Label.Text ? Visibility.Collapsed : Visibility.Visible;
                StepList.ItemsSource = Steps!.Select((s, i) => new
                {
                    Name = s,
                    Mark = i < at ? "✓" : i == at ? "›" : "",
                    Opacity = i <= at ? 1.0 : 0.5
                }).ToList();
            }
            Tick();
        }

        private void Tick()
        {
            double w = Track.ActualWidth;
            if (w <= 0) return;
            int n = Count;
            if (n == 0)
            {
                // Spinner mode: a short block that sweeps, so it reads as alive
                // without pretending to measure anything.
                _pulse = (_pulse + 0.035) % 1.4;
                Fill.Width = w * 0.28;
                Fill.Margin = new Thickness((w * 1.28) * _pulse - w * 0.28, 0, 0, 0);
                return;
            }
            Fill.Margin = new Thickness(0);
            int at = Math.Max(0, Math.Min(Step, n));
            double done = (double)at / n;
            // Creep inside the current step: fast at first, never past 85% of it.
            double secs = (DateTime.Now - _stepStarted).TotalSeconds;
            double creep = at >= n ? 0 : (1 - Math.Exp(-secs / 6)) * 0.85 / n;
            Fill.Width = w * Math.Min(1, done + creep);
        }
    }
}
