# Versioning

Engineering Intake Gate uses calendar-first release semantics with SemVer-compatible syntax:

```text
YYYY.M.PATCH
```

- `YYYY` is the calendar release year.
- `M` is the release month from `1` through `12`, without zero padding. `2026.9.0` is valid; `2026.09.0` is not valid SemVer because numeric identifiers cannot contain leading zeroes.
- `PATCH` is the monotonically increasing release number within that month, beginning at `0` and resetting when the month changes.
- Prereleases use normal SemVer identifiers, such as `2026.10.0-rc.1` or `2026.10.0-beta.1`.
- Git tags add the `v` prefix, such as `v2026.9.0`.

Examples: the first September 2026 release is `2026.9.0`, the second is `2026.9.1`, and the first October release is `2026.10.0`.

The calendar year is not a traditional SemVer compatibility major. Breaking and compatibility-relevant changes are stated explicitly in each release's notes. The root `VERSION` file is the single human-edited product-version authority; the build and release gate propagate or validate ecosystem-specific representations against it.

The `/openapi/v1.json` path and its `info.version` describe API contract major `v1`, not the product's calendar release, and therefore change only with the API-document versioning policy.
