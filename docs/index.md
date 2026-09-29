---
layout: doc
title: Self-hosted event integration documentation
description: Integrios is a self-hosted runtime for moving business-system Events to HTTP APIs. Explore setup, delivery, operations, and recovery.
outline: false
aside: false
---

# Integrios documentation

Integrios is a self-hosted runtime for moving business-system Events to HTTP APIs. Your team keeps control of its sources and deployment while Integrios handles delivery and gives Operators the history needed to investigate failures.

<div class="docs-start">
  <div>
    <strong>New to Integrios?</strong>
    <p>Run the local stack and deliver your first Event.</p>
  </div>
  <a href="/setup.html">Start with setup →</a>
</div>

<div class="docs-index-grid">
  <section>
    <h2><span class="docs-index-icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h15m-6-6 6 6-6 6"/></svg></span>Get started</h2>
    <a href="/setup.html">Setup and quickstart</a>
    <p>Run locally and send an Event end to end.</p>
    <a href="/github-to-slack-walkthrough.html">GitHub to Slack walkthrough</a>
    <p>Configure a provider webhook and HTTP Destination.</p>
  </section>

  <section>
    <h2><span class="docs-index-icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><circle cx="5" cy="12" r="2"/><circle cx="19" cy="6" r="2"/><circle cx="19" cy="18" r="2"/><path d="m7 11 10-4m-10 6 10 4"/></svg></span>Understand the system</h2>
    <a href="/architecture.html">Architecture</a>
    <p>Service boundaries, Event flow, and delivery behavior.</p>
    <a href="/connector-manifest.html">Connector manifest</a>
    <p>Reference for reusable Source and Destination contracts.</p>
  </section>

  <section>
    <h2><span class="docs-index-icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round"><path d="M4 6h16M4 12h16M4 18h16"/><circle cx="9" cy="6" r="2" fill="var(--vp-c-bg-soft)"/><circle cx="16" cy="12" r="2" fill="var(--vp-c-bg-soft)"/><circle cx="10" cy="18" r="2" fill="var(--vp-c-bg-soft)"/></svg></span>Configure and operate</h2>
    <a href="/configuration.html">Runtime configuration</a>
    <p>Host settings, credentials, and Tenant secret sources.</p>
    <a href="/operator-dashboard.html">Operator dashboard</a>
    <p>Sign-in methods and credential access.</p>
    <a href="/observability.html">Observability</a>
    <p>Metrics, traces, and logs for your monitoring stack.</p>
  </section>

  <section>
    <h2><span class="docs-index-icon" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="7" rx="1"/><rect x="4" y="13" width="16" height="7" rx="1"/><path d="M7 7.5h.01M7 16.5h.01"/></svg></span>Deploy and maintain</h2>
    <a href="/database-backends.html">Database backends</a>
    <p>PostgreSQL and SQL Server setup.</p>
    <a href="/ci-cd.html">CI and releases</a>
    <p>Build, publish, and consume service images.</p>
    <a href="https://github.com/arminzou/integrios/blob/main/deploy/README.md">Deployment guide</a>
    <p>Production runbook in the repository.</p>
  </section>
</div>
