using Xunit;

// The integration suite shares one disposable PostgreSQL database.
// Retention deliberately purges expired rows across tenants, so running it
// in parallel with legacy/expiry fixtures makes assertions nondeterministic.
// Concurrency properties are exercised explicitly within individual tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
