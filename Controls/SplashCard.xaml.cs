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
            CopyrightRun.Text = $"2020–{DateTime.Now.Year} ADLM Studio. All rights reserved.";
        }

        /// <summary>The installed version, as the installer stamps it (major.minor.patch).</summary>
        public static string Version
        {
            get
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version
                        ?? typeof(SplashCard).Assembly.GetName().Version;
                return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
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
