using System.Net.Http.Headers;
using System.Text;
using Integrios.Application.Abstractions;

namespace Integrios.Infrastructure.Http;

public sealed class HttpDeliveryClient(HttpClient httpClient) : IDeliveryClient
{
    public async Task<DeliveryResult> DeliverAsync(
        string url,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var content = new StringContent(payloadJson, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await httpClient.PostAsync(url, content, cancellationToken);
            return new DeliveryResult(response.IsSuccessStatusCode, (int)response.StatusCode);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Token not cancelled by the caller, so this is the HttpClient.Timeout firing.
            return new DeliveryResult(false, 0, "Request timed out", IsTimeout: true);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(false, 0, ex.Message);
        }
    }
}
