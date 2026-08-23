using System.Net.Http.Headers;
using System.Text;
using Integrios.Application.Delivery;
using Integrios.Domain.Delivery;

namespace Integrios.Infrastructure.Delivery;

internal sealed class HttpDeliveryClient(HttpClient httpClient) : IDeliveryClient
{
    // "Bounded" per the design: never wait longer than this even if a provider asks for more.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(15);

    public async Task<DeliveryResult> DeliverAsync(
        OutboundHttpMessage outboundRequest,
        HttpSuccessRule? successRule,
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
            int statusCode = (int)response.StatusCode;
            TimeSpan? retryAfter = ParseRetryAfter(response, statusCode);

            if (!response.IsSuccessStatusCode)
            {
                return new DeliveryResult(
                    false, statusCode, FailurePhase: DeliveryFailurePhase.Http, RetryAfter: retryAfter);
            }

            if (successRule is not { Evaluator: "json_boolean" })
                return new DeliveryResult(true, statusCode);

            byte[]? body = await ReadBoundedBodyAsync(
                response, successRule.MaxBodyBytes ?? HttpSuccessRule.DefaultMaxBodyBytes, cancellationToken);
            if (body is null)
            {
                return new DeliveryResult(
                    false, statusCode, "Response body exceeded the configured outcome-evaluation bound.",
                    FailurePhase: DeliveryFailurePhase.Http);
            }

            bool accepted = HttpSuccessEvaluator.Evaluate(successRule, body, out string? diagnostic);
            return accepted
                ? new DeliveryResult(true, statusCode)
                : new DeliveryResult(false, statusCode, diagnostic, FailurePhase: DeliveryFailurePhase.Http);
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

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response, int statusCode)
    {
        if (statusCode is not (429 or 503))
            return null;

        RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        TimeSpan? delta = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delta is null || delta <= TimeSpan.Zero)
            return null;

        return delta.Value > MaxRetryAfter ? MaxRetryAfter : delta.Value;
    }

    // Bounded so a provider cannot force the Worker to buffer an unbounded response merely because
    // its manifest opted into HTTP success rule evaluation.
    private static async Task<byte[]?> ReadBoundedBodyAsync(
        HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
            return null;

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
