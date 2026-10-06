using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdlmAi;
using ADLMRateGen.Helpers;
using ADLMRateGen.ViewModel.CustomRate;

namespace ADLMRateGen.Services
{
	/// <summary>
	/// Bridge between the ADLM AI Service and RateGen's CustomRate model.
	/// Given a plain-language work-item description it returns a ready
	/// CustomRate; every line is tagged [AI] unless it was priced from the
	/// RateGen library on the server.
	///
	/// Availability rules:
	///  - On by default: the endpoint falls back to
	///    AppEnvironment.DefaultAiServiceUrl, so an ordinary install has AI. It
	///    is hidden only when ADLM_AI_URL (or ADLM_RATEGEN_AI_URL) is set to
	///    "off" — the deliberate opt-out.
	///  - Auth uses the signed-in user's cached licence token (which carries
	///    the `ai` add-on entitlement); ADLM_AI_TOKEN overrides for dev/test.
	///  - Never throws for AI availability problems — check
	///    <see cref="AiRateResult.Status"/> and show <see cref="AiRateResult.Message"/>.
	/// </summary>
	public sealed class AiRateService
	{
		private static readonly Lazy<AiRateService> _instance = new(() => new AiRateService());
		public static AiRateService Instance => _instance.Value;

		private readonly AdlmAiClient? _client;

		private AiRateService()
		{
			var url = AppEnvironment.AiServiceUrl;
			if (!string.IsNullOrWhiteSpace(url))
			{
				_client = new AdlmAiClient(new AdlmAiOptions
				{
					BaseUrl = url!,
					TokenProvider = ResolveToken,
					Product = AppEnvironment.ProductKey, // "rategen"
				});
			}
		}

		/// <summary>The AI service address (null when ADLM_AI_URL is "off"), for callers that post to it directly.</summary>
		internal static string? ServiceUrl => AppEnvironment.AiServiceUrl;

		/// <summary>The token the AI service accepts: ADLM_AI_TOKEN, else the signed-in licence token.</summary>
		internal static string CurrentToken() => ResolveToken();

		private static string ResolveToken()
		{
			var overrideTok = AppEnvironment.AiServiceTokenOverride;
			if (!string.IsNullOrWhiteSpace(overrideTok)) return overrideTok!;
			try
			{
				return AuthProvider.Instance.Client.GetCachedLicenseToken() ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}

		/// <summary>True unless AI has been switched off (controls UI visibility).</summary>
		public bool IsConfigured => _client != null;

		/// <summary>
		/// How many of this machine's own library rows travel with a request.
		/// The service caps at 300; staying under it means the cap never
		/// silently decides which of the user's items the model gets told about.
		/// </summary>
		private const int LibraryContextLimit = 250;

		/// <summary>
		/// The library names sent with a build request, so the model can name a
		/// component the way this machine already holds it.
		///
		/// Sends the user's OWN rows first — the "Custom Rate" category, which is
		/// what Harvest stamps on anything folded in from a saved rate. Those are
		/// exactly the rows the service cannot know about: it grounds on ADLM's
		/// master price lists, so a master row spends prompt budget telling it
		/// what it already has. Remaining slots go to the rest of the library, so
		/// a smaller library still sends everything.
		///
		/// Names and units only — no prices leave the machine. The caller
		/// re-prices every matched line locally regardless, so a price would buy
		/// nothing and a user's price list is their commercial position.
		/// </summary>
		private static List<LibraryItemRef> CollectLibraryContext()
		{
			try
			{
				var items = new List<LibraryItemRef>(LibraryContextLimit);

				void TakeMaterials(bool ownRows)
				{
					foreach (var m in MaterialLibraryService.GetAllMaterials())
					{
						if (items.Count >= LibraryContextLimit) return;
						if (string.IsNullOrWhiteSpace(m.MaterialName)) continue;
						if (IsOwnRow(m.MaterialCategory) != ownRows) continue;
						items.Add(new LibraryItemRef
						{
							Kind = "material", Name = m.MaterialName, Unit = m.MaterialUnit,
						});
					}
				}

				void TakeLabours(bool ownRows)
				{
					foreach (var l in LabourLibraryService.GetAllLabours())
					{
						if (items.Count >= LibraryContextLimit) return;
						if (string.IsNullOrWhiteSpace(l.LabourName)) continue;
						if (IsOwnRow(l.LabourCategory) != ownRows) continue;
						items.Add(new LibraryItemRef
						{
							Kind = "labour", Name = l.LabourName, Unit = l.LabourUnit,
						});
					}
				}

				TakeMaterials(ownRows: true);
				TakeLabours(ownRows: true);
				TakeMaterials(ownRows: false);
				TakeLabours(ownRows: false);

				return items;
			}
			catch (Exception ex)
			{
				// Context is a naming hint, not part of the answer. A library
				// that will not enumerate must not cost the user their build-up.
				Debug.WriteLine($"[AiRateService] library context unavailable: {ex}");
				return new List<LibraryItemRef>();
			}
		}

		private static bool IsOwnRow(string? category) =>
			string.Equals(category, RateLineLibrary.HarvestedCategory, StringComparison.OrdinalIgnoreCase);

		public async Task<AiRateResult> BuildRateAsync(
			string description,
			string? unit = null,
			string? zone = null,
			IProgress<string>? progress = null,
			CancellationToken ct = default)
		{
			if (_client == null)
			{
				return AiRateResult.Fail(AiStatus.Unavailable,
					"AI is switched off on this machine (ADLM_AI_URL is set to \"off\"). "
					+ "Clear that variable to use it.");
			}
			if (string.IsNullOrWhiteSpace(description))
			{
				return AiRateResult.Fail(AiStatus.BadRequest, "Describe the work item first.");
			}

			var res = await _client
				.RateBuildupAsync(description, zone, unit, CollectLibraryContext(), progress, ct)
				.ConfigureAwait(false);

			if (!res.IsSuccess || res.Value == null)
			{
				return AiRateResult.Fail(res.Status, res.Message ?? "AI request failed.");
			}

			return new AiRateResult
			{
				Status = res.Status,
				Message = res.Message,
				Disclaimer = res.Disclaimer,
				Confidence = res.Audit?.Confidence,
				Model = res.Audit?.Model,
				Warnings = res.Value.Warnings?.Where(w => !string.IsNullOrWhiteSpace(w)).ToList()
						   ?? new List<string>(),
				Rate = MapToCustomRate(description, res.Value),
			};
		}

		/// <summary>
		/// Maps the AI build-up onto RateGen's CustomRate. Plant lines go into
		/// MaterialItems (RateGen has no plant category), tagged "(plant)".
		///
		/// Pricing rule: the local price library is the source of truth. When a
		/// component matches a library entry the line is stored under the
		/// library's own name, which makes RateEntryItem.ResolveUnitPrice() pull
		/// that entry's price and unit — the AI's figure is discarded. Only
		/// components the library has never heard of keep the AI price, and
		/// those are tagged "[AI]" so the QS can see what still needs review.
		/// </summary>
		private static CustomRate MapToCustomRate(string description, RateBuildup build)
		{
			var rate = new CustomRate
			{
				Title = Truncate(description, 60),
				Description = string.IsNullOrWhiteSpace(build.Notes)
					? description
					: $"{description} — {build.Notes}",
				OverheadPercent = (decimal)build.OverheadPercent,
				ProfitPercent = (decimal)build.ProfitPercent,
			};

			foreach (var c in build.Components ?? Enumerable.Empty<RateComponent>())
			{
				var isLabour = string.Equals(c.Kind, "labour", StringComparison.OrdinalIgnoreCase);
				var plantTag = string.Equals(c.Kind, "plant", StringComparison.OrdinalIgnoreCase)
					? " (plant)" : "";

				var item = new RateEntryItem
				{
					RateType = isLabour ? RateItemType.Labour : RateItemType.Material,
				};

				var lookupName = RateLineLibrary.CleanName(c.Name);
				var libraryName = isLabour
					? LabourLibraryService.FindByName(lookupName)?.LabourName
					: MaterialLibraryService.FindByName(lookupName)?.MaterialName;

				if (!string.IsNullOrWhiteSpace(libraryName))
				{
					// Assigning Description resolves price + unit from the library.
					item.Description = libraryName;
					item.Quantity = (decimal)c.Quantity;
					if (string.IsNullOrWhiteSpace(item.Unit))
						item.Unit = c.Unit;
				}
				else
				{
					item.Description = $"{c.Name}{plantTag} [AI]";
					item.Quantity = (decimal)c.Quantity;
					item.Unit = c.Unit;
					// Last — overrides the 0 that ResolveUnitPrice just wrote.
					item.UnitPrice = UnitPriceOf(c);
				}

				if (isLabour) rate.LabourItems.Add(item);
				else rate.MaterialItems.Add(item);
			}

			return rate;
		}

		/// <summary>
		/// Unit price for a component, falling back to totalNgn / quantity.
		///
		/// The service sends both unitPriceNgn and totalNgn. A build-up has been
		/// seen arriving with every unitPriceNgn at 0 while the rest of the line
		/// (name, quantity, unit) was correct, which saved a rate where every
		/// price read 0.00. Deriving from the line total recovers the figure
		/// whenever the total survived.
		/// </summary>
		private static decimal UnitPriceOf(RateComponent c)
		{
			var unitPrice = (decimal)c.UnitPriceNgn;
			if (unitPrice > 0m) return unitPrice;

			var quantity = (decimal)c.Quantity;
			var total = (decimal)c.TotalNgn;
			if (total > 0m && quantity > 0m) return total / quantity;

			return 0m;
		}

		/// <summary>
		/// True when the build-up carries no usable pricing at all, so the caller
		/// can warn instead of presenting an all-zero rate as ready to save.
		/// </summary>
		public static bool IsUnpriced(CustomRate rate) =>
			rate != null &&
			rate.MaterialItems.Concat(rate.LabourItems).All(i => i.UnitPrice <= 0m);

		private static string Truncate(string s, int max) =>
			s.Length <= max ? s : s.Substring(0, max - 1) + "…";
	}

	/// <summary>Outcome of an AI rate build — status-first, never throws.</summary>
	public sealed class AiRateResult
	{
		public AiStatus Status { get; set; }
		public string? Message { get; set; }
		public string? Disclaimer { get; set; }
		public double? Confidence { get; set; }
		public string? Model { get; set; }

		/// <summary>
		/// Server-side check failures the build-up was returned with anyway.
		/// Empty when it passed. See AdlmAi RateBuildup.Warnings.
		/// </summary>
		public List<string> Warnings { get; set; } = new();

		public CustomRate? Rate { get; set; }
		public bool IsSuccess => Status == AiStatus.Success || Status == AiStatus.CachedFallback;

		public static AiRateResult Fail(AiStatus status, string message) =>
			new() { Status = status, Message = message };
	}
}
