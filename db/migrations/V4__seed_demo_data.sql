-- Demo seed data for local development and routing demos.
-- Two tenants: demo-swiftpay (payment processing) and demo-tradefront (stock trading).
-- All UUIDs are fixed for reproducibility.
--
-- API credentials:
--   demo-swiftpay:   Authorization: ApiKey swiftpay_pub_key:swiftpay_secret
--   demo-tradefront: Authorization: ApiKey tradefront_pub_key:tradefront_secret

-- webhook integration (platform-level, not tenant-owned)
INSERT INTO integrations (id, key, name, direction, auth_scheme, description)
VALUES (
    '00000000-0000-0000-0000-000000000001',
    'webhook',
    'Webhook',
    'both',
    'none',
    'Generic webhook source or destination over HTTP.'
);

-- ─── demo-swiftpay ───────────────────────────────────────────────────────────

INSERT INTO tenants (id, slug, name, status, environment, description)
VALUES (
    'aaaaaaaa-0000-0000-0000-000000000001',
    'demo-swiftpay',
    'Demo SwiftPay',
    'active',
    'demo',
    'Demo tenant — payment processor domain.'
);

-- secret: swiftpay_secret  (sha256)
INSERT INTO api_keys (id, tenant_id, name, key_id, secret_hash, scopes, status)
VALUES (
    'aaaaaaaa-0000-0000-0000-000000000002',
    'aaaaaaaa-0000-0000-0000-000000000001',
    'SwiftPay Demo Key',
    'swiftpay_pub_key',
    'sha256:e5f19b94f725867b61eb7f62cd274e077dd5fad60c2e8c4e1d9a936e431e333a',
    '{}',
    'active'
);

INSERT INTO connections (id, tenant_id, integration_id, name, config, status, description)
VALUES
    -- source: where inbound webhooks arrive from
    (
        'aaaaaaaa-0000-0000-0000-000000000003',
        'aaaaaaaa-0000-0000-0000-000000000001',
        '00000000-0000-0000-0000-000000000001',
        'swiftpay-webhook-source',
        '{}',
        'active',
        'Inbound webhook source for SwiftPay events.'
    ),
    -- ledger sink
    (
        'aaaaaaaa-0000-0000-0000-000000000004',
        'aaaaaaaa-0000-0000-0000-000000000001',
        '00000000-0000-0000-0000-000000000001',
        'swiftpay-ledger-sink',
        '{"url": "http://localhost:5054/sink/swiftpay-ledger"}',
        'active',
        'Ledger service sink for SwiftPay.'
    ),
    -- risk sink
    (
        'aaaaaaaa-0000-0000-0000-000000000005',
        'aaaaaaaa-0000-0000-0000-000000000001',
        '00000000-0000-0000-0000-000000000001',
        'swiftpay-risk-sink',
        '{"url": "http://localhost:5054/sink/swiftpay-risk"}',
        'active',
        'Risk engine sink for SwiftPay.'
    );

INSERT INTO pipelines (id, tenant_id, name, source_connection_id, event_types, status, description)
VALUES (
    'aaaaaaaa-0000-0000-0000-000000000006',
    'aaaaaaaa-0000-0000-0000-000000000001',
    'payment-events-main',
    'aaaaaaaa-0000-0000-0000-000000000003',
    ARRAY['payment.created', 'payment.settled', 'payment.authorized'],
    'active',
    'Main pipeline for SwiftPay payment events.'
);

INSERT INTO routes (id, pipeline_id, name, match_rules, destination_connection_id, order_index, status, description)
VALUES
    (
        'aaaaaaaa-0000-0000-0000-000000000007',
        'aaaaaaaa-0000-0000-0000-000000000006',
        'payment-to-ledger',
        '{"event_types": ["payment.created", "payment.settled"]}',
        'aaaaaaaa-0000-0000-0000-000000000004',
        0,
        'active',
        'Route payment.created and payment.settled to the ledger sink.'
    ),
    (
        'aaaaaaaa-0000-0000-0000-000000000008',
        'aaaaaaaa-0000-0000-0000-000000000006',
        'payment-to-risk',
        '{"event_types": ["payment.authorized"]}',
        'aaaaaaaa-0000-0000-0000-000000000005',
        1,
        'active',
        'Route payment.authorized to the risk engine sink.'
    );

-- ─── demo-tradefront ──────────────────────────────────────────────────────────

INSERT INTO tenants (id, slug, name, status, environment, description)
VALUES (
    'bbbbbbbb-0000-0000-0000-000000000001',
    'demo-tradefront',
    'Demo TradeFront',
    'active',
    'demo',
    'Demo tenant — stock trading brokerage domain.'
);

-- secret: tradefront_secret  (sha256)
INSERT INTO api_keys (id, tenant_id, name, key_id, secret_hash, scopes, status)
VALUES (
    'bbbbbbbb-0000-0000-0000-000000000002',
    'bbbbbbbb-0000-0000-0000-000000000001',
    'TradeFront Demo Key',
    'tradefront_pub_key',
    'sha256:fe440865f05f90acbbacf68810bc880574b1f32ab54d7f5aa6df3f0b3062eff7',
    '{}',
    'active'
);

INSERT INTO connections (id, tenant_id, integration_id, name, config, status, description)
VALUES
    -- source
    (
        'bbbbbbbb-0000-0000-0000-000000000003',
        'bbbbbbbb-0000-0000-0000-000000000001',
        '00000000-0000-0000-0000-000000000001',
        'tradefront-webhook-source',
        '{}',
        'active',
        'Inbound webhook source for TradeFront events.'
    ),
    -- ledger sink
    (
        'bbbbbbbb-0000-0000-0000-000000000004',
        'bbbbbbbb-0000-0000-0000-000000000001',
        '00000000-0000-0000-0000-000000000001',
        'tradefront-ledger-sink',
        '{"url": "http://localhost:5054/sink/tradefront-ledger"}',
        'active',
        'Ledger service sink for TradeFront.'
    ),
    -- risk sink
    (
        'bbbbbbbb-0000-0000-0000-000000000005',
        'bbbbbbbb-0000-0000-0000-000000000001',
        '00000000-0000-0000-0000-000000000001',
        'tradefront-risk-sink',
        '{"url": "http://localhost:5054/sink/tradefront-risk"}',
        'active',
        'Risk engine sink for TradeFront.'
    );

INSERT INTO pipelines (id, tenant_id, name, source_connection_id, event_types, status, description)
VALUES (
    'bbbbbbbb-0000-0000-0000-000000000006',
    'bbbbbbbb-0000-0000-0000-000000000001',
    'brokerage-events-main',
    'bbbbbbbb-0000-0000-0000-000000000003',
    ARRAY['account.funded', 'withdrawal.requested', 'trade.executed', 'trade.rejected'],
    'active',
    'Main pipeline for TradeFront brokerage events.'
);

INSERT INTO routes (id, pipeline_id, name, match_rules, destination_connection_id, order_index, status, description)
VALUES
    (
        'bbbbbbbb-0000-0000-0000-000000000007',
        'bbbbbbbb-0000-0000-0000-000000000006',
        'funding-to-ledger',
        '{"event_types": ["account.funded", "withdrawal.requested"]}',
        'bbbbbbbb-0000-0000-0000-000000000004',
        0,
        'active',
        'Route account.funded and withdrawal.requested to the ledger sink.'
    ),
    (
        'bbbbbbbb-0000-0000-0000-000000000008',
        'bbbbbbbb-0000-0000-0000-000000000006',
        'trades-to-risk',
        '{"event_types": ["trade.executed", "trade.rejected"]}',
        'bbbbbbbb-0000-0000-0000-000000000005',
        1,
        'active',
        'Route trade.executed and trade.rejected to the risk engine sink.'
    );
