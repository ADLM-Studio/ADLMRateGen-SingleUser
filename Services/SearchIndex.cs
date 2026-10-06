using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ADLMRateGen.ViewModel.Model;
using ADLMRateGen.ViewModel;

namespace ADLMRateGen.Services
{
	public class SearchIndex
	{
		private readonly List<SearchHit> _hits = new();

		public IEnumerable<SearchHit> Hits => _hits;

		public void Rebuild(MainViewModel vm)
		{
			_hits.Clear();

			/* ───────── materials ───────── */
			foreach (var m in vm.MaterialLibraryViewModel.MaterialLibrary)
				_hits.Add(new SearchHit(
					m.MaterialName,
					"Material",
					() => vm.SelectedMaterialLibraryViewCommand.Execute(null)));

			/* ───────── labours ───────── */
			foreach (var l in vm.LabourLibraryViewModel.LabourLibrary)
				_hits.Add(new SearchHit(
					l.LabourName,
					"Labour",
					() => vm.SelectedLabourLibraryViewCommand.Execute(null)));

			/* ───────── custom rates ───────── */
			foreach (var r in vm.CustomRateListViewModel.CustomRates)
				_hits.Add(new SearchHit(
					r.Title,
					"Custom Rate",
					() =>
					{
						vm.SelectedCustomRateInputViewCommand.Execute(null);
						vm.CustomRateListViewModel.SelectedRate = r;
					}));

			/* ───────── ground ‑ work ───────── */
			foreach (var g in vm.GroundWorkViewModel.GroundworkItems)
				_hits.Add(new SearchHit(
					g.Description,                       // or g.ItemName
					"Groundwork",
					() =>
					{
						vm.SelectedGroundworkViewCommand.Execute(null);
						vm.GroundWorkViewModel.SelectedDetail = g;   // if you expose one
					}));

			/* ───────── concrete ───────── */
			foreach (var c in vm.ConcreteViewModel.ConcreteWorkItems)
				_hits.Add(new SearchHit(
					c.Description,
					"Concrete",
					() =>
					{
						vm.SelectedConcreteWorkViewCommand.Execute(null);
						vm.ConcreteViewModel.SelectedDetail = c;
					}));

			/* ───────── block work ───────── */
			foreach (var b in vm.BlockworkViewModel.BlockworkItems)
				_hits.Add(new SearchHit(
					b.Description,
					"Block work",
					() =>
					{
						vm.SelectedBlockworkViewCommand.Execute(null);
						vm.BlockworkViewModel.SelectedDetail = b;
					}));

			/* ───────── MEP work ───────── */
			foreach (var m in vm.MepWorkViewModel.MepWorkItems)
				_hits.Add(new SearchHit(
					m.Description,
					ViewModel.MepWork.MepWorkViewModel.DisciplineOf(m.Section) + " services",
					() =>
					{
						vm.MepWorkViewModel.Discipline = ViewModel.MepWork.MepWorkViewModel.DisciplineOf(m.Section);
						vm.SelectedMepWorkViewCommand.Execute(null);
						vm.MepWorkViewModel.SelectedDetail = m;
					}));

			/* ───────── finishes ───────── */
			foreach (var f in vm.FinishesViewModel.FinishesItems)
				_hits.Add(new SearchHit(
					f.Description,
					"Finishes",
					() =>
					{
						vm.SelectedFinishesViewCommand.Execute(null);
						vm.FinishesViewModel.SelectedDetail = f;
					}));

			/* ───────── roof work ───────── */
			foreach (var r in vm.RoofWorkViewModel.RoofWorkItems)
				_hits.Add(new SearchHit(
					r.Description,
					"Roof work",
					() =>
					{
						vm.SelectedRoofworkViewCommand.Execute(null);
						vm.RoofWorkViewModel.SelectedDetail = r;
					}));

			/* ───────── windows / doors ───────── */
			foreach (var w in vm.WindowAndDoorViewModel.WindowAndDoorItems)
				_hits.Add(new SearchHit(
					w.Description,
					"Windows & Doors",
					() =>
					{
						vm.SelectedWindowAndDoorViewCommand.Execute(null);
						vm.WindowAndDoorViewModel.SelectedDetail = w;
					}));

			/* ───────── painting ───────── */
			foreach (var p in vm.PaintWorkViewModel.PaintWorkItems)
				_hits.Add(new SearchHit(
					p.Description,
					"Painting",
					() =>
					{
						vm.SelectedPaintworkViewCommand.Execute(null);
						vm.PaintWorkViewModel.SelectedDetail = p;
					}));

			/* ───────── steel ───────── */
			foreach (var s in vm.SteelWorkViewModel.SteelWorkItems)
				_hits.Add(new SearchHit(
					s.Description,
					"Steel work",
					() =>
					{
						vm.SelectedSteelworkViewCommand.Execute(null);
						vm.SteelWorkViewModel.SelectedDetail = s;
					}));
		}

	}

}
