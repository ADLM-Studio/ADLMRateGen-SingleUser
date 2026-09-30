using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ADLMRateGen.ViewModel;
using FontAwesome.Sharp;

namespace ADLMRateGen.View
{
    public partial class SignInUserControl : UserControl
    {
        private const string ForgotPasswordUrl = "https://adlmstudio.net/login";
        private const string SignUpUrl = "https://adlmstudio.net/signup";

        private bool _pwdVisible;
        private bool _syncingPassword;

        public SignInUserControl()
        {
            InitializeComponent();
            UpdatePasswordVisibility();
        }

        private void TogglePwd_Click(object sender, RoutedEventArgs e)
        {
            _pwdVisible = !_pwdVisible;
            UpdatePasswordVisibility();
        }

        /// <summary>
        /// Enter from either credential field signs in, so the user never has to
        /// reach for the mouse. Handled on the fields rather than as the window's
        /// default button so Enter on the Forgot-password / Create-account links
        /// still does what those links do.
        /// </summary>
        private void LoginField_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return)
            {
                return;
            }

            e.Handled = true;

            if (DataContext is not SignInViewModel vm || vm.IsLoading)
            {
                return;
            }

            // The PasswordBox has no bindable Password, and a paste into the
            // preview box can land after the last sync — push the current value
            // through before the command reads it.
            vm.Password = _pwdVisible
                ? PasswordPreviewTextBox.Text
                : PasswordBox.Password;

            if (vm.LoginCommand != null && vm.LoginCommand.CanExecute(null))
            {
                vm.LoginCommand.Execute(null);
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPassword || sender is not PasswordBox pb)
            {
                return;
            }

            _syncingPassword = true;
            PasswordPreviewTextBox.Text = pb.Password;
            _syncingPassword = false;

            if (DataContext is SignInViewModel vm)
            {
                vm.Password = pb.Password;
            }
        }

        private void PasswordPreviewTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingPassword || sender is not TextBox tb)
            {
                return;
            }

            _syncingPassword = true;
            PasswordBox.Password = tb.Text;
            _syncingPassword = false;

            if (DataContext is SignInViewModel vm)
            {
                vm.Password = tb.Text;
            }
        }

        private void OpenForgotPassword_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl(ForgotPasswordUrl);
        }

        private void OpenSignUp_Click(object sender, RoutedEventArgs e)
        {
            OpenExternalUrl(SignUpUrl);
        }

        private void UpdatePasswordVisibility()
        {
            var password = PasswordBox?.Password ?? PasswordPreviewTextBox?.Text ?? string.Empty;

            _syncingPassword = true;

            if (PasswordPreviewTextBox != null)
            {
                PasswordPreviewTextBox.Text = password;
                PasswordPreviewTextBox.Visibility = _pwdVisible ? Visibility.Visible : Visibility.Collapsed;
                PasswordPreviewTextBox.CaretIndex = PasswordPreviewTextBox.Text.Length;
            }

            if (PasswordBox != null)
            {
                PasswordBox.Password = password;
                PasswordBox.Visibility = _pwdVisible ? Visibility.Collapsed : Visibility.Visible;
            }

            if (PasswordToggleIcon != null)
            {
                PasswordToggleIcon.Icon = _pwdVisible ? IconChar.EyeSlash : IconChar.Eye;
            }

            _syncingPassword = false;
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ADLMRateGen.Helpers.AppMessage.Show($"Unable to open link.\n{ex.Message}", "ADLM Rate Gen");
            }
        }
    }
}
