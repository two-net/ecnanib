using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Ecnanib.Tests.Logging;

/// <summary>Added behind the application's own pipeline, so request logging wraps it as it wraps an endpoint.</summary>
internal sealed class TerminalMiddleware(RequestDelegate terminal) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
    {
        next(builder);
        builder.Run(terminal);
    };
}
