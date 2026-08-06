using System.Net.Http.Headers;
using System.Text;
using Integrios.Application.Delivery;
using Integrios.Domain.Delivery;

namespace Integrios.Infrastructure.Delivery;

internal sealed class HttpDeliveryClient(HttpClient httpClient) : IDeliveryClient
{
    public async Task<DeliveryResult> DeliverAsync(
        OutboundHttpMessage outboundRequest,
        CancellationToken cancellationToken)
    {
        if (!OutboundHttpDestination.TryParse(outboundRequest.Uri, out Uri? destination))
        {
            return new DeliveryResult(
                false,
                0,
                "Destination must be an absolute HTTP or HTTPS URL.",
                FailurePhase: DeliveryFailurePhase.RequestConstruction);
        }

        using var request = new HttpRequestMessage(new HttpMethod(outboundRequest.Method), destination);
        if (outboundRequest.JsonBody is not null)
        {
            var content = new StringContent(outboundRequest.JsonBody, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;
        }

        try
        {
            foreach ((string name, string value) in outboundRequest.Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(name, value))
                    throw new DeliveryConfigurationException($"Outbound header '{name}' could not be applied.");
            }
        }
        catch (Exception ex)
        {
            return new DeliveryResult(
                false,
                0,
                DeliveryConfigurationException.SafeMessage(ex),
                FailurePhase: DeliveryFailurePhase.RequestConstruction);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return new DeliveryResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                FailurePhase: response.IsSuccessStatusCode ? null : DeliveryFailurePhase.Http);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DeliveryResult(false, 0, "Request timed out", IsTimeout: true, FailurePhase: DeliveryFailurePhase.Http);
        }
        catch (TaskCanceledException)
        {
            return new DeliveryResult(false, 0, "Request was canceled.", FailurePhase: DeliveryFailurePhase.Http);
        }
        // The stock HttpClientHandler supplies transport diagnostics such as DNS, connection,
        // and TLS failures through HttpRequestException without including request headers.
        catch (HttpRequestException ex)
        {
            return new DeliveryResult(false, 0, ex.Message, FailurePhase: DeliveryFailurePhase.Http);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(
                false,
                0,
                DeliveryConfigurationException.SafeMessage(ex, "Outbound HTTP request failed."),
                FailurePhase: DeliveryFailurePhase.Http);
        }
    }
}
