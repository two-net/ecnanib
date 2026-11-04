using System.Net.Http;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Audit;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace Ecnanib.Tests.Credentials;

/// <summary>
/// AC-03, FR-KEY-09 and FR-LOG-05 through the real pages, with logging as loud as it goes. A key with no
/// marker can't be recognised by the masking, so keeping it out relies on nothing logging it.
/// </summary>
public sealed class CredentialLoggingTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CredentialApp.Cancellation;

    [Fact]
    public async Task Keys_and_secrets_stay_out_of_the_logs_and_the_audit_trail()
    {
        var clock = new TestClock();
        var binance = new FakeBinance(clock);
        var ai = new FakeAiProvider();
        var aiKey = CanaryCases.NewCanary();
        var refusedAiKey = CanaryCases.NewCanary();
        var rotatedAiKey = CanaryCases.NewCanary();
        var binanceKey = CanaryCases.NewCanary();
        var binanceSecret = CanaryCases.NewCanary();
        var withdrawingKey = CanaryCases.NewCanary();

        await using var app = new LoggedApp(
            postgres.NewConnectionString(),
            CredentialApp.Settings(AccountApp.Settings(new()
            {
                ["Logging:MinimumLevel"] = "Verbose",
                ["Logging:Overrides:Microsoft.AspNetCore"] = "Verbose",
                ["Logging:Overrides:Microsoft.EntityFrameworkCore.Database.Command"] = "Verbose",
                ["Logging:Overrides:System.Net.Http"] = "Verbose",
            })),
            services =>
            {
                ExchangeHarness.Use(services, binance, clock);
                AiHarness.Use(services, ai, new TestAdapter(CredentialApp.Provider), new TestAdapter("Second"));
            });

        ai.On(HttpMethod.Get, CredentialApp.ModelsPath, request =>
            request.Key == refusedAiKey
                ? FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid")
                : FakeAiProvider.Models(null, "model-a"));
        binance.On(HttpMethod.Get, CredentialApp.RestrictionsPath, request =>
            FakeBinance.Json(BinanceSamples.ApiRestrictions(withdrawals: request.ApiKey == withdrawingKey)));
        binance.On(HttpMethod.Get, CredentialApp.SpotAccountPath, _ => FakeBinance.Json(BinanceSamples.SpotAccount));
        binance.On(HttpMethod.Get, CredentialApp.FuturesAccountPath, _ => FakeBinance.Json(BinanceSamples.FuturesAccount));

        using var visitor = Visitor.To(app.App);
        var connectionString = app.App.ConnectionString();
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(connectionString, email, Cancellation);

        (await AddAiAsync(visitor, aiKey)).Dispose();
        (await AddAiAsync(visitor, refusedAiKey)).Dispose();
        (await AddBinanceAsync(visitor, withdrawingKey, binanceSecret)).Dispose();
        (await AddBinanceAsync(visitor, binanceKey, binanceSecret)).Dispose();

        var credentials = app.App.Services.GetRequiredService<Modules.Credentials.Contracts.ICredentials>();
        var listed = await credentials.ListAsync(userId, Cancellation);
        clock.Advance(TimeSpan.FromMinutes(1));

        foreach (var credential in listed)
        {
            await credentials.ValidateAsync(userId, credential.Id, Cancellation);
        }

        var aiCredential = listed.Single(credential => credential.Provider == CredentialApp.Provider);
        (await visitor.SubmitAsync($"/credentials/{aiCredential.Id}", "rotate-key", ("Input.ApiKey", rotatedAiKey))).Dispose();

        foreach (var credential in listed)
        {
            await credentials.DeleteAsync(userId, credential.Id, Cancellation);
        }

        await app.StopAsync();

        var text = app.FileText() + await app.TableTextAsync(Cancellation);

        Assert.Contains("Added credential", text, StringComparison.Ordinal);
        Assert.Contains("Refused a Binance", text, StringComparison.Ordinal);
        Assert.Contains("Rotated credential", text, StringComparison.Ordinal);
        Assert.Contains("Deleted credential", text, StringComparison.Ordinal);

        foreach (var canary in new[] { aiKey, refusedAiKey, rotatedAiKey, binanceKey, binanceSecret, withdrawingKey })
        {
            Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
            Assert.Equal(0, await AuditRows.CountHoldingAsync(connectionString, canary, Cancellation));
        }
    }

    private static Task<HttpResponseMessage> AddAiAsync(Visitor visitor, string key) =>
        visitor.SubmitAsync(
            "/credentials/add", "add-ai-key", ("Input.Provider", CredentialApp.Provider), ("Input.Label", "Personal"), ("Input.ApiKey", key));

    private static Task<HttpResponseMessage> AddBinanceAsync(Visitor visitor, string key, string secret) =>
        visitor.SubmitAsync(
            "/credentials/add",
            "add-binance-key",
            ("Input.Environment", "Live"),
            ("Input.Label", "Main account"),
            ("Input.ApiKey", key),
            ("Input.Secret", secret));
}
