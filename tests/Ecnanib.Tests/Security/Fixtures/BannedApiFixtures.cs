// Deliberately broken code for BannedApiGuardTests. None of it ever runs. Each class hides one use
// of a banned API in a different way, except the last two namespaces, which have to pass.

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
}

namespace Ecnanib.Tests.Security.Fixtures.Allowed
{
    public static class InterpolatedSql
    {
        public static Task<int> RunAsync(DbContext context) => context.Database.ExecuteSqlAsync($"SELECT 1");
    }
}

namespace Ecnanib.Tests.Security.Fixtures.Modules.ExchangeAdapter
{
    public static class AnAdapter
    {
        public static HttpClient Create(IHttpClientFactory factory) => factory.CreateClient("any");
    }
}
