using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ADLMRateGen.ViewModel.CustomRate;

namespace ADLMRateGen.View
{
    /// <summary>
    /// Interaction logic for CustomRateListView.xaml
    /// </summary>
    public partial class CustomRateListView : UserControl
    {
        public CustomRateListView()
        {
            InitializeComponent();

			// whenever DataContext changes, subscribe to its PropertyChanged
			DataContextChanged += OnDataContextChanged;
		}

		/* ───────────────── helpers ───────────────── */


		private PopupHost GlobalPopup =>
			((MainWindow)Application.Current.MainWindow).PopupHost;

		private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			if (e.OldValue is CustomRateListViewModel oldVm)
				oldVm.OnViewRequested -= ListVm_OnViewRequested;

			if (e.NewValue is CustomRateListViewModel newVm)
				newVm.OnViewRequested += ListVm_OnViewRequested;
		}

		private void ListVm_OnViewRequested(CustomRate rate)
		{
			Dispatcher.Invoke(() =>
			{
				// build the editor view on the fly
				var editorView = new CustomRateEntryView();
				var editorVm = new CustomRateEntryViewModel();
				editorVm.LoadRate(rate);            // put it in EDIT mode
				editorVm.Saved += () => GlobalPopup.Hide();
				editorView.DataContext = editorVm;

				GlobalPopup.Show(editorView);
			});
		}

		/// <summary>
		/// The × on a row (the suite design): select that row, confirm, then run
		/// the view model's existing DeleteRateCommand, which deletes SelectedRate.
		/// </summary>
		private void DeleteRow_Click(object sender, RoutedEventArgs e)
		{
			if (DataContext is not CustomRateListViewModel vm) return;
			if ((sender as FrameworkElement)?.DataContext is not CustomRate rate) return;

			var name = string.IsNullOrWhiteSpace(rate.Title) ? "This rate" : rate.Title;
			if (!Suite.SuiteDialog.Ask("Delete this rate?",
					$"{name} is removed from this PC. The ADLM library is not affected. This cannot be undone.",
					"Delete", "Keep it", Suite.SuiteDialog.Tone.Danger)) return;

			vm.SelectedRate = rate;
			if (vm.DeleteRateCommand?.CanExecute(null) == true)
				vm.DeleteRateCommand.Execute(null);
		}

	}
}
