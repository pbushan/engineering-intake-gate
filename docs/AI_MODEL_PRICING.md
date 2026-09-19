# AI model pricing and cost accounting

Engineering Intake Gate displays estimated model pricing only after the backend has validated and confirmed an OpenAI or Anthropic model. Pricing lookup failure is non-fatal: the confirmed model remains usable and setup may continue.

## Resolution and freshness

The provider-neutral `AiModelPricingService` owns resolution. The browser calls the pricing API only for the currently confirmed provider/model and never while a manual ID is being typed or merely validated.

Resolution order is designed for:

1. an explicit/manual override already stored for negotiated, regional, or enterprise pricing;
2. a future authoritative application catalog source;
3. the bundled application catalog;
4. unavailable, with no guessed or zero-valued fallback.

SQLite is the cache authority. A resolved catalog record stores the exact provider/model key, decimal prices per one million tokens, nullable cached-input price, currency, source URL/label, catalog version, effective date, last verification time, expiry, and source kind. `AiPricing:FreshnessDays` defaults to exactly `7`.

A normal read uses a fresh cached value. A stale read attempts resolution and preserves the prior value with a stale/refresh-failed status if every source fails. **Refresh pricing** bypasses freshness but follows the same replace-on-success rule. A manual override is never replaced by ordinary catalog refresh.

## Bundled catalog

The reviewed catalog is embedded from `src/IntakeGate.Infrastructure/Ai/Pricing/ai-model-pricing-catalog.v1.json`. Version `2026-09-19.1` contains only exact model IDs whose standard first-party API token prices were verified on 2026-09-19:

- OpenAI: `gpt-6-astra`, `gpt-5.6-sol`, `gpt-5.6-terra`, and `gpt-5.6-luna`.
- Anthropic: `claude-fable-5-1`, `claude-opus-5`, `claude-sonnet-5`, and `claude-haiku-4-5-20251001`.

Sources:

- [OpenAI API pricing](https://developers.openai.com/api/docs/pricing) — standard, short-context text-token prices.
- [Anthropic Claude API pricing](https://platform.claude.com/docs/en/about-claude/pricing) — base input, cache-hit, and output prices.
- [Anthropic model IDs](https://platform.claude.com/docs/en/models/overview).

The application does not scrape those pages and does not assume the provider Models APIs return pricing. Updating prices is a reviewed catalog change: verify the exact model ID, currency, units, pricing mode, cache dimension, source, and effective date; update the catalog version; then rerun pricing, persistence, API, and UI tests. If any value is uncertain, omit the model so the UI reports pricing unavailable.

## Runtime cost accounting

The runtime uses this same service and SQLite cache; it does not maintain a second pricing table or TTL. Each provider attempt is retained as a separate interaction so successful calls, failed calls that report usage, and retries are never collapsed before costing. Provider/model selection uses provider-reported actual metadata first, the provider/model used to send the request second, and the captured profile configuration only as a final fallback.

For every interaction with token usage and a USD quote, fixed-point decimal arithmetic calculates:

```text
input tokens / 1,000,000 × input price per million
+ output tokens / 1,000,000 × output price per million
```

The audit snapshot persists requested and costing provider/model identifiers, usage, input/output/total estimate components, pricing source and catalog version, effective/verification timestamps, and whether stale cached pricing was used. Pricing failure, unsupported providers/models, and missing usage never change an otherwise valid evaluation result.

Evaluation cost is the sum of its provider interactions. Run cost is the sum of its evaluations. Home uses persisted evaluation estimates as its single authoritative aggregation path within the existing half-open 7/30/90-day window, so run and evaluation totals are never added together.

Cost coverage has three distinct states:

- **known**, including a true `$0.00 USD`;
- **unknown**, shown as `—`, when no interaction can be costed;
- **partial**, where the known amount is shown with an explicit coverage indicator.

Completed historical records are never repriced or backfilled. Opening a run or changing the Home summary window reads persisted snapshots only and never invokes a provider or pricing source.

## Deliberate boundaries

The displayed value is an estimate and does not guarantee provider billing. The current model intentionally does not include negotiated enterprise pricing, batch, cache-write, long-context, regional, tool, marketplace, invoice, tax, or currency-conversion charges. Cached-input pricing remains visible during Setup, but runtime cached-token accounting is deferred until provider usage exposes that category reliably. No administrator pricing override UI or API is included.

Legacy optional profile pricing fields remain import-compatible but are not the authority for new runtime estimates. New activity uses the shared model-pricing service described above; historical persisted estimates retain their original values.
