using Ecnanib.Ui.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Ui;

public static class UiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the validated <see cref="UiOptions"/>: the theme and animation defaults for visitors and new accounts
    /// (Appendix D "ui"). Register before <c>AddAccounts</c>, which seeds new users from them. Adds no service that
    /// connects to anything.
    /// </summary>
    public static IServiceCollection AddUi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<UiOptions>()
            .Bind(configuration.GetSection(UiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<UiOptions>, UiOptionsValidator>());

        return services;
    }
}
