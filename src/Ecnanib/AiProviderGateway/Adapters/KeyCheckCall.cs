using System.Net.Http.Headers;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.AiProviderGateway.Adapters;

/// <summary>
/// The one call that every provider's key check makes: a GET for the list of its models, with
/// the key in a header, which reads and changes nothing (FR-KEY-3). What differs between
/// providers is the path, the header, and which answer is a refusal, and each provider's check
/// says those.
/// </summary>
/// <remarks>
/// <para>
/// Only an answer that the provider gives to a wrong key is a refusal. Anything else that is not
/// a success says nothing about the key: a limit on requests, a block of the service's address,
/// a fault of the provider, or a request that this code got wrong. It is
/// <see cref="KeyVerdict.Unreachable"/>, so that none of them turns every stored key invalid.
/// </para>
/// <para>
/// The answer is not read beyond what tells a refusal. Which models a key can use is the
/// catalog's to ask (FR-CAT-9).
/// </para>
/// </remarks>
internal sealed class KeyCheckCall(IHttpClientFactory clients, AiProvidersSettings settings)
{
    /// <summary>
    /// How long a provider may take to answer a check. It is shorter than the time a call to a
    /// model may take: a user waits for it with a form.
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    /// <summary>Asks a provider for its models with a key, and says what the answer means for the key.</summary>
    /// <param name="provider">The provider, whose address the configuration file holds.</param>
    /// <param name="path">The path of the list of models, under the provider's address, with its query.</param>
    /// <param name="authorize">Puts the key into the headers of the request, as the provider takes it.</param>
    /// <param name="refuses">Whether an answer that is not a success is the provider's answer to a wrong key.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<KeyVerdict> AskAsync(
        AiProviderCode provider,
        string path,
        Action<HttpRequestHeaders> authorize,
        Func<HttpResponseMessage, CancellationToken, Task<bool>> refuses,
        CancellationToken cancellationToken)
    {
        if (!settings.ByCode.TryGetValue(provider, out var endpoint))
        {
            throw new InvalidOperationException($"The configuration file has no entry for AI provider '{provider}'.");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Limit);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.BaseUrl.AbsoluteUri.TrimEnd('/')}/{path}");
            authorize(request.Headers);

            var client = clients.CreateClient(AiProviderGatewayModule.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);

            if (response.IsSuccessStatusCode)
            {
                return KeyVerdict.Accepted;
            }

            return await refuses(response, limit.Token) ? KeyVerdict.Refused : KeyVerdict.Unreachable;
        }
        catch (HttpRequestException)
        {
            // No answer. The call is in the log, with why.
            return KeyVerdict.Unreachable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The provider took longer than the limit. The caller did not cancel.
            return KeyVerdict.Unreachable;
        }
    }

    /// <summary>A refusal that the status of the answer tells, whatever its body says.</summary>
    public static Func<HttpResponseMessage, CancellationToken, Task<bool>> Status(params System.Net.HttpStatusCode[] statuses) =>
        (response, _) => Task.FromResult(statuses.Contains(response.StatusCode));
}
