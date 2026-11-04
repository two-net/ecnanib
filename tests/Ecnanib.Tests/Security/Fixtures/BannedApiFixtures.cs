// Deliberately broken code for BannedApiGuardTests. None of it ever runs. Each class in Banned hides
// one use of a banned API in a different way. Everything outside Banned has to pass.

using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Security.Fixtures.Banned
{
    public sealed class Row
    {
        public int Id { get; set; }
    }

    public static class MarkupStringCast
    {
        public static object Render(string text) => (MarkupString)text;
    }

    public static class MarkupStringConstructed
    {
        public static object Render(string text) => new MarkupString(text);
    }

    public static class MarkupStringInLambda
    {
        public static Func<string, object> Render() => text => (MarkupString)text;
    }

    public static class FromSqlRawCall
    {
        public static IQueryable<Row> Read(DbContext context) => context.Set<Row>().FromSqlRaw("SELECT 1");
    }

    public static class ExecuteSqlRawCall
    {
        public static int Run(DbContext context) => context.Database.ExecuteSqlRaw("SELECT 1");
    }

    public static class ExecuteSqlRawAsyncCall
    {
        public static async Task<int> RunAsync(DbContext context) => await context.Database.ExecuteSqlRawAsync("SELECT 1");
    }

    public static class SqlQueryRawCall
    {
        public static IQueryable<int> Read(DbContext context) => context.Database.SqlQueryRaw<int>("SELECT 1");
    }

    public static class AntiforgeryTurnedOff
    {
        public static void Map(IEndpointRouteBuilder endpoints) => endpoints.MapPost("/", () => 1).DisableAntiforgery();
    }

    public static class HttpClientOutsideAdapters
    {
        public static HttpClient Create() => new();
    }

    public static class HttpClientInAsyncMethod
    {
        public static async Task<string> ReadAsync(HttpClient client, Uri url) => await client.GetStringAsync(url);
    }

    public static class HttpClientFactoryOutsideAdapters
    {
        public static HttpClient Create(IHttpClientFactory factory) => factory.CreateClient("any");
    }

    public static class HttpClientPassedToAnExtension
    {
        public static Task<int> ReadAsync(HttpClient client, Uri url) => client.GetFromJsonAsync<int>(url);
    }

    public static class SecretRevealedOutsideItsOwners
    {
        public static Task<string> ReadAsync(Platform.Contracts.ISecretStore secrets, Guid id) => secrets.RevealAsync(id);
    }

    public static class SecretStoredOutsideItsOwners
    {
        public static Guid Store(Platform.Contracts.ISecretStore secrets, string value) => secrets.Add(value);
    }

    public static class AddressesOutsideNotifications
    {
        public static Task<IReadOnlyList<string>> FindAsync(Modules.Identity.Contracts.IEmailRecipients recipients, Guid id) =>
            recipients.FindAsync([id]);
    }

    public static class MailKitClientOutsideEmail
    {
        public static IDisposable Create() => new MailKit.Net.Smtp.SmtpClient();
    }

    public static class FrameworkSmtpClient
    {
        public static IDisposable Create() => new System.Net.Mail.SmtpClient();
    }

    public static class OrdersOutsideExecution
    {
        public static Task PlaceAsync(Modules.ExchangeAdapter.Contracts.IExchangeTrading trading) => trading.PlaceOrderAsync("BTCUSDT");
    }

    public static class OrdersCancelledOutsideExecution
    {
        public static Task CancelAsync(Modules.ExchangeAdapter.Contracts.IExchangeTrading trading) => trading.CancelOrderAsync("BTCUSDT");
    }

    public static class CompletionsOutsideCouncil
    {
        public static Task<string> AskAsync(Modules.AiAdapters.Contracts.IAiCompletions completions) => completions.CompleteAsync("a prompt");
    }

    public static class AiKeyOutsideCredentials
    {
        public static object Build() => new Modules.AiAdapters.Contracts.AiKey("a platform key");
    }

    public static class ExchangeKeyOutsideCredentials
    {
        public static object Build() => new Modules.ExchangeAdapter.Contracts.ExchangeKey("a platform key");
    }

    public static class KeyOpenedByTheWebUi
    {
        public static Task<object> OpenAsync(Modules.Credentials.Contracts.ICredentialKeys keys) => keys.OpenAsync(Guid.Empty);
    }

    public static class EveryKeySyncedByTheWebUi
    {
        public static Task SyncAsync(Modules.Catalog.Contracts.ICatalogAdministration catalog) => catalog.SyncAllAsync();
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Platform.Contracts
{
    /// <summary>Stands in for the real contract, under the same path below the root.</summary>
    public interface ISecretStore
    {
        Guid Add(string value);

        Task<string> RevealAsync(Guid id);
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.Identity.Contracts
{
    /// <summary>Stands in for the real contract, under the same path below the root.</summary>
    public interface IEmailRecipients
    {
        Task<IReadOnlyList<string>> FindAsync(IReadOnlyCollection<Guid> userIds);
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.Notifications
{
    public static class TheEmailJob
    {
        public static Task<IReadOnlyList<string>> FindAsync(Identity.Contracts.IEmailRecipients recipients, Guid id) =>
            recipients.FindAsync([id]);
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.Credentials
{
    public static class AnOwner
    {
        public static Task<string> ReadAsync(Platform.Contracts.ISecretStore secrets, Guid id) => secrets.RevealAsync(id);
    }

    public static class TheKeyBuilder
    {
        public static object Build() => new AiAdapters.Contracts.AiKey("a user's key");
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.Credentials.Contracts
{
    /// <summary>Stands in for the real contract, under the same path below the root.</summary>
    public interface ICredentialKeys
    {
        Task<object> OpenAsync(Guid credentialId);
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.Catalog
{
    public static class TheCatalog
    {
        public static Task<object> OpenAsync(Credentials.Contracts.ICredentialKeys keys) => keys.OpenAsync(Guid.Empty);
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.Catalog.Contracts
{
    /// <summary>Stands in for the real contract, under the same path below the root.</summary>
    public interface ICatalogAdministration
    {
        Task SyncAllAsync();
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.Administration
{
    public static class TheAdministrator
    {
        public static Task SyncAsync(Catalog.Contracts.ICatalogAdministration catalog) => catalog.SyncAllAsync();
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Allowed
{
    public static class InterpolatedSql
    {
        public static Task<int> RunAsync(DbContext context) => context.Database.ExecuteSqlAsync($"SELECT 1");
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Platform.Email
{
    public static class TheEmailSender
    {
        public static IDisposable Create() => new MailKit.Net.Smtp.SmtpClient();
    }

    /// <summary>Banned even where MailKit's client is allowed.</summary>
    public static class FrameworkSmtpClientInEmail
    {
        public static IDisposable Create() => new System.Net.Mail.SmtpClient();
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.ExchangeAdapter.Contracts
{
    /// <summary>Stands in for the real contract, under the same path below the root.</summary>
    public interface IExchangeTrading
    {
        Task PlaceOrderAsync(string symbol);

        Task CancelOrderAsync(string symbol);
    }

    /// <summary>Stands in for the real type, under the same path below the root.</summary>
    public sealed class ExchangeKey(string apiKey)
    {
        public string ApiKey { get; } = apiKey;
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.Execution
{
    public static class TheExecutor
    {
        public static Task PlaceAsync(ExchangeAdapter.Contracts.IExchangeTrading trading) => trading.PlaceOrderAsync("BTCUSDT");
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.AiAdapters.Contracts
{
    /// <summary>Stands in for the real contract, under the same path below the root.</summary>
    public interface IAiCompletions
    {
        Task<string> CompleteAsync(string prompt);
    }

    /// <summary>Stands in for the real type, under the same path below the root.</summary>
    public sealed class AiKey(string apiKey)
    {
        public string ApiKey { get; } = apiKey;
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.AiCouncil
{
    public static class TheCouncil
    {
        public static Task<string> AskAsync(AiAdapters.Contracts.IAiCompletions completions) => completions.CompleteAsync("a prompt");
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.ExchangeAdapter
{
    public static class AnAdapter
    {
        public static HttpClient Create(IHttpClientFactory factory) => factory.CreateClient("any");
    }
}
