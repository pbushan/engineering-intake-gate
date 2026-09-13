using Xunit;

// SQLite connection-pool cleanup is process-wide; serial execution keeps restart/key-loss tests
// deterministic while they intentionally remove durable files.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
