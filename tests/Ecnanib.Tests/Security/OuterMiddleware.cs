using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Ecnanib.Tests.Security;

/// <summary>Runs ahead of the application's own pipeline, to set what a test host can't.</summary>
internal sealed class OuterMiddleware(Action<HttpContext> before) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
    {
        builder.Use((context, inner) =>
        {
            before(context);
            return inner(context);
        });

        next(builder);
    };
}
