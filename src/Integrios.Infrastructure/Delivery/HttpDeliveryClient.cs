using System.Net.Http.Headers;
using System.Text;
using Integrios.Application.Delivery;
using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Infrastructure.Delivery;

internal sealed class HttpDeliveryClient(HttpClient httpClient) : IDeliveryClient
{
    // "Bounded" per the design: never wait longer than this even if a provider asks for more.
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(15);

    // The ceiling on what is kept for diagnosis. Applied at capture rather than at read because the
    // value is a foreign server's output: unbounded by contract, and an error page from a
    // misconfigured proxy can be megabytes.
    internal const int CaptureMaxBytes = 8 * 1024;

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

            // Read once, capped by whichever bound is larger, and serve both readers from those
            // bytes: the response stream can only be consumed once, and capture must not change
            // what an outcome rule is allowed to see.
            bool evaluatesBody = successRule is { Evaluator: "json_boolean" };
            int ruleMaxBytes = evaluatesBody
                ? successRule!.MaxBodyBytes ?? HttpSuccessRule.DefaultMaxBodyBytes
                : 0;

            (byte[] body, bool moreRemained) = await ReadCappedAsync(
                response, Math.Max(CaptureMaxBytes, ruleMaxBytes), cancellationToken);

            bool truncated = moreRemained || body.Length > CaptureMaxBytes;
            string? captured = Capture(body);

            if (!response.IsSuccessStatusCode)
            {
                return new DeliveryResult(
                    false, statusCode, FailurePhase: DeliveryFailurePhase.Http, RetryAfter: retryAfter,
                    ResponseBody: captured, ResponseBodyTruncated: truncated);
            }

            if (!evaluatesBody)
                return new DeliveryResult(
                    true, statusCode, ResponseBody: captured, ResponseBodyTruncated: truncated);

            if (moreRemained || body.Length > ruleMaxBytes)
            {
                return new DeliveryResult(
                    false, statusCode, "Response body exceeded the configured outcome-evaluation bound.",
                    FailurePhase: DeliveryFailurePhase.Http,
                    ResponseBody: captured, ResponseBodyTruncated: truncated);
            }

            bool accepted = HttpSuccessEvaluator.Evaluate(successRule, body, out string? diagnostic);
            return accepted
                ? new DeliveryResult(true, statusCode, ResponseBody: captured, ResponseBodyTruncated: truncated)
                : new DeliveryResult(
                    false, statusCode, diagnostic, FailurePhase: DeliveryFailurePhase.Http,
                    ResponseBody: captured, ResponseBodyTruncated: truncated);
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

    // Reads at most `cap` bytes and reports whether the body continued past them, so a destination
    // cannot dictate how much the Worker buffers. The caller decides what exceeding a bound means:
    // for an outcome rule it is a failure, for capture it is a truncation.
    private static async Task<(byte[] Body, bool MoreRemained)> ReadCappedAsync(
        HttpResponseMessage response, int cap, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while (buffer.Length <= cap && (read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            buffer.Write(chunk, 0, read);

        byte[] body = buffer.ToArray();
        return body.Length > cap ? (body[..cap], true) : (body, false);
    }

    // Decoded with `flush: false` so a multi-byte character split by the ceiling is dropped rather
    // than stored as a replacement character the destination never sent.
    private static string? Capture(byte[] body)
    {
        if (body.Length == 0)
            return null;

        ReadOnlySpan<byte> bounded = body.Length > CaptureMaxBytes
            ? body.AsSpan(0, CaptureMaxBytes)
            : body;

        Decoder decoder = Encoding.UTF8.GetDecoder();
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(bounded.Length)];
        int count = decoder.GetChars(bounded, chars, flush: false);
        return count == 0 ? null : new string(chars, 0, count);
    }
}
