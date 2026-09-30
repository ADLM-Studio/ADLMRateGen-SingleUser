using System;
using ADLMRateGen.Command;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using ADLMRateGen.View;
using ADLMRateGen.ViewModel.Model;
using ADLMRateGen.ViewModel;
using System.Windows.Controls;
using System.Windows.Input;

namespace ADLMRateGen.ViewModel
{
	public sealed class LibraryShellViewModel : ViewModelBase
	{
		// rename these so ShowMaterialPopup can find them:
		public MaterialLibraryViewModel MaterialLibraryViewModel { get; }
		public LabourLibraryViewModel LabourLibraryViewModel { get; }

		

		// events bubble up to MainWindow so it can show the popup
		public event Action RequestAddMaterial;
		public event Action RequestAddLabour;
		public event Action<MaterialModel> RequestEditMaterial;
		public event Action<LabourModel> RequestEditLabour;
		public ICommand AddCommand { get; }

		/* ───────────────── pricing location ───────────────── */

		/// <summary>
		/// Where the prices on screen came from.
		///
		/// Read-only on purpose. The pricing location is set on the ADLM profile on
		/// the website and arrives here on the next sign-in. There used to be a
		/// picker here as well, which meant two places could disagree about where a
		/// user prices from and nothing said which had won. Removing the control
		/// should not remove the answer, so the note stays.
		///
		/// Names the zone, not just the state, because prices are evidenced at zone
		/// level: two states in the same zone read alike until one is priced on its
		/// own, and a screen that hid that would make it look like a fault.
		/// </summary>
		public string LocationNote
		{
			get
			{
				var state = NigerianStates.Find(MasterLibrarySyncService.ResolveState());
				return state == null
					? ""
					: $"{state.Label}: priced from {state.Zone.Replace('_', ' ')} rates. Set this on your ADLM profile.";
			}
		}

		/// <summary>Call after a sync so the note follows the account.</summary>
		public void RefreshLocationNote()
		{
			// The sync finishes off the UI thread, so come back before raising.
			var d = Application.Current?.Dispatcher;
			if (d != null && !d.CheckAccess()) { d.Invoke(RefreshLocationNote); return; }

			RaisePropertyChanged(nameof(LocationNote));
		}

		/* ───────────────── your prices vs the server's ───────────────── */

		/// <summary>
		/// Rows where a price the user typed disagrees with a price the server has
		/// since moved. The user's figure is already in force: the sync preserved
		/// it. This is an offer to switch, not a warning that something was lost.
		/// </summary>
		public ObservableCollection<PriceConflictRow> PriceConflictRows { get; } = new();

		public bool HasPriceConflicts => PriceConflicts.Any;

		/// <summary>
		/// Hiding is not deciding. The rows stay pending so the bell can bring the
		/// review back; only <see cref="KeepMyPricesCommand"/> and
		/// <see cref="UseServerPricesCommand"/> actually settle anything.
		/// </summary>
		private bool _isPriceConflictsHidden;

		public bool IsPriceConflictPanelVisible => HasPriceConflicts && !_isPriceConflictsHidden;

		/// <summary>There is a review waiting, but the user put it away.</summary>
		public bool HasHiddenPriceConflicts => HasPriceConflicts && _isPriceConflictsHidden;

		public string PriceConflictSummary
		{
			get
			{
				var n = PriceConflicts.Count;
				if (n == 0) return "";
				return n == 1
					? "1 rate you edited has a newer published price"
					: $"{n} rates you edited have newer published prices";
			}
		}

		/// <summary>Your figures are already the ones in force; this is an offer.</summary>
		public string PriceConflictDetail =>
			PriceConflicts.Count == 1
				? "Yours is being used. Tick it to take the published price instead."
				: "Yours are being used. Tick the ones you want to take the published price.";

		public int SelectedConflictCount => PriceConflictRows.Count(r => r.IsSelected);

		public string SelectionSummary =>
			$"{SelectedConflictCount} of {PriceConflictRows.Count} selected";

		public bool HasSelectedConflicts => SelectedConflictCount > 0;

		/// <summary>Tri-state so the header box shows a dash on a partial selection.</summary>
		public bool? AllConflictsSelected
		{
			get
			{
				if (PriceConflictRows.Count == 0) return false;
				var n = SelectedConflictCount;
				if (n == 0) return false;
				if (n == PriceConflictRows.Count) return true;
				return null;
			}
			set
			{
				// A click on an indeterminate box means "take all of them".
				var target = value ?? true;
				foreach (var r in PriceConflictRows) r.IsSelected = target;
				RaiseSelectionState();
			}
		}

		private void RaiseSelectionState()
		{
			RaisePropertyChanged(nameof(SelectedConflictCount));
			RaisePropertyChanged(nameof(SelectionSummary));
			RaisePropertyChanged(nameof(HasSelectedConflicts));
			RaisePropertyChanged(nameof(AllConflictsSelected));
		}

		private void RebuildConflictRows()
		{
			// Selections the user has already made survive a re-sync that leaves the
			// same row pending.
			var kept = PriceConflictRows
				.ToDictionary(r => (r.IsLabour ? "L|" : "M|") + r.Source.RowKey,
							  r => r.IsSelected,
							  StringComparer.OrdinalIgnoreCase);

			PriceConflictRows.Clear();
			foreach (var e in PriceConflicts.Pending)
			{
				var row = new PriceConflictRow(e);
				if (kept.TryGetValue((e.IsLabour ? "L|" : "M|") + e.RowKey, out var wasSelected))
					row.IsSelected = wasSelected;
				row.SelectionChanged = RaiseSelectionState;
				PriceConflictRows.Add(row);
			}

			// A review that has emptied out should not reopen itself later.
			if (!PriceConflicts.Any) _isPriceConflictsHidden = false;
		}

		public ICommand UseServerPricesCommand { get; }
		public ICommand KeepMyPricesCommand { get; }
		public ICommand HidePriceConflictsCommand { get; }
		public ICommand ShowPriceConflictsCommand { get; }

		private void RaiseConflictState()
		{
			// The sync runs off the UI thread, and Archives is an ObservableCollection
			// bound to a list, so this has to come back to the dispatcher.
			var d = Application.Current?.Dispatcher;
			if (d != null && !d.CheckAccess()) { d.Invoke(RaiseConflictState); return; }

			RebuildConflictRows();

			RaisePropertyChanged(nameof(HasPriceConflicts));
			RaisePropertyChanged(nameof(IsPriceConflictPanelVisible));
			RaisePropertyChanged(nameof(HasHiddenPriceConflicts));
			RaisePropertyChanged(nameof(PriceConflictSummary));
			RaisePropertyChanged(nameof(PriceConflictDetail));
			RaiseSelectionState();
		}

		/* ───────────────── archive and undo ───────────────── */

		/// <summary>
		/// Snapshots taken before anything rewrote the library. The most recent is
		/// first, because undoing the last thing that happened is what this is for
		/// nearly every time.
		/// </summary>
		public ObservableCollection<LibraryArchive.Entry> Archives { get; } = new();

		private LibraryArchive.Entry _selectedArchive;
		public LibraryArchive.Entry SelectedArchive
		{
			get => _selectedArchive;
			set { _selectedArchive = value; RaisePropertyChanged(); }
		}

		private bool _isArchiveOpen;
		public bool IsArchiveOpen
		{
			get => _isArchiveOpen;
			set { _isArchiveOpen = value; RaisePropertyChanged(); }
		}

		public ICommand OpenArchiveCommand { get; }
		public ICommand RestoreCommand { get; }

		private void RefreshArchives()
		{
			Archives.Clear();
			foreach (var e in LibraryArchive.List()) Archives.Add(e);
			SelectedArchive = Archives.FirstOrDefault();
		}

		private void RestoreSelected()
		{
			var pick = SelectedArchive;
			if (pick == null) return;

			var ask = ADLMRateGen.Helpers.AppMessage.Show(
				$"Restore the material and labour libraries as they were on {pick.TakenAt:dd MMM yyyy} at {pick.TakenAt:HH:mm}?\n\n"
				+ $"{pick.MaterialRows} materials and {pick.LabourRows} labour rates will be put back.\n\n"
				+ "The current library is archived first, so this can itself be undone.",
				"Restore library", MessageBoxButton.OKCancel, MessageBoxImage.Question);
			if (ask != MessageBoxResult.OK) return;

			if (LibraryArchive.Restore(pick.Id))
			{
				MaterialLibraryViewModel.ReloadFromDisk();
				LabourLibraryViewModel.ReloadFromDisk();
				PriceConflicts.KeepMine();   // the restored figures are now the ones in force
				IsArchiveOpen = false;
				RefreshArchives();
			}
			else
			{
				ADLMRateGen.Helpers.AppMessage.Show("That archive could not be read, so nothing was changed.",
					"Restore library", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		}

		private readonly UserControl _materialView = new MaterialLibraryView();
		private readonly UserControl _labourView = new LabourLibraryView();

		private bool _isMaterialTab = true;
		public bool IsMaterialTab
		{
			get => _isMaterialTab;
			set
			{
				if (_isMaterialTab == value) return;
				_isMaterialTab = value;
				RaisePropertyChanged();
				if (value)
				{
					IsLabourTab = false;
					CurrentContent = _materialView;
					RaisePropertyChanged(nameof(AddButtonText));
				}
			}
		}

		private bool _isLabourTab;
		public bool IsLabourTab
		{
			get => _isLabourTab;
			set
			{
				if (_isLabourTab == value) return;
				_isLabourTab = value;
				RaisePropertyChanged();
				if (value)
				{
					IsMaterialTab = false;
					CurrentContent = _labourView;
					RaisePropertyChanged(nameof(AddButtonText));
				}
			}
		}

		private UserControl _currentContent;
		public UserControl CurrentContent
		{
			get => _currentContent;
			private set { _currentContent = value; RaisePropertyChanged(); }
		}

		public string AddButtonText => IsMaterialTab ? "Add Material  +" : "Add Labour  +";

		public LibraryShellViewModel(
			MaterialLibraryViewModel materialLibraryVm,
			LabourLibraryViewModel labourLibraryVm)
		{
			MaterialLibraryViewModel = materialLibraryVm;
			LabourLibraryViewModel = labourLibraryVm;

			// set the views’ data contexts
			_materialView.DataContext = MaterialLibraryViewModel;
			_labourView.DataContext = LabourLibraryViewModel;
			_currentContent = _materialView;

			AddCommand = new RelayCommand(_ =>
			{
				if (IsMaterialTab) RequestAddMaterial?.Invoke();
				else RequestAddLabour?.Invoke();     // ← **fixed**
			});

			MaterialLibraryViewModel.EditMaterialRequested += m => RequestEditMaterial?.Invoke(m);
			LabourLibraryViewModel.EditLabourRequested += l => RequestEditLabour?.Invoke(l);   // ← added

			OpenArchiveCommand = new RelayCommand(_ =>
			{
				RefreshArchives();
				IsArchiveOpen = !IsArchiveOpen;
			});

			RestoreCommand = new RelayCommand(_ => RestoreSelected());

			// Both buttons act on the ticked rows only, so a user can take three of
			// five published prices and keep their own on the rest. Rows left
			// unticked stay pending and the panel stays up with what is left.
			UseServerPricesCommand = new RelayCommand(_ =>
			{
				var rows = PriceConflictRows.Where(r => r.IsSelected).Select(r => r.Source).ToList();
				if (rows.Count == 0) return;

				var ask = ADLMRateGen.Helpers.AppMessage.Show(
					$"Take the published price on {rows.Count} rate(s), replacing your own figure?\n\n"
					+ "Your current library is archived first, so this can be undone.",
					"Use published prices", MessageBoxButton.OKCancel, MessageBoxImage.Question);
				if (ask != MessageBoxResult.OK) return;

				DataSourceCloudSync.AcceptServerPrices(rows);   // resolves them as it goes
				MaterialLibraryViewModel.ReloadFromDisk();
				LabourLibraryViewModel.ReloadFromDisk();
				RefreshArchives();
			});

			// Nothing to write: their prices are already the ones in use. This only
			// stops the app asking about the ticked rows again.
			KeepMyPricesCommand = new RelayCommand(_ =>
			{
				var rows = PriceConflictRows.Where(r => r.IsSelected).Select(r => r.Source).ToList();
				if (rows.Count == 0) return;
				PriceConflicts.Resolve(rows);
			});

			HidePriceConflictsCommand = new RelayCommand(_ =>
			{
				_isPriceConflictsHidden = true;
				RaisePropertyChanged(nameof(IsPriceConflictPanelVisible));
				RaisePropertyChanged(nameof(HasHiddenPriceConflicts));
			});

			ShowPriceConflictsCommand = new RelayCommand(_ =>
			{
				_isPriceConflictsHidden = false;
				RaisePropertyChanged(nameof(IsPriceConflictPanelVisible));
				RaisePropertyChanged(nameof(HasHiddenPriceConflicts));
			});

			PriceConflicts.Changed += RaiseConflictState;
			RefreshArchives();
		}
	}
}
