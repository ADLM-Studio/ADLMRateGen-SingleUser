using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace ADLMRateGen.Controls
{
    /// <summary>
    /// RateGen's splash card. <see cref="Say"/> names the start-up step under way;
    /// <see cref="Form"/> turns the same card into the sign-in.
    /// </summary>
    public partial class SplashCard : UserControl
    {
        public static readonly DependencyProperty FormProperty =
            DependencyProperty.Register(nameof(Form), typeof(object), typeof(SplashCard),
                new PropertyMetadata(null, (d, e) => ((SplashCard)d).ShowForm(e.NewValue)));

        /// <summary>The sign-in form. Null shows the splash's promise and status.</summary>
        public object? Form
        {
            get => GetValue(FormProperty);
            set => SetValue(FormProperty, value);
        }

        public SplashCard()
        {
            InitializeComponent();
            VersionText.Text = "v" + Version;
            VersionText.ToolTip = BuildLabel;
            CopyrightRun.Text = $"2020–{DateTime.Now.Year} ADLM Studio. All rights reserved.";
        }

        private static System.Version? Assembly4 =>
            Assembly.GetEntryAssembly()?.GetName().Version
            ?? typeof(SplashCard).Assembly.GetName().Version;

        /// <summary>
        /// The installed version for the badge. A 2026 build (Major.Minor.YYMM.N,
        /// third part 1000 or more) keeps its launch version, so it reads "3.0";
        /// an older install reads major.minor.patch as before.
        /// </summary>
        public static string Version
        {
            get
            {
                var v = Assembly4;
                if (v == null) return "1.0.0";
                return v.Build >= 1000 ? $"{v.Major}.{v.Minor}" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
        }

        /// <summary>The full build, e.g. "Rate Gen 3.0, build 3.0.2610.1".</summary>
        public static string BuildLabel
        {
            get
            {
                var v = Assembly4;
                if (v == null) return "Rate Gen";
                return v.Build >= 1000
                    ? $"Rate Gen {v.Major}.{v.Minor}, build {v.Major}.{v.Minor}.{v.Build}.{Math.Max(0, v.Revision)}"
                    : $"Rate Gen {v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
        }

        /// <summary>Change the status line: name the step, not a percentage.</summary>
        public void Say(string text)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Say(text)); return; }
            StatusText.Text = text;
        }

        private void ShowForm(object? form)
        {
            FormHost.Content = form;
            bool signIn = form != null;
            FormHost.Visibility = signIn ? Visibility.Visible : Visibility.Collapsed;
            Splash.Visibility = signIn ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
