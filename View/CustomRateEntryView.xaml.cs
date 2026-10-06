using System.Windows;
using System.Windows.Controls;
using ADLMRateGen.ViewModel.CustomRate;

namespace ADLMRateGen.View
{
    /// <summary>
    /// Build New Custom Rate. Shown in the window's popup host from a trade
    /// screen, or as a page; Tag="SxSheet" tells the host it has its own close.
    /// </summary>
    public partial class CustomRateEntryView : UserControl
    {
        public CustomRateEntryView()
        {
            InitializeComponent();
            Tag = "SxSheet";
            // Three summary tiles across when there is room, one when there is not.
            SizeChanged += (_, e) => Summary.Columns = e.NewSize.Width < 640 ? 1 : 3;
        }

        /// <summary>The x on a material or labour line.</summary>
        private void RemoveRow_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not CustomRateEntryViewModel vm) return;
            if ((sender as FrameworkElement)?.DataContext is not RateEntryItem item) return;
            if (!vm.MaterialItems.Remove(item)) vm.LabourItems.Remove(item);
        }

        /// <summary>Close or Cancel: nothing is saved. Hides the popup when shown in one.</summary>
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current?.MainWindow is MainWindow mw && mw.PopupHost.IsVisible)
                mw.PopupHost.Hide();
        }
    }
}
