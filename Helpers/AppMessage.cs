using System;
using System.Windows;
using ADLMRateGen.View.Suite;

namespace ADLMRateGen.Helpers
{
    /// <summary>
    /// MessageBox.Show's signatures, drawn as the suite's dialog (Richard's rule 4:
    /// the customer never sees the engine's own chrome). Existing calls move over
    /// by name alone; the wording each call site chose is kept.
    ///
    /// Falls back to the native box only when the app has no resources to draw
    /// with (very early start-up), so a message is never lost.
    /// </summary>
    public static class AppMessage
    {
        public static MessageBoxResult Show(string text)
            => Show(text, "ADLM RateGen", MessageBoxButton.OK, MessageBoxImage.None);

        public static MessageBoxResult Show(string text, string caption)
            => Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None);

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button)
            => Show(text, caption, button, MessageBoxImage.None);

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button,
                                            MessageBoxImage icon, MessageBoxResult defaultResult)
            => Show(text, caption, button, icon);

        public static MessageBoxResult Show(Window? owner, string text, string caption = "ADLM RateGen",
                                            MessageBoxButton button = MessageBoxButton.OK,
                                            MessageBoxImage icon = MessageBoxImage.None)
            => Show(text, caption, button, icon);

        public static MessageBoxResult Show(string text, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            var app = Application.Current;
            if (app == null || app.TryFindResource("SxSheet") == null)
                return MessageBox.Show(text, caption, button, icon);

            if (!app.Dispatcher.CheckAccess())
                return app.Dispatcher.Invoke(() => Show(text, caption, button, icon));

            var tone = icon switch
            {
                MessageBoxImage.Error => SuiteDialog.Tone.Danger,       // also Hand, Stop
                MessageBoxImage.Warning => SuiteDialog.Tone.Warning,    // also Exclamation
                _ => SuiteDialog.Tone.Info
            };
            var title = string.IsNullOrWhiteSpace(caption) ? "ADLM RateGen" : caption;

            switch (button)
            {
                case MessageBoxButton.OKCancel:
                    return SuiteDialog.Ask(title, text, "OK", "Cancel", tone) ? MessageBoxResult.OK : MessageBoxResult.Cancel;
                case MessageBoxButton.YesNo:
                    return SuiteDialog.Ask(title, text, "Yes", "No", tone) ? MessageBoxResult.Yes : MessageBoxResult.No;
                case MessageBoxButton.YesNoCancel:
                    return SuiteDialog.Ask(title, text, "Yes", "No", tone) ? MessageBoxResult.Yes : MessageBoxResult.No;
                default:
                    SuiteDialog.Tell(title, text, tone, "OK");
                    return MessageBoxResult.OK;
            }
        }
    }
}
