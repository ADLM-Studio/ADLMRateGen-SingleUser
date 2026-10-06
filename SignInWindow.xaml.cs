using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ADLMRateGen
{
    public partial class SignInWindow : Window
    {
        public SignInWindow(object? signInViewModel)
        {
            InitializeComponent();
            DataContext = signInViewModel;
            SplashWindow.FitToScreen(this);
        }

        /// <summary>The card is the title bar: drag it from anywhere but a field or a button.</summary>
        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && IsInput(d)) return;
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private static bool IsInput(DependencyObject d)
        {
            for (var x = d; x != null; x = System.Windows.Media.VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
                if (x is TextBoxBase or PasswordBox or ButtonBase) return true;
            return false;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
