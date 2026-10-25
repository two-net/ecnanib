using Ecnanib.Ui;
using Ecnanib.Ui.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Ui.DependencyInjection;

public class AddUiTests
{
    [Fact]
    public void RegistersTheOptionsAndTheValidatorAndNothingElse()
    {
        var services = Build(new() { ["Ui:TransitionMilliseconds"] = "120" });
        using var provider = services.BuildServiceProvider();

        Assert.Equal(120, provider.GetRequiredService<IOptions<UiOptions>>().Value.TransitionMilliseconds);
        Assert.Contains(provider.GetServices<IValidateOptions<UiOptions>>(), v => v is UiOptionsValidator);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AnInvalidSectionFailsAtStartupValidation()
    {
        using var provider = Build(new() { ["Ui:DefaultTheme"] = "sepia" }).BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("Ui:DefaultTheme must be System, Dark or Light.", ex.Message);
    }

    private static ServiceCollection Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddUi(configuration);
        return services;
    }
}
