# What is Integrios?

Integrios is an open-source, self-hostable platform for setting up and running event-driven
integrations. Your team configures how events move from your existing systems to downstream
applications, while Integrios handles reliable delivery and recovery without a separate custom
worker for each integration.

## Why does it exist?

Each integration needs a source, a destination, routing rules, and often a different payload shape.
Building those choices into a custom service means repeating setup and mapping code. Then a
destination goes down, a request times out, or someone asks whether an event arrived: now that
service also needs retries, delivery history, and recovery. Integrios brings the configuration and
reliability work together so your team can focus on what the integration is meant to do.

## What does it do?

Integrios accepts events from applications, incoming webhooks, or supported message brokers. It
routes each accepted event to the right destinations, delivers it over HTTP, and records what
happened. When delivery fails, your team can inspect the attempts and recover the work without
returning to the source system.

You can configure sources, destinations, and subscriptions through the dashboard. Guided mapping
tools help shape common payloads; advanced expressions remain available when an integration needs
them.

You run Integrios on your own infrastructure and keep control of your sources and configuration.
If you serve multiple tenants, their events and integration settings stay separate.

Follow the [setup and quickstart](setup.md) to deliver your first event, or see a concrete
[GitHub-to-Slack walkthrough](github-to-slack-walkthrough.md). The [architecture guide](architecture.md)
explains the processing flow and service boundaries.
