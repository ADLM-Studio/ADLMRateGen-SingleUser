using System;
using System.Windows;

namespace ADLMRateGen
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            FitToScreen(this);
        }

        /// <summary>Name the start-up step under way on the card's status line.</summary>
        public void Say(string text) => Card.Say(text);

        /// <summary>
        /// The frame at full size where it fits, otherwise the largest that does:
        /// at most 94% of the screen's width and 90% of its height, aspect kept.
        /// </summary>
        internal static void FitToScreen(Window w, double pad = 80)
        {
            var area = SystemParameters.WorkArea;
            double scale = Math.Min(1.0, Math.Min((area.Width * 0.94) / 815.0, (area.Height * 0.9) / 545.0));
            w.Width = 815 * scale + pad;
            w.Height = 545 * scale + pad;
        }
    }
}
