// Deliberately broken code for ModuleBoundaryGuardTests. The fixture map lets Alpha use Beta and
// nothing else, so each ViaX class below hides one forbidden dependency on Gamma in a different way.

namespace Ecnanib.Tests.Architecture.Fixtures.Modules.Gamma.Contracts
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class GammaAttribute : Attribute;

    public sealed class GammaException : Exception;

    public enum GammaKind
    {
        First = 1,
    }

    public interface IGamma;

    public class GammaThing
    {
        public const int Limit = 5;

        public static int Count() => 1;
    }
}

namespace Ecnanib.Tests.Architecture.Fixtures.Modules.Beta.Contracts
{
    public interface IBeta;
}

namespace Ecnanib.Tests.Architecture.Fixtures.Modules.Beta
{
    internal sealed class BetaInternal;

    public sealed class LeakedImplementation;

    public static class BetaModule;
}

namespace Ecnanib.Tests.Architecture.Fixtures.Modules.Delta
{
    internal sealed class NotInTheMap;
}

namespace Ecnanib.Tests.Architecture.Fixtures.Modules.Alpha
{
    using System.Collections;
    using Ecnanib.Tests.Architecture.Fixtures.Modules.Beta;
    using Ecnanib.Tests.Architecture.Fixtures.Modules.Beta.Contracts;
    using Ecnanib.Tests.Architecture.Fixtures.Modules.Gamma.Contracts;

    internal sealed class Clean(IBeta beta)
    {
        public IBeta Beta { get; } = beta;
    }

    internal sealed class UsesInternalOfAllowedUnit
    {
        public object Run() => new BetaInternal();
    }

    internal sealed class ViaField
    {
        public GammaThing? Value = null;
    }

    internal sealed class ViaProperty
    {
        public GammaThing? Value { get; set; }
    }

    internal sealed class ViaParameter
    {
        public void Run(GammaThing? value)
        {
        }
    }

    internal sealed class ViaReturnType
    {
        public GammaThing? Run() => null;
    }

    internal sealed class ViaBaseType : GammaThing;

    internal sealed class ViaInterface : IGamma;

    [Gamma]
    internal sealed class ViaClassAttribute;

    internal sealed class ViaMethodAttribute
    {
        [Gamma]
        public void Run()
        {
        }
    }

    internal sealed class ViaMethodBody
    {
        public object Run() => new GammaThing();
    }

    // Gamma appears nowhere in this signature or in the called method's.
    internal sealed class ViaStaticCall
    {
        public int Run() => GammaThing.Count();
    }

    internal sealed class ViaMethodGroup
    {
        public Func<int> Run() => GammaThing.Count;
    }

    internal sealed class ViaLambda
    {
        public Func<object> Run() => () => new GammaThing();
    }

    internal sealed class ViaClosure
    {
        public Func<object> Run(int size) => () => new GammaThing[size];
    }

    internal sealed class ViaLocalFunction
    {
        public int Run()
        {
            return Local();

            static int Local() => GammaThing.Count();
        }
    }

    internal sealed class ViaAsync
    {
        public async Task<object> Run()
        {
            await Task.Yield();
            return new GammaThing();
        }
    }

    internal sealed class ViaIterator
    {
        public IEnumerable Run()
        {
            yield return new GammaThing();
        }
    }

    internal sealed class ViaGenericField
    {
        public List<GammaThing>? Values = null;
    }

    internal sealed class ViaGenericMethodCall
    {
        public object Run() => Array.Empty<GammaThing>();
    }

    internal sealed class ViaGenericConstraint<T>
        where T : GammaThing;

    internal sealed class ViaTypeof
    {
        public Type Run() => typeof(GammaThing);
    }

    internal sealed class ViaTypeCheck
    {
        public bool Run(object value) => value is GammaThing;
    }

    internal sealed class ViaSwitchPattern
    {
        public int Run(object value) => value switch
        {
            GammaThing => 1,
            _ => 0,
        };
    }

    internal sealed class ViaCatchClause
    {
        public void Run()
        {
            try
            {
                Console.WriteLine();
            }
            catch (GammaException)
            {
            }
        }
    }

    // The compiler inlines the next three, so they leave no reference in the compiled code.
    internal sealed class ViaConstant
    {
        public int Run() => GammaThing.Limit;
    }

    internal sealed class ViaEnumValue
    {
        public int Run() => (int)GammaKind.First;
    }

    internal sealed class ViaNameof
    {
        public string Run() => nameof(GammaThing);
    }
}
