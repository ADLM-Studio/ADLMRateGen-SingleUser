using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ADLMRateGen.Services;
using ADLMRateGen.ViewModel;
using ADLMRateGen.ViewModel.Model;
using FontAwesome.Sharp;

namespace ADLMCivilPlugin.Controls
{
    public partial class Header : UserControl
    {
        public Header()
        {
            InitializeComponent();
        }

        private void Header_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateThemeIcon((Application.Current as ADLMRateGen.App)?.IsDarkTheme == true);
        }

        private void SuggestionList_Click(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is MainViewModel vm &&
                ((ListBox)sender).SelectedItem is SearchHit hit)
            {
                vm.GlobalSearch.Accept(hit);
            }
        }

        /// <summary>Ctrl+F from the main window: caret into the search box.</summary>
        public void FocusSearch()
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }

        /// <summary>Ctrl+Shift+L from the main window: same as the colour-mode button, icon included.</summary>
        public void ToggleTheme() => ColorModeButton_Click(ColorModeButton, new RoutedEventArgs());

        private void ColorModeButton_Click(object sender, RoutedEventArgs e)
        {
            var isDark = (Application.Current as ADLMRateGen.App)?.ToggleTheme() == true;
            UpdateThemeIcon(isDark);
        }

        private void ProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://adlmstudio.net/profile")
                {
                    UseShellExecute = true
                });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Unable to open profile.\n{ex.Message}");
            }
        }

        private void NotificationsPopup_Closed(object sender, System.EventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.IsNotificationsOpen = false;
            }
        }

        /// <summary>
        /// Bring back a price review the user hid. The panel lives in the library
        /// shell, so this also navigates there — reopening something onto a screen
        /// the user cannot see would look like nothing happened — and closes the
        /// notifications popup on the way out.
        /// </summary>
        private void ReopenPriceReview_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm) return;

            vm.LibraryShellViewModel?.ShowPriceConflictsCommand?.Execute(null);
            vm.SelectedLibraryShellViewCommand?.Execute(null);
            vm.IsNotificationsOpen = false;
        }

        private void UpdateThemeIcon(bool isDark)
        {
            ColorModeIcon.Icon = isDark ? IconChar.Sun : IconChar.Moon;
            ColorModeButton.ToolTip = isDark ? "Switch to light mode" : "Switch to dark mode";
        }

        /// <summary>
        /// Global reset for all user Quantity overrides — wipes every saved edit across every
        /// rate-build-up section (Concrete, Block, Ground, Finishes, Paint, Roof, Steel, Doors/Windows),
        /// persists the cleared state to disk, and pushes the wipe to the cloud so QUIV and HERON
        /// also see the reset.
        /// </summary>
        private async void ResetAllEditsButton_Click(object sender, RoutedEventArgs e)
        {
            var count = UserRateEditStore.Current.TotalOverrideCount;
            if (count == 0)
            {
                MessageBox.Show(
                    "You don't have any saved rate edits to reset.",
                    "Nothing to reset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Reset all {count} saved rate-build-up edits back to the shipped defaults?\n\n" +
                "This affects EVERY section (Concrete, Blockwork, Groundwork, Finishes, Painting, " +
                "Roofing, Steelwork, Windows & Doors) and will sync the reset to QUIV and HERON.\n\n" +
                "This action cannot be undone.",
                "Reset All Rate Edits",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            await RateEditCommands.ResetAllAsync();
            // The store fires OverridesChanged after ClearAll → each section VM is
            // subscribed and rebuilds its items automatically (see RecomputeAll in each VM ctor).
        }
    }
}
