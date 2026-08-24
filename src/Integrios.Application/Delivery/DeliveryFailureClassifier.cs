using Integrios.Domain.Entities;
using Integrios.Domain.Enums;

namespace Integrios.Application.Delivery;

// Fixed platform disposition policy: transport errors, timeouts, 408, 429, and 5xx are transient
// and keep retrying to exhaustion; everything else that reaches the HTTP phase (other 4xx, 3xx, and
// a logically rejected 2xx) is terminal and dead-letters immediately. Non-HTTP failure phases
// (transform, secret resolution, request construction) are unaffected and keep the pre-existing
// retry-until-exhaustion behavior, since a bad request never reached a real HTTP outcome to classify.
public static class DeliveryFailureClassifier
{
    public static bool IsTerminal(DeliveryResult result)
    {
        if (result.Succeeded || result.FailurePhase != DeliveryFailurePhase.Http || result.IsTimeout)
            return false;

        return result.StatusCode switch
        {
            0 => false,
            408 or 429 => false,
            >= 500 => false,
            _ => true
        };
    }
}
