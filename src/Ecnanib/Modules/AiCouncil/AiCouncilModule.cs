using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Modules.AiCouncil;

public static class AiCouncilModule
{
    public static IServiceCollection AddAiCouncil(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IPrompts, Prompts>();
        services.AddSingleton<IReferenceDataSeeder, PromptTemplateSeeder>();
        return services;
    }
}
