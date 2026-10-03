using System.Windows.Controls;
using System.Windows.Input;
using ADLMRateGen.Services.Bill;
using ADLMRateGen.ViewModel.BillPricing;

namespace ADLMRateGen.View
{
    public partial class BillPricingView : UserControl
    {
        public BillPricingView() => InitializeComponent();

        private void UseSelectedRate()
        {
            if (DataContext is BillPricingViewModel vm && RateList.SelectedItem is BillRate rate
                && vm.UseRateCommand.CanExecute(rate))
                vm.UseRateCommand.Execute(rate);
        }

        private void RateList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => UseSelectedRate();

        private void RateList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            UseSelectedRate();
            e.Handled = true;
        }
    }
}
