using Ecnanib.Platform.Contracts;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Ecnanib.Platform.Correlation;

/// <summary>
/// Gives each user action on a circuit its own ID. Without this, circuit logs would still carry an
/// ID, but it would be the /_blazor request's, shared by everything until the user disconnects.
/// </summary>
internal sealed class CorrelationCircuitHandler(ICorrelationContext correlation) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            using var scope = correlation.Begin();

            await next(context);
        };
}
