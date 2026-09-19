# AI model pricing

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

The application does not scrape those pages and does not assume the provider Models APIs return pricing. Updating prices is a reviewed catalog change: verify the exact model ID, currency, units, pricing mode, cache dimension, source, and effective date; update the catalog version; then rerun pricing, migration, API, and UI tests. If any value is uncertain, omit the model so the UI reports pricing unavailable.

## Deliberate boundaries

The displayed value is an estimate and does not guarantee provider billing. The current schema intentionally does not model batch, cache-write, long-context, regional, tool, marketplace, or invoice charges. Those dimensions require explicit future contracts rather than inference from the base price.

This discovery cache is not yet an input to run/evaluation cost accounting. Existing optional profile pricing remains the authority for persisted run estimates, preserving historical behavior until a separately reviewed cost-accounting change is made.
