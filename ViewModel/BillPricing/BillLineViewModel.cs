using System;
using ADLMRateGen.Services.Bill;

namespace ADLMRateGen.ViewModel.BillPricing
{
    /// <summary>
    /// One row of the client's bill on the review screen: a section or heading (shown,
    /// never priced), a sum (priced by hand in the bill), or a measured item that
    /// takes a RateGen rate. Nothing is priced until the user accepts a rate.
    /// </summary>
    public sealed class BillLineViewModel : ViewModelBase
    {
        public BillLineViewModel(ClientBillRow row, bool priceable)
        {
            Row = row;
            IsPriceable = priceable;
            Qty = BillMoney.ParseQty(row.ExistingQty);
            UnitKey = ClientBillUnits.Canonical(row.Unit);
            Context = BillCandidatePicker.ContextOf(row);
        }

        public ClientBillRow Row { get; }
        public string Id => Row.Id;
        public string SheetName => Row.SheetName;
        public string ItemRef => Row.ItemRef ?? "";
        public string Description => Row.Description ?? "";
        public string Unit => Row.Unit ?? "";
        public string UnitKey { get; }
        public string Section => Row.Section ?? "";
        /// <summary>Section › headings › the item it continues › its own words: how the line is read.</summary>
        public string Context { get; }
        public decimal? Qty { get; }

        public bool IsTitle => Row.Kind == ClientBillRowKind.Section || Row.Kind == ClientBillRowKind.Heading;
        public bool IsSum => Row.Kind == ClientBillRowKind.Sum;
        /// <summary>A measured item in a unit RateGen knows: the only kind that takes a rate.</summary>
        public bool IsPriceable { get; }

        /// <summary>The rate ADLM AI proposed, with how sure it was.</summary>
        public BillRate? Suggested { get; private set; }
        public double Confidence { get; private set; }
        public string Reason { get; private set; } = "";

        private BillRate? _rate;
        /// <summary>The rate the user accepted or chose. Only this prices the line.</summary>
        public BillRate? Rate
        {
            get => _rate;
            private set
            {
                _rate = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsPriced));
                RaisePropertyChanged(nameof(UnitRate));
                RaisePropertyChanged(nameof(Amount));
                RaisePropertyChanged(nameof(MatchName));
                RaisePropertyChanged(nameof(Status));
                RaisePropertyChanged(nameof(NeedsAttention));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? Changed;

        public bool IsPriced => _rate != null;
        public decimal? UnitRate => _rate?.Rate;
        public decimal Amount => _rate != null && Qty.HasValue ? BillMoney.Amount(Qty.Value, _rate.Rate) : 0m;

        /// <summary>The accepted rate, else the suggestion, for the table's rate column.</summary>
        public string MatchName => (_rate ?? Suggested)?.Name ?? "";
        public string MatchTrade => (_rate ?? Suggested)?.Trade ?? "";

        public string ConfidenceText => Suggested == null ? "" : $"{Confidence:P0}";

        public string Status
        {
            get
            {
                if (IsTitle) return "";
                if (IsSum) return "Sum: price in the bill";
                if (!IsPriceable) return string.IsNullOrEmpty(Row.Warning) ? "Not a measured item" : Row.Warning!;
                if (_rate != null) return Qty.HasValue ? "Priced" : "Priced, no quantity in the bill";
                if (Suggested != null) return "Suggested, check it";
                return "No match: choose a rate";
            }
        }

        /// <summary>A measured item still waiting for the user: a suggestion to check, or no match.</summary>
        public bool NeedsAttention => IsPriceable && _rate == null;

        public void Suggest(BillRate rate, double confidence, string reason)
        {
            Suggested = rate;
            Confidence = confidence;
            Reason = reason ?? "";
            RaisePropertyChanged(nameof(Suggested));
            RaisePropertyChanged(nameof(ConfidenceText));
            RaisePropertyChanged(nameof(Reason));
            RaisePropertyChanged(nameof(MatchName));
            RaisePropertyChanged(nameof(MatchTrade));
            RaisePropertyChanged(nameof(Status));
        }

        public void Accept() { if (Suggested != null && IsPriceable) Rate = Suggested; }
        public void Use(BillRate rate) { if (IsPriceable) Rate = rate; }
        public void Clear() => Rate = null;
    }
}
