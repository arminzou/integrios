using System.Net.Http.Headers;
using System.Text;
using Integrios.Application.Abstractions;

namespace Integrios.Infrastructure.Http;

public sealed class HttpDeliveryClient(HttpClient httpClient) : IDeliveryClient
{
    public async Task<DeliveryResult> DeliverAsync(
        string url,
        string payloadJson,
        Action<HttpRequestMessage>? decorate = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            var content = new StringContent(payloadJson, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;
            decorate?.Invoke(request);

            var response = await httpClient.SendAsync(request, cancellationToken);
            return new DeliveryResult(response.IsSuccessStatusCode, (int)response.StatusCode);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeliveryResult(false, 0, "Request timed out", IsTimeout: true);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(false, 0, ex.Message);
        }
    }
}
