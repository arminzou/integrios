using System.Net.Http.Headers;
using System.Text;
using Integrios.Application.Abstractions;
using Integrios.Domain.Delivery;

namespace Integrios.Infrastructure.Http;

public sealed class HttpDeliveryClient(HttpClient httpClient) : IDeliveryClient
{
    public async Task<DeliveryResult> DeliverAsync(
        string url,
        string payloadJson,
        Action<HttpRequestMessage>? decorate = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var content = new StringContent(payloadJson, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content = content;

        try
        {
            decorate?.Invoke(request);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(false, 0, ex.Message, FailurePhase: DeliveryFailurePhase.RequestConstruction);
        }

        try
        {
            var response = await httpClient.SendAsync(request, cancellationToken);
            return new DeliveryResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                FailurePhase: response.IsSuccessStatusCode ? null : DeliveryFailurePhase.Http);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeliveryResult(false, 0, "Request timed out", IsTimeout: true, FailurePhase: DeliveryFailurePhase.Http);
        }
        catch (TaskCanceledException ex)
        {
            return new DeliveryResult(false, 0, ex.Message, FailurePhase: DeliveryFailurePhase.Http);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(false, 0, ex.Message, FailurePhase: DeliveryFailurePhase.Http);
        }
    }
}
