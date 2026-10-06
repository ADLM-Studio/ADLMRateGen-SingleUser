using System.Text.Json.Serialization;

namespace ADLMRateGen.ViewModel.CustomRate
{
	public class CustomRate
	{
		public Guid Id { get; set; } = Guid.NewGuid();

		public string Title { get; set; }
		public string Description { get; set; }
		public List<RateEntryItem> MaterialItems { get; set; } = new List<RateEntryItem>();
		public List<RateEntryItem> LabourItems { get; set; } = new List<RateEntryItem>();
		public decimal OverheadPercent { get; set; }
		public decimal ProfitPercent { get; set; }
		public DateTime CreatedDate { get; set; } = DateTime.Now;

		/* ── cloud sync (Services/UserRatesCloudSync.cs) ── */

		/// <summary>The cloud id when it is not a Guid: a rate built on the website is keyed by a slug.</summary>
		public string? CloudId { get; set; }
		/// <summary>Unit, section and section label as the cloud holds them. The desktop has no field for these, so they are kept to be sent back unchanged.</summary>
		public string? CloudUnit { get; set; }
		public string? SectionKey { get; set; }
		public string? SectionLabel { get; set; }
		/// <summary>The cloud's updatedAt when this copy last matched it.</summary>
		public DateTime? CloudUpdatedAt { get; set; }
		/// <summary>The rate's content when it last matched the cloud. Differs from the current content once the user edits it here.</summary>
		public string? SyncedSignature { get; set; }

		[JsonIgnore]
		public decimal TotalMaterialCost => MaterialItems.Sum(item => item.TotalCost);
		[JsonIgnore]
		public decimal TotalLabourCost => LabourItems.Sum(item => item.TotalCost);
		[JsonIgnore]
		public decimal OverallTotal => TotalMaterialCost + TotalLabourCost;
		[JsonIgnore]
		public decimal GrandTotal => OverallTotal * (1 + (OverheadPercent + ProfitPercent) / 100);
	}
}
