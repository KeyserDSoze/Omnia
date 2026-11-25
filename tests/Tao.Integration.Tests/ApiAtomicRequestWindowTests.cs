using Tao.Api;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// Synthetic deterministic admission contracts. Real HTTP+PostgreSQL integration
/// and real browser OIDC are separate CI release gates.
/// </summary>
public sealed class ApiAtomicRequestWindowTests
{
    [Fact]
    public void Tenant429DoesNotSpendActorQuotaOrCreateAnyNewActorWindow()
    {
        var clock = new ManualClock();
        var actors = new ApiActorRequestWindowLimiter(
            10, 2, clock, maxTrackedActorsPerTenant: 2);
        var tenants = new ApiTenantRequestWindowLimiter(1, 2, clock);
        var joint = new ApiAtomicRequestWindowLimiter(actors, tenants);

        Assert.True(joint.TryAcquire("account:a1", "tenant:A", out _));
        Assert.Equal(1, actors.TrackedActorPartitions);
        Assert.False(joint.TryAcquire("account:a2", "tenant:A", out var retry));
        Assert.Equal(60, retry);
        Assert.Equal(1, actors.TrackedActorPartitions);
        Assert.Equal(1, tenants.TrackedTenantPartitions);
        Assert.True(joint.TryAcquire("account:b1", "tenant:B", out _));
        Assert.Equal(2, actors.TrackedActorPartitions);
        Assert.False(joint.TryAcquire("account:c1", "tenant:C", out _));

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(joint.TryAcquire("account:a2", "tenant:A", out _));
        Assert.Equal(1, actors.TrackedActorPartitions);
        Assert.Equal(1, tenants.TrackedTenantPartitions);
    }

    [Fact]
    public void Actor429DoesNotConsumeAggregateTenantQuota()
    {
        var clock = new ManualClock();
        var actors = new ApiActorRequestWindowLimiter(1, 4, clock);
        var tenants = new ApiTenantRequestWindowLimiter(3, 2, clock);
        var joint = new ApiAtomicRequestWindowLimiter(actors, tenants);

        Assert.True(joint.TryAcquire("account:a1", "tenant:A", out _));
        Assert.False(joint.TryAcquire("account:a1", "tenant:A", out var retry));
        Assert.Equal(60, retry);
        Assert.True(joint.TryAcquire("account:a2", "tenant:A", out _));
        Assert.True(joint.TryAcquire("account:a3", "tenant:A", out _));
        Assert.False(joint.TryAcquire("account:a4", "tenant:A", out retry));
        Assert.Equal(60, retry);
        Assert.Equal(3, actors.TrackedActorPartitions);
        Assert.Equal(1, tenants.TrackedTenantPartitions);
    }

    [Fact]
    public async Task HundredsOfConcurrentSubjectsCannotOvergrantEitherWindow()
    {
        var clock = new ManualClock();
        var actors = new ApiActorRequestWindowLimiter(
            10, 16, clock, maxTrackedActorsPerTenant: 16);
        var tenants = new ApiTenantRequestWindowLimiter(7, 2, clock);
        var joint = new ApiAtomicRequestWindowLimiter(actors, tenants);

        var results = await Task.WhenAll(Enumerable.Range(0, 128)
            .Select(i => Task.Run(() =>
                joint.TryAcquire($"account:synthetic-{i}", "tenant:A", out _))));

        Assert.Equal(7, results.Count(x => x));
        Assert.Equal(7, actors.TrackedActorPartitions);
        Assert.Equal(1, tenants.TrackedTenantPartitions);
        Assert.False(joint.TryAcquire("account:extra", "tenant:A", out var retry));
        Assert.InRange(retry, 1, 60);
        Assert.True(joint.TryAcquire("account:another-tenant", "tenant:B", out _));
        Assert.Equal(8, actors.TrackedActorPartitions);
    }

    [Fact]
    public void TenantPartitionCapacityDenialCannotChargeAnOtherwiseAvailableActor()
    {
        var clock = new ManualClock();
        var actors = new ApiActorRequestWindowLimiter(10, 4, clock);
        var tenants = new ApiTenantRequestWindowLimiter(5, 1, clock);
        var joint = new ApiAtomicRequestWindowLimiter(actors, tenants);

        Assert.True(joint.TryAcquire("account:a", "tenant:A", out _));
        Assert.False(joint.TryAcquire("account:b", "tenant:B", out var retry));
        Assert.InRange(retry, 1, 60);
        Assert.Equal(1, actors.TrackedActorPartitions);
        Assert.Equal(1, tenants.TrackedTenantPartitions);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
