-- W3C traceparent carries trace context across the async outbox and
-- delivery-retry hops so a single event yields one continuous trace.
-- Nullable and additive: older rows have no stored context and start a
-- fresh trace on the consuming side.
ALTER TABLE outbox
    ADD COLUMN traceparent TEXT;

ALTER TABLE subscription_deliveries
    ADD COLUMN traceparent TEXT;
