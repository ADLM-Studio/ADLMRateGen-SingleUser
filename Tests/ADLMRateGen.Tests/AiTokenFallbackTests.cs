using System.Net.Http;
using System.Net;
using System.Text;
using AdlmAi;
using ADLMRateGen.Helpers;
using ADLMRateGen.Services;
using Xunit;

namespace ADLMRateGen.Tests;

/// <summary>
/// Which token "Build with AI" shows the AI service, and what the user is told
/// when it is refused.
///
/// On 29 Oct 2026 the owner's PC answered every click with the SDK's
/// "Sign in again to use AI features." A stale ADLM_AI_TOKEN, a developer's
/// override, sat in the user environment and was sent BEFORE the signed-in
/// licence, so a dead token replaced a good one and signing in again could not
/// help. These tests pin the rules that make that impossible: an installed
/// build never sends the override, a Debug build drops it the moment it is
/// rejected, a rejected licence is retried after a session refresh, and the
/// message names the cause. No network: the "service" is a fake handler.
/// </summary>
public class AiTokenFallbackTests
{
    private const string Dev = "dev-token";
    private const string Licence = "licence-token";
    private const string Url = "https://ai.test.invalid";

    private const string OkBody =
        "{\"result\":{\"rateNgn\":1000,\"components\":[],\"notes\":\"\"},\"audit\":{\"model\":\"test\"}}";

    [Fact]
    public void ReleaseBuild_IgnoresTheOverride_AndSendsTheLicence()
    {
        var creds = new FakeCredentials { DevelopmentOverride = Dev, LicenceToken = Licence };
        var resolver = new AiTokenResolver(creds, honourOverride: false);

        Assert.Equal(Licence, resolver.Current());
        Assert.Equal(AiTokenKind.Licence, resolver.Pick().Kind);
    }

    [Fact]
    public void DevelopmentBuild_SendsTheOverrideFirst()
    {
        var creds = new FakeCredentials { DevelopmentOverride = Dev, LicenceToken = Licence };
        var resolver = new AiTokenResolver(creds, honourOverride: true);

        Assert.Equal(Dev, resolver.Current());

        resolver.MarkOverrideRejected();
        Assert.Equal(Licence, resolver.Current());
        Assert.True(resolver.OverrideRejected);
    }

    [Fact]
    public void NoLicenceAndNoOverride_SendsNothing()
    {
        var resolver = new AiTokenResolver(new FakeCredentials(), honourOverride: true);

        Assert.Equal(string.Empty, resolver.Current());
        Assert.Equal(AiTokenKind.None, resolver.Pick().Kind);
    }

    [Fact]
    public void IsDevelopmentBuild_FollowsTheCompilation()
    {
#if DEBUG
        Assert.True(AppEnvironment.IsDevelopmentBuild);
#else
        Assert.False(AppEnvironment.IsDevelopmentBuild);
#endif
    }

    [Fact]
    public async Task ReleaseBuild_NeverShowsTheServiceTheOverride()
    {
        var creds = new FakeCredentials { DevelopmentOverride = Dev, LicenceToken = Licence };
        var service = new FakeAiService { Answer = _ => HttpStatusCode.OK };
        var (client, auth) = Build(creds, service, honourOverride: false);

        var res = await client.RateBuildupAsync("225mm blockwork", null, null, null, null);

        Assert.Equal(AiStatus.Success, res.Status);
        Assert.Equal(new[] { Licence }, service.TokensSeen);
        Assert.Equal(AiAuthOutcome.None, auth.LastOutcome);
    }

    [Fact]
    public async Task RejectedOverride_FallsBackToTheLicence_AndStaysThere()
    {
        var creds = new FakeCredentials { DevelopmentOverride = Dev, LicenceToken = Licence };
        var service = new FakeAiService
        {
            Answer = token => token == Dev ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
        };
        var (client, auth) = Build(creds, service, honourOverride: true);

        var first = await client.RateBuildupAsync("225mm blockwork", null, null, null, null);

        Assert.Equal(AiStatus.Success, first.Status);
        Assert.Equal(new[] { Dev, Licence }, service.TokensSeen);
        Assert.Equal(AiAuthOutcome.OverrideRejectedLicenceUsed, auth.LastOutcome);
        Assert.Equal(AiAuthMessages.OverrideRejectedLicenceUsed, AiAuthMessages.ForSuccess(auth.LastOutcome));

        // The dead override is remembered: the next call goes straight to the licence.
        service.TokensSeen.Clear();
        var second = await client.RateBuildupAsync("150mm concrete bed", null, null, null, null);

        Assert.Equal(AiStatus.Success, second.Status);
        Assert.Equal(new[] { Licence }, service.TokensSeen);
        Assert.Equal(AiAuthOutcome.None, auth.LastOutcome);
        Assert.Null(AiAuthMessages.ForSuccess(auth.LastOutcome));
    }

    [Fact]
    public async Task RejectedOverride_AndRejectedLicence_SaysSignOutAndIn()
    {
        var creds = new FakeCredentials { DevelopmentOverride = Dev, LicenceToken = Licence };
        var service = new FakeAiService { Answer = _ => HttpStatusCode.Unauthorized };
        var (client, auth) = Build(creds, service, honourOverride: true);

        var res = await client.RateBuildupAsync("225mm blockwork", null, null, null, null);

        Assert.Equal(AiStatus.NotEntitled, res.Status);
        // Override, then licence; the refresh returned the same licence, so no third try.
        Assert.Equal(new[] { Dev, Licence }, service.TokensSeen);
        Assert.Equal(1, creds.Refreshes);
        Assert.Equal(AiAuthOutcome.OverrideRejectedLicenceRejected, auth.LastOutcome);
        Assert.Equal(AiAuthMessages.SignInAgain, AiAuthMessages.ForFailure(auth.LastOutcome, res.Message));
    }

    [Fact]
    public async Task RejectedLicence_IsRetriedWithTheLicenceARefreshProduces()
    {
        var creds = new FakeCredentials { LicenceToken = "old-licence", RefreshedLicence = "new-licence" };
        var service = new FakeAiService
        {
            Answer = token => token == "new-licence" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized,
        };
        var (client, auth) = Build(creds, service, honourOverride: false);

        var res = await client.RateBuildupAsync("225mm blockwork", null, null, null, null);

        Assert.Equal(AiStatus.Success, res.Status);
        Assert.Equal(new[] { "old-licence", "new-licence" }, service.TokensSeen);
        Assert.Equal(1, creds.Refreshes);
        Assert.Equal(AiAuthOutcome.None, auth.LastOutcome);
    }

    [Fact]
    public async Task RejectedLicence_WithNothingNewerToTry_SaysSignOutAndIn()
    {
        var creds = new FakeCredentials { LicenceToken = Licence };
        var service = new FakeAiService { Answer = _ => HttpStatusCode.Unauthorized };
        var (client, auth) = Build(creds, service, honourOverride: false);

        var res = await client.RateBuildupAsync("225mm blockwork", null, null, null, null);

        Assert.Equal(AiStatus.NotEntitled, res.Status);
        Assert.Equal(new[] { Licence }, service.TokensSeen);
        Assert.Equal(1, creds.Refreshes);
        Assert.Equal(AiAuthOutcome.LicenceRejected, auth.LastOutcome);
        Assert.Equal(AiAuthMessages.SignInAgain, AiAuthMessages.ForFailure(auth.LastOutcome, res.Message));
    }

    [Fact]
    public async Task Forbidden_IsNotRetried_AndKeepsTheSubscriptionWording()
    {
        var creds = new FakeCredentials { DevelopmentOverride = Dev, LicenceToken = Licence };
        var service = new FakeAiService { Answer = _ => HttpStatusCode.Forbidden };
        var (client, auth) = Build(creds, service, honourOverride: true);

        var res = await client.RateBuildupAsync("225mm blockwork", null, null, null, null);

        Assert.Equal(AiStatus.NotEntitled, res.Status);
        Assert.Single(service.TokensSeen);
        Assert.Equal(0, creds.Refreshes);
        Assert.Equal(AiAuthOutcome.Forbidden, auth.LastOutcome);

        var shown = AiAuthMessages.ForFailure(auth.LastOutcome, res.Message);
        Assert.Equal(res.Message, shown);
        Assert.Contains("add-on", shown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sign in", shown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildWithAi_AfterARejectedOverride_SucceedsAndCarriesTheNotice()
    {
        var creds = new FakeCredentials { DevelopmentOverride = Dev, LicenceToken = Licence };
        var service = new FakeAiService
        {
            Answer = token => token == Dev ? HttpStatusCode.Unauthorized : HttpStatusCode.OK,
        };
        var rates = new AiRateService(Url, creds, service, honourOverride: true);

        var result = await rates.BuildRateAsync("Build a rate for 225mm blockwork");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Rate);
        Assert.Equal(AiAuthMessages.OverrideRejectedLicenceUsed, result.AuthNotice);
    }

    [Fact]
    public async Task BuildWithAi_WithARejectedLicence_SaysSignOutAndIn_NotTheSdkSentence()
    {
        var creds = new FakeCredentials { LicenceToken = Licence };
        var service = new FakeAiService { Answer = _ => HttpStatusCode.Unauthorized };
        var rates = new AiRateService(Url, creds, service, honourOverride: false);

        var result = await rates.BuildRateAsync("Build a rate for 225mm blockwork");

        Assert.False(result.IsSuccess);
        Assert.Equal(AiStatus.NotEntitled, result.Status);
        Assert.Equal(AiAuthMessages.SignInAgain, result.Message);
        Assert.DoesNotContain("Sign in again to use AI features.", result.Message);
    }

    [Fact]
    public async Task BuildWithAi_Forbidden_KeepsTheSubscriptionWording()
    {
        var creds = new FakeCredentials { LicenceToken = Licence };
        var service = new FakeAiService { Answer = _ => HttpStatusCode.Forbidden };
        var rates = new AiRateService(Url, creds, service, honourOverride: false);

        var result = await rates.BuildRateAsync("Build a rate for 225mm blockwork");

        Assert.Equal(AiStatus.NotEntitled, result.Status);
        Assert.Contains("add-on", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static (AdlmAiClient Client, AiAuthRetryHandler Auth) Build(
        FakeCredentials creds, FakeAiService service, bool honourOverride)
    {
        var resolver = new AiTokenResolver(creds, honourOverride);
        var auth = new AiAuthRetryHandler(resolver, service);
        var client = new AdlmAiClient(new AdlmAiOptions
        {
            BaseUrl = Url,
            TokenProvider = resolver.Current,
            Product = "rategen",
            EnableLocalCache = false,
        }, new HttpClient(auth));
        return (client, auth);
    }

    private sealed class FakeCredentials : IAiCredentialSource
    {
        public string? DevelopmentOverride { get; set; }
        public string? LicenceToken { get; set; }

        /// <summary>What a refresh turns the licence into; null leaves it as it is.</summary>
        public string? RefreshedLicence { get; set; }

        public int Refreshes { get; private set; }

        public Task<string?> RefreshLicenceAsync(CancellationToken ct)
        {
            Refreshes++;
            if (RefreshedLicence != null) LicenceToken = RefreshedLicence;
            return Task.FromResult(LicenceToken);
        }
    }

    /// <summary>Stands in for the AI service: answers by bearer token, records what it saw.</summary>
    private sealed class FakeAiService : HttpMessageHandler
    {
        public List<string?> TokensSeen { get; } = new();
        public Func<string?, HttpStatusCode> Answer { get; set; } = _ => HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string? token = null;
            if (request.Headers.TryGetValues("Authorization", out var values))
            {
                var raw = values.FirstOrDefault() ?? string.Empty;
                token = raw.StartsWith("Bearer ", StringComparison.Ordinal) ? raw.Substring(7) : raw;
            }
            TokensSeen.Add(token);

            var status = Answer(token);
            var body = status == HttpStatusCode.OK ? OkBody : "{\"error\":\"refused\"}";
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

/// <summary>
/// The start-up cleanup for a leftover ADLM_AI_TOKEN. Runs on a throwaway
/// variable name so no real one on this machine is touched.
/// </summary>
[Collection(TestCollections.ProcessEnvironment)]
public class AiTokenOverrideCleanupTests
{
    [Fact]
    public void InstalledBuild_RemovesAUserScopeToken_AndTheProcessCopy()
    {
        var name = "ADLM_AI_TOKEN_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(name, "stale", EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, "stale");

            Assert.True(AppEnvironment.RemoveStaleAiTokenOverride(name, developmentBuild: false));

            Assert.Null(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));
            Assert.Null(Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void DevelopmentBuild_KeepsTheToken()
    {
        var name = "ADLM_AI_TOKEN_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(name, "mine", EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, "mine");

            Assert.False(AppEnvironment.RemoveStaleAiTokenOverride(name, developmentBuild: true));

            Assert.Equal("mine", Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));
            Assert.Equal("mine", Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void NothingSet_IsReportedAsNothingRemoved()
    {
        var name = "ADLM_AI_TOKEN_TEST_" + Guid.NewGuid().ToString("N");

        Assert.False(AppEnvironment.RemoveStaleAiTokenOverride(name, developmentBuild: false));
    }

    [Fact]
    public void TheRealVariableName_IsTheOneDevelopersSet()
    {
        Assert.Equal("ADLM_AI_TOKEN", AppEnvironment.AiTokenOverrideVariable);
    }
}
