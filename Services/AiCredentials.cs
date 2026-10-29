using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ADLMRateGen.Helpers;

namespace ADLMRateGen.Services
{
	/// <summary>
	/// Where the AI service's bearer token comes from. Abstracted so the fallback
	/// rules below can be exercised without a signed-in user or a network.
	/// </summary>
	internal interface IAiCredentialSource
	{
		/// <summary>ADLM_AI_TOKEN exactly as found on this machine, or null.</summary>
		string? DevelopmentOverride { get; }

		/// <summary>The licence token the user signed in with, or null.</summary>
		string? LicenceToken { get; }

		/// <summary>
		/// Refreshes the ADLM Cloud session the way every other cloud call does and
		/// returns the licence token held afterwards. Never throws.
		/// </summary>
		Task<string?> RefreshLicenceAsync(CancellationToken ct);
	}

	/// <summary>The real source: the environment and the app's single AuthClient.</summary>
	internal sealed class AuthClientCredentialSource : IAiCredentialSource
	{
		public string? DevelopmentOverride => AppEnvironment.AiServiceTokenOverride;

		public string? LicenceToken
		{
			get
			{
				try { return AuthProvider.Instance.Client.GetCachedLicenseToken(); }
				catch { return null; }
			}
		}

		public Task<string?> RefreshLicenceAsync(CancellationToken ct) =>
			AuthProvider.Instance.Client.TryRefreshLicenseTokenAsync(ct);
	}

	internal enum AiTokenKind
	{
		/// <summary>No credential at all: the user has not completed a cloud sign-in.</summary>
		None,
		/// <summary>The signed-in licence token, the normal credential.</summary>
		Licence,
		/// <summary>ADLM_AI_TOKEN, a developer's hand-made token.</summary>
		DevelopmentOverride,
	}

	/// <summary>
	/// Decides which token the AI service is shown.
	///
	/// The signed-in licence is the normal credential. ADLM_AI_TOKEN exists so a
	/// developer can hit the service without a subscription, and it is honoured
	/// only in a Debug build. Before this, every build sent the override FIRST,
	/// so a dead token left in the owner's user environment silently replaced a
	/// perfectly good licence, every click answered "Sign in again", and signing
	/// in again could not help because the licence was never consulted.
	///
	/// Even in a Debug build the override is not trusted blindly: once the
	/// service has rejected it with 401 (<see cref="MarkOverrideRejected"/>) it
	/// is skipped for the rest of the process and the licence is used directly.
	/// </summary>
	internal sealed class AiTokenResolver
	{
		private readonly IAiCredentialSource _source;
		private volatile bool _overrideRejected;

		public AiTokenResolver(IAiCredentialSource source, bool? honourOverride = null)
		{
			_source = source ?? throw new ArgumentNullException(nameof(source));
			HonoursOverride = honourOverride ?? AppEnvironment.IsDevelopmentBuild;
		}

		/// <summary>True only in a development build: ADLM_AI_TOKEN may be sent at all.</summary>
		public bool HonoursOverride { get; }

		/// <summary>True once the service answered 401 for the override this session.</summary>
		public bool OverrideRejected => _overrideRejected;

		public (string Token, AiTokenKind Kind) Pick()
		{
			if (HonoursOverride && !_overrideRejected)
			{
				var dev = _source.DevelopmentOverride;
				if (!string.IsNullOrWhiteSpace(dev)) return (dev!.Trim(), AiTokenKind.DevelopmentOverride);
			}

			var licence = _source.LicenceToken;
			return string.IsNullOrWhiteSpace(licence)
				? (string.Empty, AiTokenKind.None)
				: (licence!.Trim(), AiTokenKind.Licence);
		}

		/// <summary>The token to send right now (empty when there is none).</summary>
		public string Current() => Pick().Token;

		public void MarkOverrideRejected()
		{
			if (_overrideRejected) return;
			_overrideRejected = true;
			Debug.WriteLine("[AiTokenResolver] ADLM_AI_TOKEN was rejected by the AI service; "
				+ "using the signed-in licence for the rest of this session.");
		}

		public Task<string?> RefreshLicenceAsync(CancellationToken ct)
		{
			try { return _source.RefreshLicenceAsync(ct); }
			catch { return Task.FromResult<string?>(null); }
		}
	}

	/// <summary>What the last AI request's authentication came to, for the message shown.</summary>
	internal enum AiAuthOutcome
	{
		/// <summary>Accepted, or failed for a reason other than authentication.</summary>
		None,
		/// <summary>403: the account has no AI add-on. Not a token problem.</summary>
		Forbidden,
		/// <summary>The override was rejected and the licence was accepted in its place.</summary>
		OverrideRejectedLicenceUsed,
		/// <summary>The override was rejected and so was the licence (after a refresh).</summary>
		OverrideRejectedLicenceRejected,
		/// <summary>The licence (or an absent one) was rejected, and a refresh did not help.</summary>
		LicenceRejected,
	}

	/// <summary>
	/// Sits between the AdlmAi SDK and the network and applies the token rules
	/// per request, so every SDK call gets them without the SDK knowing:
	///
	///  1. Send with the resolver's current pick.
	///  2. On 401 for the development override: remember it is dead, re-send once
	///     with the licence.
	///  3. On 401 for the licence (or when there was no token): refresh the ADLM
	///     Cloud session once and re-send if that produced a different licence.
	///  4. 403 is never retried; it is a subscription answer, not a token one.
	///
	/// The SDK maps both 401 and 403 to one status with a generic message, so
	/// <see cref="LastOutcome"/> is how the caller tells them apart and says
	/// something specific.
	/// </summary>
	internal sealed class AiAuthRetryHandler : DelegatingHandler
	{
		private readonly AiTokenResolver _resolver;

		public AiAuthRetryHandler(AiTokenResolver resolver, HttpMessageHandler inner) : base(inner)
		{
			_resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
		}

		/// <summary>Outcome of the most recent request through this handler.</summary>
		public AiAuthOutcome LastOutcome { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			var (token, kind) = _resolver.Pick();
			ApplyBearer(request, token);
			var response = await base.SendAsync(request, ct).ConfigureAwait(false);
			var overrideRejected = false;

			if (response.StatusCode == HttpStatusCode.Unauthorized && kind == AiTokenKind.DevelopmentOverride)
			{
				_resolver.MarkOverrideRejected();
				overrideRejected = true;
				(token, kind) = _resolver.Pick();
				if (kind != AiTokenKind.None)
				{
					response.Dispose();
					var retry = await CloneAsync(request).ConfigureAwait(false);
					ApplyBearer(retry, token);
					response = await base.SendAsync(retry, ct).ConfigureAwait(false);
				}
			}

			if (response.StatusCode == HttpStatusCode.Unauthorized && kind != AiTokenKind.DevelopmentOverride)
			{
				var refreshed = await _resolver.RefreshLicenceAsync(ct).ConfigureAwait(false);
				if (!string.IsNullOrWhiteSpace(refreshed) && !string.Equals(refreshed!.Trim(), token, StringComparison.Ordinal))
				{
					response.Dispose();
					var retry = await CloneAsync(request).ConfigureAwait(false);
					ApplyBearer(retry, refreshed.Trim());
					response = await base.SendAsync(retry, ct).ConfigureAwait(false);
				}
			}

			LastOutcome = response.StatusCode switch
			{
				HttpStatusCode.Forbidden => AiAuthOutcome.Forbidden,
				HttpStatusCode.Unauthorized => overrideRejected
					? AiAuthOutcome.OverrideRejectedLicenceRejected
					: AiAuthOutcome.LicenceRejected,
				_ => overrideRejected ? AiAuthOutcome.OverrideRejectedLicenceUsed : AiAuthOutcome.None,
			};
			return response;
		}

		private static void ApplyBearer(HttpRequestMessage request, string token)
		{
			request.Headers.Remove("Authorization");
			if (!string.IsNullOrEmpty(token))
				request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
		}

		/// <summary>An HttpRequestMessage can be sent once, so a retry needs a copy.</summary>
		private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
		{
			var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
			foreach (var header in request.Headers)
				clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

			if (request.Content != null)
			{
				var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
				var content = new ByteArrayContent(bytes);
				foreach (var header in request.Content.Headers)
					content.Headers.TryAddWithoutValidation(header.Key, header.Value);
				clone.Content = content;
			}

			return clone;
		}
	}

	/// <summary>
	/// The sentences Rate Gen shows for an authentication failure, in place of the
	/// SDK's one-size "Sign in again to use AI features." (which blamed the user
	/// for a token they never set).
	/// </summary>
	internal static class AiAuthMessages
	{
		public const string OverrideRejectedLicenceUsed =
			"A development AI token on this PC was rejected; your licence was used instead.";

		public const string SignInAgain = "Sign out and sign in again to use AI features.";

		/// <summary>
		/// Message for a failed request. 403 keeps the SDK's subscription wording;
		/// every 401 becomes the sign-in sentence; anything else passes through.
		/// </summary>
		public static string? ForFailure(AiAuthOutcome outcome, string? sdkMessage) => outcome switch
		{
			AiAuthOutcome.OverrideRejectedLicenceRejected => SignInAgain,
			AiAuthOutcome.LicenceRejected => SignInAgain,
			_ => sdkMessage,
		};

		/// <summary>Note to show beside a successful result, or null.</summary>
		public static string? ForSuccess(AiAuthOutcome outcome) =>
			outcome == AiAuthOutcome.OverrideRejectedLicenceUsed ? OverrideRejectedLicenceUsed : null;
	}
}
