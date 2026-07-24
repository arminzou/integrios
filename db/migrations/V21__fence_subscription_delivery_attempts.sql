DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM subscription_deliveries)
       OR EXISTS (SELECT 1 FROM delivery_attempts) THEN
        RAISE EXCEPTION 'V21 requires subscription_deliveries and delivery_attempts to be empty before fenced delivery attempts are introduced';
    END IF;
END $$;

ALTER TABLE subscription_deliveries
    RENAME COLUMN attempt_count TO lifetime_attempt_count;

ALTER TABLE subscription_deliveries
    ADD COLUMN retry_cycle_attempt_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN active_attempt_id UUID,
    ADD COLUMN lease_expires_at TIMESTAMPTZ,
    ADD CONSTRAINT ck_subscription_deliveries_attempt_counts_nonnegative
        CHECK (
            lifetime_attempt_count >= 0
            AND retry_cycle_attempt_count >= 0
            AND retry_cycle_attempt_count <= lifetime_attempt_count
        ),
    ADD CONSTRAINT ck_subscription_deliveries_lease_state
        CHECK (
            (status = 'in_flight' AND active_attempt_id IS NOT NULL AND lease_expires_at IS NOT NULL)
            OR
            (status IN ('pending', 'succeeded', 'dead_lettered') AND active_attempt_id IS NULL AND lease_expires_at IS NULL)
        );

ALTER TABLE delivery_attempts
    DROP COLUMN event_id,
    DROP COLUMN subscription_id,
    DROP COLUMN destination_connection_id,
    ADD COLUMN subscription_delivery_id UUID NOT NULL REFERENCES subscription_deliveries(id),
    ADD COLUMN failure_phase TEXT,
    ADD CONSTRAINT ck_delivery_attempts_status
        CHECK (status IN ('in_progress', 'succeeded', 'failed', 'indeterminate')),
    ADD CONSTRAINT ck_delivery_attempts_number_positive
        CHECK (attempt_number > 0),
    ADD CONSTRAINT ck_delivery_attempts_failure_phase
        CHECK (
            (
                status = 'failed'
                AND failure_phase IS NOT NULL
                AND failure_phase IN ('transform', 'secret_resolution', 'request_construction', 'http')
            )
            OR
            (status <> 'failed' AND failure_phase IS NULL)
        ),
    ADD CONSTRAINT ck_delivery_attempts_completion
        CHECK (
            (status = 'in_progress' AND completed_at IS NULL)
            OR
            (status <> 'in_progress' AND completed_at IS NOT NULL)
        ),
    ADD CONSTRAINT uq_delivery_attempts_delivery_number
        UNIQUE (subscription_delivery_id, attempt_number),
    ADD CONSTRAINT uq_delivery_attempts_delivery_id
        UNIQUE (subscription_delivery_id, id);

ALTER TABLE subscription_deliveries
    ADD CONSTRAINT fk_subscription_deliveries_active_attempt
        FOREIGN KEY (id, active_attempt_id)
        REFERENCES delivery_attempts(subscription_delivery_id, id);

CREATE INDEX idx_delivery_attempts_delivery
    ON delivery_attempts(subscription_delivery_id, attempt_number);

DROP INDEX idx_subscription_deliveries_pending;

CREATE INDEX idx_subscription_deliveries_claimable
    ON subscription_deliveries(status, lease_expires_at, deliver_after, created_at)
    WHERE status IN ('pending', 'in_flight');
