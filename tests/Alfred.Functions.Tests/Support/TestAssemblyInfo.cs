using Xunit;

// Several tests read or temporarily set process-wide environment variables (Gmail link
// base URL, GitHub token for /evolve, Gmail account for the redirect page). Serial
// execution keeps those interactions race-free; the suite is pure in-memory work, so
// parallelism buys little anyway.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
