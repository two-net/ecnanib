// Types with deliberate rule violations. DependencyCheckerTests runs the checker over them to
// prove that it reports each kind of forbidden reference. They are never used by the application.
//
// The fixture rules are: Low uses nothing; Mid may use Low and Gateway; High may use Low and
// Gateway, and is the only area that may use Gateway.Trading. Low is the only area that may use
// the host's configuration, and no area may read the process environment or create an HTTP client.

#pragma warning disable CA1822 // Members are instance members on purpose.

namespace Ecnanib.Tests.Architecture.Fixtures.Low
{
    public sealed class Clean;

    public sealed class UsesConfigurationAsAllowed
    {
        public string? Read(Microsoft.Extensions.Configuration.IConfiguration configuration) => configuration["Key"];
    }

    public sealed class UsesHighInSignature
    {
        public void Use(High.Top top) => _ = top;
    }

    public sealed class UsesHighInBody
    {
        public object Make() => new High.Top();
    }

    public sealed class UsesHighInLambda
    {
        public Func<object> Make() => () => new High.Top();
    }

    public sealed class UsesHighInAsyncBody
    {
        public async Task<object> MakeAsync()
        {
            await Task.Yield();
            return new High.Top();
        }
    }

    public sealed class UsesHighAsGenericArgument
    {
        public List<High.Top> Items { get; } = [];
    }
}

namespace Ecnanib.Tests.Architecture.Fixtures.Mid
{
    public sealed class UsesGatewayReadSide
    {
        public void Read(Gateway.IReader reader) => _ = reader;
    }

    public sealed class UsesTrading
    {
        public void Place(Gateway.Trading.IOrders orders) => _ = orders;
    }

    public sealed class UsesAdapterDirectly
    {
        public object Make() => new Gateway.Adapters.Fake.FakeAdapter();
    }

    public sealed class UsesNetwork
    {
        public object Make() => new HttpClient();
    }

    public sealed class UsesConfiguration
    {
        public string? Read(Microsoft.Extensions.Configuration.IConfiguration configuration) => configuration["Key"];
    }

    public sealed class ReadsAnEnvironmentVariable
    {
        public string? Read() => Environment.GetEnvironmentVariable("ECNANIB_FIXTURE");
    }

    public sealed class ReadsAllEnvironmentVariables
    {
        public Func<object> Read() => () => Environment.GetEnvironmentVariables();
    }

    public sealed class ReadsTheCommandLine
    {
        public string[] Read() => Environment.GetCommandLineArgs();
    }

    public sealed class UsesTheEnvironmentAsAllowed
    {
        public string Read() => Environment.NewLine + Environment.ProcessId;
    }
}

namespace Ecnanib.Tests.Architecture.Fixtures.High
{
    public sealed class Top
    {
        public Low.Clean Clean { get; } = new();
    }

    public sealed class PlacesOrders
    {
        public void Place(Gateway.Trading.IOrders orders) => _ = orders;
    }
}

namespace Ecnanib.Tests.Architecture.FixtureHost
{
    // Stands for the composition root: outside every area, like Program. It may use any area,
    // but not a component's adapters or a restricted namespace.
    public sealed class Host
    {
        public Fixtures.High.Top Top { get; } = new();

        public object Make() => new Fixtures.Gateway.Adapters.Fake.FakeAdapter();

        public void Place(Fixtures.Gateway.Trading.IOrders orders) => _ = orders;
    }
}

namespace Ecnanib.Tests.Architecture.Fixtures.Gateway
{
    public interface IReader;
}

namespace Ecnanib.Tests.Architecture.Fixtures.Gateway.Trading
{
    public interface IOrders;
}

namespace Ecnanib.Tests.Architecture.Fixtures.Gateway.Adapters.Fake
{
    public sealed class FakeAdapter : IReader, Trading.IOrders
    {
        public object Client { get; } = new HttpClient();
    }
}
