using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.Credentials.Contracts;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// Validates an AI key the lightweight way FR-KEY-04 suggests: by listing its models. The adapters
/// normalise each provider's refusal, including Gemini's and xAI's misleading 400s, as
/// <see cref="AiFailure.Authentication"/>. A 403 means the provider knows the key but won't let it list
/// models, such as an OpenAI key restricted without model access.
/// </summary>
internal sealed class AiValidator(IAiProviders providers, ILogger<AiValidator> logger)
{
    public async Task<Verdict> ValidateAsync(string provider, Guid credentialId, SecretValue apiKey, CancellationToken cancellationToken)
    {
        try
        {
            var models = await providers.ListModelsAsync(new AiKey(provider, credentialId, apiKey), cancellationToken);

            return models.Count > 0
                ? new Verdict(Reached: true, CredentialStatus.Valid)
                : new Verdict(Reached: true, CredentialStatus.InsufficientPermissions, CredentialProblem.NoModels);
        }
        catch (AiException exception) when (exception.Failure == AiFailure.Authentication)
        {
            return exception.StatusCode == StatusCodes.Status403Forbidden
                ? new Verdict(Reached: true, CredentialStatus.InsufficientPermissions, CredentialProblem.PermissionDenied)
                : Verdict.Refused;
        }
        catch (AiException exception)
        {
            // The adapter logged each attempt already, with its status and code.
            logger.LogInformation(
                "{Provider} couldn't validate credential {CredentialId}: {Failure}", provider, credentialId, exception.Failure);

            return Verdict.Unreachable;
        }
    }
}
