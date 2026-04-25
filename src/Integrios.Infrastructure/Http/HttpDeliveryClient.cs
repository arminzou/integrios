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
        catch (Exception ex)
        {
            return new DeliveryResult(false, 0, ex.Message);
        }
    }
}
