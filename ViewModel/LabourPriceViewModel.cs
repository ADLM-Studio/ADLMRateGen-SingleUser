using ADLMRateGen.Helpers;
using System;
using System.Windows.Input;
using ADLMRateGen.Command;
using ADLMRateGen.Services;
using ADLMRateGen.ViewModel.Model;

namespace ADLMRateGen.ViewModel
{
    public class LabourPriceViewModel : ViewModelBase
    {
        private string _labourName;
        private decimal _labourPrice;      // displayed in current currency
        private string _labourUnit;
        private string _newLabourCategory;
        private string _newLabourNote;
        private LabourModel? _editingLabour;

        public event Action<LabourModel> LabourSaved;
        public event Action<bool, string?>? BusyChanged;

        /// <summary>
        /// Raised when the user presses the sheet's delete control. The labour
        /// library view model does the deleting (rows, file and cloud copy).
        /// Static so exactly one subscriber handles it however the sheet was opened.
        /// </summary>
        public static event Action<LabourModel>? LabourDeleteRequested;

        public string LabourName
        {
            get => _labourName;
            set
            {
                if (_labourName != value)
                {
                    _labourName = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(Spec));
                    RaisePropertyChanged(nameof(HasSpec));
                }
            }
        }

        /// <summary>
        /// The estimator's own specification for this rate. Only rates they add
        /// carry one; the shipped catalogue has its reference text bundled, which
        /// is why the panel below shows whichever of the two exists.
        /// </summary>
        public string NewLabourNote
        {
            get => _newLabourNote;
            set
            {
                if (_newLabourNote != value)
                {
                    _newLabourNote = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(Spec));
                    RaisePropertyChanged(nameof(HasSpec));
                }
            }
        }

        /// <summary>
        /// Reference specification and expected output for the item being edited,
        /// or null where none is recorded. Read-only: a day rate is a proxy for an
        /// output, and this is here so the person setting the rate can see the
        /// output it has to cover.
        ///
        /// A note the user typed wins over the bundled entry. They know their own
        /// plant better than a published table does, and a rate they added has no
        /// bundled entry at all.
        /// </summary>
        public LabourSpec? Spec
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(NewLabourNote))
                    return new LabourSpec
                    {
                        Spec = NewLabourNote,
                        Source = "Your own note"
                    };

                return LabourSpecs.Find(LabourName);
            }
        }

        public bool HasSpec => Spec != null;

        /// <summary>
        /// Price shown in the edit dialog – always in the CURRENT display currency.
        /// Converted to/from NGN on load/save.
        /// </summary>
        public decimal LabourPrice
        {
            get => _labourPrice;
            set
            {
                if (_labourPrice != value)
                {
                    _labourPrice = value;
                    RaisePropertyChanged();
                }
            }
        }

        /// <summary>Current currency code for the price label hint.</summary>
        public string CurrencyLabel => CurrencyService.Instance.Code;

        public string LabourUnit
        {
            get => _labourUnit;
            set
            {
                if (_labourUnit != value)
                {
                    _labourUnit = value;
                    RaisePropertyChanged();
                }
            }
        }
        public string NewLabourCategory
        {
            get => _newLabourCategory;
            set
            {
                if (_newLabourCategory != value)
                {
                    _newLabourCategory = value;
                    RaisePropertyChanged();
                }
            }
        }
        public LabourModel? EditingLabour
        {
            get => _editingLabour;
            set
            {
                if (_editingLabour != value)
                {
                    _editingLabour = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(IsEditing));
                    ((DelegateCommand)SaveLabourCommand).RaiseCanExecuteChanged();
                    ((DelegateCommand)UpdateLabourCommand).RaiseCanExecuteChanged();
                    CanDelete = value != null && UserRowChecker.IsUserLabour(value);
                }
            }
        }
        public bool IsEditing => EditingLabour != null;

        private bool _canDelete;

        /// <summary>
        /// True only while editing a labour item the user added themselves. One
        /// of ADLM's defaults shows no delete control, and the command refuses
        /// it too. The rule is <see cref="UserRowChecker"/>'s.
        /// </summary>
        public bool CanDelete
        {
            get => _canDelete;
            private set
            {
                if (_canDelete == value) return;
                _canDelete = value;
                RaisePropertyChanged();
                ((DelegateCommand)DeleteLabourCommand).RaiseCanExecuteChanged();
            }
        }

        public ICommand SaveLabourCommand { get; }
        public ICommand UpdateLabourCommand { get; }
        public ICommand DeleteLabourCommand { get; }

        public LabourPriceViewModel()
        {
            _newLabourCategory = string.Empty;
            _newLabourNote = string.Empty;
            SaveLabourCommand = new DelegateCommand(o => SaveLabour(), o => !IsEditing);
            UpdateLabourCommand = new DelegateCommand(o => UpdateLabour(), o => IsEditing);
            DeleteLabourCommand = new DelegateCommand(o => DeleteLabour(), o => CanDelete);
            EditingLabour = null;

            // Keep CurrencyLabel up to date when currency changes
            CurrencyService.Instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CurrencyService.Code))
                    RaisePropertyChanged(nameof(CurrencyLabel));
            };
        }

        /// <summary>
        /// Call this when loading a labour item into the edit form.
        /// Converts the stored NGN price → current display currency.
        /// </summary>
        public void LoadForEdit(LabourModel labour)
        {
            EditingLabour = labour;
            LabourName = labour.LabourName;
            LabourUnit = labour.LabourUnit;
            LabourPrice = CurrencyService.Instance.FromNgn(labour.LabourPrice); // NGN → display
            NewLabourCategory = labour.LabourCategory ?? string.Empty;
            NewLabourNote = labour.LabourNote ?? string.Empty;
        }

        private void SaveLabour()
        {
            if (!string.IsNullOrEmpty(LabourName))
            {
                // User entered price in current currency – convert back to NGN for storage
                var priceNgn = CurrencyService.Instance.ToNgn(LabourPrice);

                string categoryToUse = !string.IsNullOrEmpty(NewLabourCategory) ? NewLabourCategory : string.Empty;
                var newLabour = new LabourModel
                {
                    SerialNumber = 0,
                    LabourName = this.LabourName,
                    LabourUnit = this.LabourUnit,
                    LabourPrice = priceNgn,
                    LabourCategory = categoryToUse,
                    LabourNote = NewLabourNote ?? string.Empty,
                };
                LabourSaved?.Invoke(newLabour);
                ClearInputFields();
            }
        }

        private void UpdateLabour()
        {
            if (EditingLabour != null)
            {
                EditingLabour.LabourUnit = LabourUnit;
                // User edited price in current currency – convert back to NGN for storage
                EditingLabour.LabourPrice = CurrencyService.Instance.ToNgn(LabourPrice);

                string categoryToUse = !string.IsNullOrEmpty(NewLabourCategory)
                    ? NewLabourCategory : EditingLabour.LabourCategory;
                EditingLabour.LabourCategory = categoryToUse;
                EditingLabour.LabourNote = NewLabourNote ?? string.Empty;

                LabourSaved?.Invoke(EditingLabour);
                ClearInputFields();
                EditingLabour = null;
            }
        }

        /* ───────── delete, user-added rows only ───────── */
        private void DeleteLabour()
        {
            var target = EditingLabour;
            // Checked again here, not only in CanExecute: a default never leaves this method.
            if (target == null || !UserRowChecker.IsUserLabour(target)) return;

            ClearInputFields();
            EditingLabour = null;
            LabourDeleteRequested?.Invoke(target);
        }

        private void ClearInputFields()
        {
            LabourName = string.Empty;
            LabourUnit = string.Empty;
            LabourPrice = 0;
            NewLabourCategory = string.Empty;
            NewLabourNote = string.Empty;
        }
    }
}
