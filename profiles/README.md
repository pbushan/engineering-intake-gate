# Sample profile fixtures

`profiles/example/` and `profiles/alternate/` are synthetic fixtures for documentation, deterministic tests, and the explicit one-time legacy-import path. They are not product defaults and are never runtime configuration authority. The alternate fixture exists to exercise portability and internal release-gate behavior; it does not authorize or expose Production mode in Product Compose.

For a normal installation, complete the setup wizard. The validated profile and policy stored in SQLite are the sole authority for subsequent runs. A legacy import must name this fixture—or another exact file path—explicitly and is accepted only when the installation has no existing profile.
