using ADLMRateGen.Services;
using ADLMRateGen.ViewModel;
using ADLMRateGen.ViewModel.WindowAndDoor;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ADLMRateGen.View
{
    public partial class WindowAndDoorDetailControl : UserControl
    {
        public event Action? BackRequested;

        public WindowAndDoorDetailControl()
        {
            InitializeComponent();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke();
        }

        private void QuantityTextBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBox tb) return;
            if (!tb.IsEnabled) return;

            tb.IsReadOnly = false;
            tb.Focus();
            tb.SelectAll();
            e.Handled = true;
        }

        private void QuantityTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || sender is not TextBox tb) return;
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            QuantityTextBox_LostFocus(sender, e);
            tb.SelectAll();
            e.Handled = true;
        }

        private void QuantityTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;

            if (tb.DataContext is not WindowAndDoorBreakdownLine line) return;
            if (DataContext is not WindowAndDoorItem item) return;
            if (string.IsNullOrWhiteSpace(line.ComponentName)) return;
            if (line.IsTotalLine) return;

            UserRateEditStore.Current.SetOverride(
                SectionKeys.DoorsWindows,
                item.ItemNo,
                line.ComponentName,
                line.Quantity);

            if (TryGetParentVm(out var vm))
            {
                vm!.RecomputeItemInPlace(item.ItemNo);
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var ok = await RateEditCommands.SaveAsync();
            if (ok)
            {
                Suite.SuiteDialog.Tell("Rate saved",
                    "Your quantities are kept on this PC and used everywhere this rate appears. They sync to QUIV and HERON.",
                    Suite.SuiteDialog.Tone.Success);
            }
        }

        private async void ResetThisRateButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not WindowAndDoorItem item) return;

            if (!Suite.SuiteDialog.Ask("Reset this rate?",
                $"Every quantity in item {item.ItemNo} goes back to what ADLM published. The reset syncs to QUIV and HERON.",
                "Reset", "Cancel", Suite.SuiteDialog.Tone.Warning)) return;

            await RateEditCommands.ResetItemAsync(SectionKeys.DoorsWindows, item.ItemNo);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            RateEditCommands.CancelUnsavedEdits();
            BackRequested?.Invoke();
        }

        private static bool TryGetParentVm(out WindowAndDoorViewModel? vm)
        {
            vm = null;
            if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
            {
                vm = main.WindowAndDoorViewModel;
                return vm != null;
            }
            return false;
        }
    }
}
