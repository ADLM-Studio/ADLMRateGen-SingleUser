using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using ADLMRateGen.ViewModel;
using ADLMRateGen.ViewModel.Model;

namespace ADLMRateGen.View
{
    /// <summary>
    /// Interaction logic for LabourLibraryView.xaml
    /// </summary>
    public partial class LabourLibraryView : UserControl
    {
        public LabourLibraryView()
        {
            InitializeComponent();
            WireSuite();

			//// once DataContext is set, hook the edit request
			//this.Loaded += (s, e) =>
			//{
			//	if (this.DataContext is LabourLibraryViewModel vm)
			//	{
			//		vm.EditLabourRequested += OnEditLabourRequested;
			//	}
			//};

			/* hook when DataContext becomes available */
			Loaded += (s, e) =>
			{
				if (DataContext is LabourLibraryViewModel vm)
					vm.EditLabourRequested += OnEditLabourRequested;
			};
		}

		//private void OnEditLabourRequested(LabourModel labour)
		//{
		//	var editVm = new LabourPriceViewModel
		//	{
		//		EditingLabour = labour,
		//		LabourName = labour.LabourName,
		//		LabourPrice = labour.LabourPrice,
		//		LabourUnit = labour.LabourUnit,
		//		NewLabourCategory = labour.LabourCategory,
		//	};

		//	editVm.LabourSaved += saved =>
		//	{
		//		var win = Application.Current.MainWindow as MainWindow;
		//		win?.PopupHost.Hide();
		//	};

		//	var editView = new LabourPriceView
		//	{
		//		DataContext = editVm,
		//	};

		//	var main = Application.Current.MainWindow as MainWindow;
		//	main?.ShowPopup(editView);
		//}

		/* open the popup for editing */
		private void OnEditLabourRequested(LabourModel labour)
		{
			// build a fresh ViewModel – converts price NGN → display currency
			var editVm = new LabourPriceViewModel();
			editVm.LoadForEdit(labour);

			/* ❶ – ► PERSIST the change when the user clicks *Update Library* */
			editVm.LabourSaved += saved =>
			{
				/* push the change back into the library → also writes JSON */
				((LabourLibraryViewModel)DataContext!).AddOrUpdateLabour(saved);

				/* close the popup */
				((MainWindow)Application.Current.MainWindow).PopupHost.Hide();
			};

			/* show the popup */
			((MainWindow)Application.Current.MainWindow)
				.ShowPopup(new LabourPriceView { DataContext = editVm });
		}


		/* ---------- suite table: sort, open, reflow, empty ---------------------------- */

		private System.ComponentModel.ICollectionView? View =>
			LabourGrid?.ItemsSource as System.ComponentModel.ICollectionView
			?? ((LabourGrid?.ItemsSource is System.Collections.IEnumerable e) ? CollectionViewSource.GetDefaultView(e) : null);

		private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
			Suite.LibraryTable.Sort(View, SortBox.SelectedIndex, "LabourPrice", "LabourName");

		private void Grid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
			Suite.LibraryTable.OpenRowUnder(e, LabourGrid, DataContext, "EditLabourCommand");

		private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key != Key.Enter) return;
			Suite.LibraryTable.Run(DataContext, "EditLabourCommand", LabourGrid.SelectedItem);
			e.Handled = true;
		}

		private void WireSuite()
		{
			SizeChanged += (_, e) => Suite.LibraryTable.Reflow(e.NewSize.Width, Bar, FindBox, CategoryBox, SortBox, UpdateButton);
			Loaded += (_, __) => Suite.LibraryTable.WatchEmpty(View, None, "labour");
		}
	}
}
