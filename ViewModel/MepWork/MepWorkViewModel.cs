using ADLMRateGen.Command;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using ADLMRateGen.View;
using ADLMRateGen.ViewModel.CustomRate;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;

namespace ADLMRateGen.ViewModel.MepWork
{
	/// <summary>
	/// Mechanical, electrical and plumbing rate build-ups.
	///
	/// This engine differs from the concrete and blockwork ones in an important way.
	/// Those start from bare material prices and add labour, plant and waste. The MEP
	/// catalog rows are already INSTALLED rates - the source bills price MEP as
	/// "supply, fix, connect & commission" - so this engine must NOT add a labour
	/// line, or every rate double counts the fixing.
	///
	/// What it does instead:
	///   - composes the components that make up a complete measured item, where the
	///     source bill measures them as separate lines against the same physical
	///     point (a lighting point plus the fitting it serves, for example)
	///   - applies the user's own overhead and profit percentages
	///
	/// Every component is shown as its own breakdown line so the quantity surveyor
	/// can see exactly what is in the rate and strike anything already covered
	/// elsewhere in their bill.
	/// </summary>
	public class MepWorkViewModel : ViewModelBase
	{
		/// <summary>Registered in <see cref="Services.SectionKeys"/>, so MEP rates take part
		/// in user quantity overrides and sync to QUIV and HERON like every other section.</summary>
		public const string SectionKey = Services.SectionKeys.Mep;

		private readonly GetItemsFromDB _helper;
		private readonly MaterialLibraryViewModel _matLib;
		private readonly ComputeItemEngine _compute;

		private double _overheadPercent = 10.0;
		private double _profitPercent = 25.0;
		private string _searchTerm = string.Empty;
		private object _selectedDetail;
		private string _selectedSection = "All";

		public double OverheadPercent
		{
			get => _overheadPercent;
			set
			{
				if (_overheadPercent != value)
				{
					_overheadPercent = value;
					RaisePropertyChanged();
					RecomputeAll();
				}
			}
		}

		public double ProfitPercent
		{
			get => _profitPercent;
			set
			{
				if (_profitPercent != value)
				{
					_profitPercent = value;
					RaisePropertyChanged();
					RecomputeAll();
				}
			}
		}

		public ObservableCollection<MepWorkItem> MepWorkItems { get; set; } = new();
		public ICollectionView MepWorkCollectionView { get; private set; }

		public ObservableCollection<string> MepSections { get; } = new()
		{
			"All", "Lighting", "Power", "Cables", "Earthing", "Containment",
			"Sanitary", "Air Conditioning & Ventilation", "Fire Protection", "Security"
		};

		/* ───────────── services, split the way a bill is (30 Sep 2026) ─────────────
		   The rail shows four services rather than one MEP heap: Mechanical,
		   Electrical, Plumbing and Fire. One engine still prices them all, so a
		   rate and its overrides are the same whichever list it is opened from;
		   the discipline only decides which rows are listed. */
		public const string Mechanical = "Mechanical";
		public const string Electrical = "Electrical";
		public const string Plumbing = "Plumbing";
		public const string Fire = "Fire";

		public static string DisciplineOf(string? section) => section switch
		{
			Mechanical or Electrical or Plumbing or Fire => section!,
			"Air Conditioning & Ventilation" => Mechanical,
			"Sanitary" or "Water Supply" or "Soil & Waste" or "Rainwater" => Plumbing,
			"Fire Protection" => Fire,
			_ => Electrical
		};

		private string _discipline = Electrical;

		/// <summary>Which of the four services is listed.</summary>
		public string Discipline
		{
			get => _discipline;
			set
			{
				if (_discipline == value) return;
				_discipline = value;
				RaisePropertyChanged();
				RaisePropertyChanged(nameof(DisciplineTitle));
				MepWorkCollectionView?.Refresh();
			}
		}

		public string DisciplineTitle => Discipline + " Services Computation";

		public string SelectedSection
		{
			get => _selectedSection;
			set
			{
				if (_selectedSection != value)
				{
					_selectedSection = value;
					RaisePropertyChanged();
					MepWorkCollectionView.Refresh();
				}
			}
		}

		public string SearchTerm
		{
			get => _searchTerm;
			set
			{
				if (_searchTerm != value)
				{
					_searchTerm = value;
					RaisePropertyChanged();
					MepWorkCollectionView.Refresh();
				}
			}
		}

		public object SelectedDetail
		{
			get => _selectedDetail;
			set
			{
				if (_selectedDetail != value)
				{
					_selectedDetail = value;
					RaisePropertyChanged();
				}
			}
		}

		public ICommand RecomputeCommand { get; }
		public ICommand ShowDetailsCommand { get; }
		public ICommand AddCustomRateCommand { get; }

		public MepWorkViewModel(MaterialLibraryViewModel matLib, LabourLibraryViewModel labourLib)
		{
			_helper = new GetItemsFromDB(matLib, labourLib);
			_matLib = matLib;
			_compute = new ComputeItemEngine(_helper.GetMaterialPrice, _helper.GetLabourRate);
			matLib.LibraryChanged += OnLibraryChanged;
			ComputeCatalogStore.Changed += () =>
			{
				var disp = System.Windows.Application.Current?.Dispatcher;
				if (disp == null || disp.CheckAccess()) RecomputeAll();
				else disp.BeginInvoke((Action)RecomputeAll);
			};
			labourLib.LibraryChanged += OnLibraryChanged;

			BuildMepWorkItems();

			MepWorkCollectionView = CollectionViewSource.GetDefaultView(MepWorkItems);
			MepWorkCollectionView.Filter = FilterMepWorkItem;

			RecomputeCommand = new DelegateCommand(_ => RecomputeAll());
			ShowDetailsCommand = new DelegateCommand(o => ShowDetails(o));
			AddCustomRateCommand = new DelegateCommand(_ => OpenCustomRateEntry());

			CurrencyService.Instance.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName is nameof(CurrencyService.Rate) or nameof(CurrencyService.Code))
					RecomputeAll();
			};

			Services.UserRateEditStore.Current.OverridesChanged += (_, __) =>
			{
				var disp = System.Windows.Application.Current?.Dispatcher;
				if (disp == null || disp.CheckAccess()) RecomputeAll();
				else disp.BeginInvoke((Action)RecomputeAll);
			};
		}

		private void OnLibraryChanged() => RecomputeAll();

		/// <summary>
		/// Rebuild a single item after the user edits one of its quantities, without
		/// rebuilding the whole grid and losing the open detail view.
		/// </summary>
		public void RecomputeItemInPlace(int itemNo)
		{
			var existing = MepWorkItems.FirstOrDefault(i => i.ItemNo == itemNo);
			if (existing == null) return;

			var rebuilt = new ObservableCollection<MepWorkItem>();
			var saved = MepWorkItems;
			try
			{
				MepWorkItems = rebuilt;
				BuildMepWorkItems();
			}
			finally
			{
				MepWorkItems = saved;
			}

			var fresh = rebuilt.FirstOrDefault(i => i.ItemNo == itemNo);
			if (fresh == null) return;

			existing.NetCost = fresh.NetCost;
			existing.OverheadValue = fresh.OverheadValue;
			existing.ProfitValue = fresh.ProfitValue;
			existing.TotalCost = fresh.TotalCost;

			existing.MepBreakdownLine.Clear();
			foreach (var line in fresh.MepBreakdownLine) existing.MepBreakdownLine.Add(line);

			MepWorkCollectionView?.Refresh();
		}

		private void RecomputeAll()
		{
			MepWorkItems.Clear();
			BuildMepWorkItems();
		}

		private bool FilterMepWorkItem(object obj)
		{
			if (obj is not MepWorkItem item) return false;

			if (DisciplineOf(item.Section) != Discipline) return false;

			if (SelectedSection != "All" &&
				!string.Equals(item.Section, SelectedSection, StringComparison.OrdinalIgnoreCase))
				return false;

			if (string.IsNullOrEmpty(SearchTerm)) return true;
			return item.Description?.IndexOf(SearchTerm, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private void ShowDetails(object o)
		{
			if (o is MepWorkItem item)
			{
				var detail = new MepWorkItemDetailControl { DataContext = item };
				detail.BackRequested += () => SelectedDetail = null;
				SelectedDetail = detail;
			}
		}

		private void OpenCustomRateEntry()
		{
			var view = new CustomRateEntryView { DataContext = new CustomRateEntryViewModel() };
			SelectedDetail = view;
		}

		/* ───────────────────── helpers ───────────────────── */

		private double Price(string name) => _helper.GetMaterialPrice(name);

		private (double overheadVal, double profitVal, double total) ApplyOHP(double netCost)
		{
			double ov = netCost * (OverheadPercent / 100);
			double pv = netCost * (ProfitPercent / 100);
			return (ov, pv, netCost + ov + pv);
		}

		private int _itemNo;

		/// <summary>
		/// Build one item from a set of catalog components. Each component becomes a
		/// visible breakdown line, so nothing is hidden inside the rate.
		/// </summary>
		private MepWorkItem Compose(string section, string description, string unit,
									params (string catalogName, double qty, string qtyUnit)[] parts)
			=> Build(section, description, unit, parts, Array.Empty<(string, double, string)>());

		/// <summary>
		/// A rate built from SUPPLY prices plus the labour to fix them (materials
		/// listed first, then labour, as the builder lists them), for the
		/// catalogue rows priced supply-only (plumbing pipework, valves, drainage).
		/// These are the one place in services where labour is added, because the
		/// price does not already include it. Outputs are the library's own, from
		/// Data/labourSpecs.json, cited at each item; nothing here is estimated.
		/// </summary>
		private MepWorkItem Build(string section, string description, string unit,
								  (string catalogName, double qty, string qtyUnit)[] parts,
								  (string labourName, double qty, string qtyUnit)[] labour)
		{
			int itemNo = ++_itemNo;
			var lines = new ObservableCollection<MepWorkBreakdownLine>();
			double net = 0;

			foreach (var (catalogName, defaultQty, qtyUnit) in parts)
			{
				// A quantity the user edited on this line wins over the shipped default,
				// exactly as in the concrete and blockwork engines.
				double qty = Services.UserRateEditStore.Current.Qty(SectionKey, itemNo, catalogName, defaultQty);
				double unitPrice = Price(catalogName);
				double total = unitPrice * qty;
				net += total;
				lines.Add(new MepWorkBreakdownLine
				{
					ComponentName = catalogName,
					Quantity = qty,
					Unit = qtyUnit,
					UnitPrice = unitPrice,
					TotalPrice = total
				});
			}

			foreach (var (labourName, defaultQty, qtyUnit) in labour)
			{
				double qty = Services.UserRateEditStore.Current.Qty(SectionKey, itemNo, labourName, defaultQty);
				double rate = _helper.GetLabourRate(labourName);
				double total = rate * qty;
				net += total;
				lines.Add(new MepWorkBreakdownLine
				{
					ComponentName = labourName,
					Quantity = qty,
					Unit = qtyUnit,
					UnitPrice = rate,
					TotalPrice = total
				});
			}

			var ohp = ApplyOHP(net);

			lines.Add(new MepWorkBreakdownLine
			{
				ComponentName = labour.Length == 0
					? "Net cost (supply & install, from library)"
					: "Net cost (supply and labour, from library)",
				Quantity = 1,
				Unit = unit,
				TotalPrice = net
			});
			lines.Add(new MepWorkBreakdownLine
			{
				ComponentName = $"Overhead @ {OverheadPercent:0.##}%",
				Quantity = 1,
				Unit = "",
				TotalPrice = ohp.overheadVal
			});
			lines.Add(new MepWorkBreakdownLine
			{
				ComponentName = $"Profit @ {ProfitPercent:0.##}%",
				Quantity = 1,
				Unit = "",
				TotalPrice = ohp.profitVal
			});
			lines.Add(new MepWorkBreakdownLine
			{
				ComponentName = "Total rate",
				Quantity = 1,
				Unit = unit,
				TotalPrice = ohp.total
			});

			return new MepWorkItem
			{
				ItemNo = itemNo,
				Section = section,
				Description = description,
				Unit = unit,
				NetCost = Math.Round(net, 2),
				OverheadValue = Math.Round(ohp.overheadVal, 0),
				ProfitValue = Math.Round(ohp.profitVal, 0),
				TotalCost = Math.Round(ohp.total, 2),
				MepBreakdownLine = lines
			};
		}

		/* ───────────────────── item definitions ───────────────────── */

		private const string PtLight = "Point wiring, lighting point, concealed PVC conduit";
		private const string PtPower = "Point wiring, socket/power point, concealed PVC conduit";

		private void BuildMepWorkItems()
		{
			_itemNo = 0;
			BuildLocalItems();
			AppendCloudServices();
		}

		/* ───────────── the services ADLM publishes from the cloud ─────────────
		   The AI service's building-services build-ups (sockets, lighting, earthing,
		   fans, split units, fire, pipework, valves, sanitary fittings, tank, pump)
		   arrive in the cloud's "carbon" section; ServiceRouting files each under
		   its service by the library category of its materials. Where one prices
		   the same thing as a rate built here, the published build-up wins (it
		   carries fittings and waste) and the local one steps aside.

		   Numbered from 201 in a fixed order, so a quantity someone edits keeps
		   its item number however the list changes around it. */
		private const int CloudNumberBase = 200;

		private string? CategoryOf(string name) =>
			_matLib.MaterialLibrary.FirstOrDefault(m => m.MaterialName == name)?.MaterialCategory;

		private void AppendCloudServices()
		{
			var defs = (ComputeCatalogStore.Items ?? Array.Empty<ComputeItemDefinition>())
				.Where(d => d != null && d.enabled)
				.OrderBy(d => d.id, StringComparer.Ordinal)
				.ToList();
			if (defs.Count == 0) return;

			var cloudPrimaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int n = CloudNumberBase;
			foreach (var def in defs)
			{
				n++;
				var discipline = ServiceRouting.DisciplineOf(def, CategoryOf);
				if (discipline == null) continue;

				var item = ComposeCloud(def, discipline, n);
				if (item == null) continue;
				MepWorkItems.Add(item);

				var primary = def.lines.FirstOrDefault(l => string.Equals(l.kind, "material", StringComparison.OrdinalIgnoreCase));
				if (primary != null) cloudPrimaries.Add(primary.refName ?? primary.description);
			}

			// local rates whose main component the cloud already prices
			foreach (var local in MepWorkItems.Where(i => i.ItemNo <= CloudNumberBase).ToList())
			{
				var first = local.MepBreakdownLine.FirstOrDefault()?.ComponentName;
				if (first != null && cloudPrimaries.Contains(first)) MepWorkItems.Remove(local);
			}
		}

		private MepWorkItem? ComposeCloud(ComputeItemDefinition def, string discipline, int itemNo)
		{
			ComputeItemEngine.Result computed;
			try { computed = _compute.Compute(def); }
			catch { return null; }

			var lines = new ObservableCollection<MepWorkBreakdownLine>();
			double net = 0;
			foreach (var l in computed.Lines)
			{
				// every quantity stays the QS's to change, as in every other build-up
				double qty = Services.UserRateEditStore.Current.Qty(SectionKey, itemNo, l.Name, (double)l.Qty);
				// Show the rate actually used per unit of the line: a labour line in
				// hours carries the day rate times the published hourly factor, and
				// "0.35 hr x 10,000/day" would not add up on the screen.
				double unitPrice = (double)l.Qty == 0 ? (double)l.UnitPrice : (double)(l.Total / l.Qty);
				double total = (double)l.Qty == 0 ? (double)l.Total : unitPrice * qty;
				net += total;
				lines.Add(new MepWorkBreakdownLine
				{
					ComponentName = l.Name,
					Quantity = qty,
					Unit = l.Unit ?? "",
					UnitPrice = unitPrice,
					TotalPrice = total
				});
			}
			// The published item carries a "PO" uplift of 35%: ADLM's own 10%
			// overhead plus 25% profit, baked in. The QS's overhead and profit are
			// applied below, as on every other rate, so the uplift is left out
			// rather than charged twice.
			if (!(net > 0)) return null;

			var ohp = ApplyOHP(net);
			lines.Add(new MepWorkBreakdownLine { ComponentName = "Net cost (published by ADLM, priced from your library)", Quantity = 1, Unit = def.outputUnit ?? "", TotalPrice = net });
			lines.Add(new MepWorkBreakdownLine { ComponentName = $"Overhead @ {OverheadPercent:0.##}%", Quantity = 1, TotalPrice = ohp.overheadVal });
			lines.Add(new MepWorkBreakdownLine { ComponentName = $"Profit @ {ProfitPercent:0.##}%", Quantity = 1, TotalPrice = ohp.profitVal });
			lines.Add(new MepWorkBreakdownLine { ComponentName = "Total rate", Quantity = 1, Unit = def.outputUnit ?? "", TotalPrice = ohp.total });
			foreach (var w in computed.Warnings)
				lines.Add(new MepWorkBreakdownLine { ComponentName = "Check: " + w, Quantity = 0 });

			return new MepWorkItem
			{
				ItemNo = itemNo,
				Section = discipline,
				Description = def.name ?? "Untitled",
				Unit = string.IsNullOrWhiteSpace(def.outputUnit) ? "No." : def.outputUnit,
				NetCost = Math.Round(net, 2),
				OverheadValue = Math.Round(ohp.overheadVal, 0),
				ProfitValue = Math.Round(ohp.profitVal, 0),
				TotalCost = Math.Round(ohp.total, 2),
				MepBreakdownLine = lines
			};
		}

		private void BuildLocalItems()
		{

			// ── Lighting. Point wiring plus the fitting it serves, plus the switch that
			//    controls it. The source bill measures all three separately against the
			//    same installation, which is why they are added rather than assumed to be
			//    included in one another.
			MepWorkItems.Add(Compose("Lighting",
				"Lighting point complete with 12W LED ceiling fitting, concealed conduit, switch and connection", "No.",
				(PtLight, 1, "No."),
				("LED ceiling fitting, 1 x 12W", 1, "No."),
				("10A 1 way 1 gang switch", 1, "No.")));

			MepWorkItems.Add(Compose("Lighting",
				"Lighting point complete with 7W LED spot light, concealed conduit, switch and connection", "No.",
				(PtLight, 1, "No."),
				("LED spot light, ceiling mounted, 1 x 7W", 1, "No."),
				("10A 1 way 1 gang switch", 1, "No.")));

			MepWorkItems.Add(Compose("Lighting",
				"Lighting point complete with 600 x 600mm LED panel, concealed conduit, switch and connection", "No.",
				(PtLight, 1, "No."),
				("LED panel light 600 x 600mm, 4000K", 1, "No."),
				("10A 1 way 1 gang switch", 1, "No.")));

			MepWorkItems.Add(Compose("Lighting",
				"Lighting point complete with 40W x 600mm LED batten, concealed conduit, switch and connection", "No.",
				(PtLight, 1, "No."),
				("LED batten/fluorescent fitting, 40W x 600mm", 1, "No."),
				("10A 1 way 1 gang switch", 1, "No.")));

			MepWorkItems.Add(Compose("Lighting",
				"External lighting point complete with 20W bulkhead fitting, conduit, switch and connection", "No.",
				(PtLight, 1, "No."),
				("External wall bracket (bulkhead) light, 20W", 1, "No."),
				("10A 1 way 1 gang switch", 1, "No.")));

			MepWorkItems.Add(Compose("Lighting",
				"Lighting point complete with 4 x 20W chandelier fitting, conduit, 3 gang switch and connection", "No.",
				(PtLight, 1, "No."),
				("Chandelier fitting, 4 x 20W", 1, "No."),
				("10A 2 way 3 gang switch", 1, "No.")));

			MepWorkItems.Add(Compose("Lighting",
				"Mirror light point complete with 10W fitting, conduit, switch and connection", "No.",
				(PtLight, 1, "No."),
				("Mirror light, 10W", 1, "No."),
				("10A 1 way 1 gang switch", 1, "No.")));

			// ── Power
			MepWorkItems.Add(Compose("Power",
				"13A switched socket outlet point complete, concealed conduit, wiring and connection", "No.",
				(PtPower, 1, "No."),
				("13A 1 gang switched socket outlet", 1, "No.")));

			MepWorkItems.Add(Compose("Power",
				"20A DP switched appliance outlet point complete, concealed conduit, wiring and connection", "No.",
				(PtPower, 1, "No."),
				("20A DP switched socket outlet (appliance)", 1, "No.")));

			MepWorkItems.Add(Compose("Power",
				"Air conditioner point complete with 20A DP isolator, conduit, wiring and connection", "No.",
				(PtPower, 1, "No."),
				("20A DP isolator switch (air conditioner/water heater)", 1, "No.")));

			MepWorkItems.Add(Compose("Power",
				"Water heater point complete with heater, 20A DP isolator, conduit and connection", "No.",
				(PtPower, 1, "No."),
				("Electric water heater, incl. 20A DP switch and connection", 1, "No."),
				("20A DP isolator switch (air conditioner/water heater)", 1, "No.")));

			MepWorkItems.Add(Compose("Power",
				"Distribution board (DB) with MCBs, complete, labelled and connected", "No.",
				("Distribution board (DB) with MCBs, complete and labelled", 1, "No.")));

			// ── Cables, per metre of run
			foreach (var cable in new[]
			{
				"4 core 70mm2 PVC/SWA/PVC copper cable",
				"4 core 35mm2 PVC/SWA/PVC copper cable",
				"4 core 25mm2 PVC/PVC copper cable",
				"5 core 6mm2 PVC/PVC copper cable",
				"5 core 4mm2 PVC/PVC copper cable",
				"1 core 35mm2 PVC/AWA/PVC copper cable",
				"1 core 16mm2 PVC/PVC copper cable",
				"12 core single mode fibre optic cable with LCUPC connectors",
			})
			{
				MepWorkItems.Add(Compose("Cables",
					$"{cable}; laid, terminated and connected", "m",
					(cable, 1, "m")));
			}

			// ── Earthing
			MepWorkItems.Add(Compose("Earthing",
				"Earth electrode installation complete with 19mm x 3m copper rod and inspection pit", "No.",
				("Earth rod, copper, 19mm diameter x 3m long", 1, "No."),
				("Earth pit/chamber with ground rod", 1, "No.")));

			MepWorkItems.Add(Compose("Earthing",
				"35mm2 bare copper earth conductor to ground grid; laid and bonded", "m",
				("35mm2 bare copper earth conductor for ground grid", 1, "m")));

			MepWorkItems.Add(Compose("Earthing",
				"8mm copper round wire earth conductor; fixed and bonded", "m",
				("8mm copper round wire, earthing", 1, "m")));

			// ── Containment. Trenched duct runs, not plumbing pipe.
			foreach (var duct in new[]
			{
				"110mm diameter cable duct, laid in trench, complete",
				"75mm diameter cable duct, laid in trench, complete",
				"50mm diameter cable duct, laid in trench, complete",
				"25mm diameter cable duct, laid in trench, complete",
			})
			{
				MepWorkItems.Add(Compose("Containment", duct, "m", (duct, 1, "m")));
			}

			// ── Sanitary
			MepWorkItems.Add(Compose("Sanitary",
				"Water closet (WC) suite; supply, fix, connect and commission complete", "No.",
				("Water closet (WC) suite, complete with cistern, seat and connections", 1, "No.")));

			MepWorkItems.Add(Compose("Sanitary",
				"Wash hand basin (WHB) with pillar tap, waste, trap and brackets; fixed and connected", "No.",
				("Wash hand basin (WHB) with pillar tap, waste, trap and brackets", 1, "No.")));

			MepWorkItems.Add(Compose("Sanitary",
				"Double bowl kitchen sink with mixer tap, waste and trap; fixed and connected", "No.",
				("Kitchen sink, double bowl, with mixer tap, waste and trap", 1, "No.")));

			MepWorkItems.Add(Compose("Sanitary",
				"Floor drain with trap and grating; fixed and connected", "No.",
				("Floor drain (FD) with trap and grating", 1, "No.")));

			MepWorkItems.Add(Compose("Sanitary",
				"Inspection chamber complete with cover and benching", "No.",
				("Inspection chamber (IC) complete with cover and benching", 1, "No.")));

			// ── Air conditioning and ventilation
			MepWorkItems.Add(Compose("Air Conditioning & Ventilation",
				"1HP split unit air conditioner; supply, install, pipe, charge and commission", "No.",
				("Split unit air conditioner, 1HP, complete with pipework and brackets", 1, "No.")));

			MepWorkItems.Add(Compose("Air Conditioning & Ventilation",
				"2HP split unit air conditioner; supply, install, pipe, charge and commission", "No.",
				("Split unit air conditioner, 2HP, complete with pipework and brackets", 1, "No.")));

			MepWorkItems.Add(Compose("Air Conditioning & Ventilation",
				"Ceiling/duct mounted extractor fan with grille; installed and connected", "No.",
				("Extractor fan, ceiling/duct mounted, with grille and connection", 1, "No.")));

			MepWorkItems.Add(Compose("Air Conditioning & Ventilation",
				"Wall mounted extractor fan with external louvre; installed and connected", "No.",
				("Extractor fan, wall mounted, with external louvre and connection", 1, "No.")));

			MepWorkItems.Add(Compose("Air Conditioning & Ventilation",
				"Ceiling fan complete with regulator; installed and connected", "No.",
				("Ceiling fan complete with regulator", 1, "No.")));

			// ── Plumbing built up from supply prices and the library's labour outputs.
			//    Data/labourSpecs.json:
			//      Pipefitter (PPR fusion and uPVC solvent welding), gang 1 fitter + 1 helper:
			//        "30-60 joints/day, or 20-30 m/day of run pipe"      -> 25 m/day, 45 joints/day
			//      Plumber (skilled), gang 1 plumber + 1.5 to 2 helpers:
			//        "8-10 m/day supply pipe, 6-8 m/day drain pipe, or about 3 toilets/day"
			//                                                           -> 7 m/day drain, 3 fittings/day, 1.75 mates
			//    Midpoints of the published ranges; every quantity is editable in the
			//    composition, because output is the figure that changes from job to job.
			const string Fitter = "Pipefitter (PPR fusion and uPVC solvent welding)";
			const string Plumber = "Plumber (skilled)";
			const string Mate = "Plumber mate";
			const double RunPerDay = 25, JointsPerDay = 45, DrainPerDay = 7, FittingsPerDay = 3, Mates = 1.75;

			foreach (var (size, pipe) in new[]
			{
				("15mm", "PPR pressure pipe, PN10 to BS EN ISO 15874, 15mm"),
				("25mm", "PPR pressure pipe, PN10 to BS EN ISO 15874, 25mm"),
				("32mm", "PPR pressure pipe, PN10 to BS EN ISO 15874, 32mm"),
			})
			{
				MepWorkItems.Add(Build("Water Supply",
					$"{size} PPR pressure pipe, PN10 to BS EN ISO 15874, heat-fusion jointed; fixed with clips", "m",
					new[] { (pipe, 1.0, "m") },
					new[] { (Fitter, Math.Round(1 / RunPerDay, 4), "day"), (Mate, Math.Round(1 / RunPerDay, 4), "day") }));
			}

			MepWorkItems.Add(Build("Water Supply",
				"15mm isolating valve, jointed to PPR pipework", "No.",
				new[] { ("Isolating valve, 15mm", 1.0, "No.") },
				new[] { (Fitter, Math.Round(2 / JointsPerDay, 4), "day"), (Mate, Math.Round(2 / JointsPerDay, 4), "day") }));

			MepWorkItems.Add(Build("Water Supply",
				"25mm chromium plated stop valve, jointed to PPR pipework", "No.",
				new[] { ("Stop valve, 25mm, chromium plated", 1.0, "No.") },
				new[] { (Fitter, Math.Round(2 / JointsPerDay, 4), "day"), (Mate, Math.Round(2 / JointsPerDay, 4), "day") }));

			MepWorkItems.Add(Build("Soil & Waste",
				"100mm uPVC soil, waste and vent pipe to BS 4514, solvent welded; fixed with brackets", "m",
				new[] { ("uPVC soil, waste and vent pipe to BS 4514, 100mm", 1.0, "m") },
				new[] { (Plumber, Math.Round(1 / DrainPerDay, 4), "day"), (Mate, Math.Round(Mates / DrainPerDay, 4), "day") }));

			MepWorkItems.Add(Build("Soil & Waste",
				"38mm uPVC liquid waste and vent pipe, solvent welded; fixed with clips", "m",
				new[] { ("uPVC liquid waste and vent pipe, 38mm", 1.0, "m") },
				new[] { (Plumber, Math.Round(1 / DrainPerDay, 4), "day"), (Mate, Math.Round(Mates / DrainPerDay, 4), "day") }));

			MepWorkItems.Add(Build("Rainwater",
				"75mm uPVC rigid rainwater downpipe, fixed to walls with sockets", "m",
				new[] { ("uPVC rigid rainwater downpipe, 75mm", 1.0, "m") },
				new[] { (Plumber, Math.Round(1 / DrainPerDay, 4), "day"), (Mate, Math.Round(Mates / DrainPerDay, 4), "day") }));

			MepWorkItems.Add(Build("Sanitary",
				"Shower and shower tray with all accessories; fixed and connected", "No.",
				new[] { ("Shower and shower tray, including all accessories", 1.0, "No.") },
				new[] { (Plumber, Math.Round(1 / FittingsPerDay, 4), "day"), (Mate, Math.Round(Mates / FittingsPerDay, 4), "day") }));

			// ── Fire protection
			MepWorkItems.Add(Compose("Fire Protection",
				"Smoke/heat detector complete with base and connection", "No.",
				("Smoke/heat detector, complete with base and connection", 1, "No.")));

			MepWorkItems.Add(Compose("Fire Protection",
				"Fire alarm sounder/bell; installed and connected", "No.",
				("Fire alarm sounder/bell", 1, "No.")));

			MepWorkItems.Add(Compose("Fire Protection",
				"8 zone fire alarm control panel with battery back-up; installed and commissioned", "No.",
				("Fire alarm control panel, 8 zone, with battery back-up", 1, "No.")));

			MepWorkItems.Add(Compose("Fire Protection",
				"Fire hydrant/landing valve with hose reel, nozzle and cabinet", "No.",
				("Fire hydrant/landing valve with hose reel, nozzle and cabinet", 1, "No.")));

			MepWorkItems.Add(Compose("Fire Protection",
				"Fire extinguisher, dry powder/CO2, with bracket and signage", "No.",
				("Fire extinguisher, dry powder/CO2, with bracket and signage", 1, "No.")));

			// ── Security
			MepWorkItems.Add(Compose("Security",
				"CCTV camera including cabling to recorder; installed and commissioned", "No.",
				("CCTV camera, incl. cabling to recorder", 1, "No.")));

			MepWorkItems.Add(Compose("Security",
				"Electric fence energizer with remote and status indicator; installed and commissioned", "No.",
				("Electric fence energizer, Nemtek Wizord 2i or equal", 1, "No."),
				("Remote control device for fence energizer", 1, "No."),
				("Electric fence status indicator light", 1, "No.")));

			MepWorkItems.Add(Compose("Security",
				"Strobe light and 30W sounder/siren, external; installed and connected", "No.",
				("Strobe light and 30W sounder/siren, external", 1, "No.")));
		}
	}
}
