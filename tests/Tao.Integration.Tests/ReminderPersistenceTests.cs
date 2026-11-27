using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Tao.Application;
using Tao.Domain;
using Tao.Infrastructure;
using Tao.Infrastructure.Push;
using Xunit;

namespace Tao.Integration.Tests;

/// <summary>
/// Reminders and push subscriptions on real PostgreSQL with the restricted FORCE-RLS API role and the
/// narrow worker role. All keys and addresses are synthetic.
/// </summary>
public sealed class ReminderPersistenceTests
{
    private sealed record Databases(NpgsqlDataSource Admin, NpgsqlDataSource App, NpgsqlDataSource Worker) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Admin.DisposeAsync();
            await App.DisposeAsync();
            await Worker.DisposeAsync();
        }
    }

    private static async Task<Databases> DatabasesAsync()
    {
        var adminCs = Environment.GetEnvironmentVariable("TAO_TEST_DB");
        var appCs = Environment.GetEnvironmentVariable("TAO_TEST_APP_DB");
        var workerCs = Environment.GetEnvironmentVariable("TAO_TEST_RETENTION_DB");
        Assert.False(string.IsNullOrWhiteSpace(adminCs));
        Assert.False(string.IsNullOrWhiteSpace(appCs));
        Assert.False(string.IsNullOrWhiteSpace(workerCs));
        var admin = NpgsqlDataSource.Create(adminCs!);
        await new PostgresMigrator(admin, "tao_app", "tao_retention").MigrateAsync();
        var app = NpgsqlDataSource.Create(appCs!);
        await PostgresRuntimeGuard.VerifyAsync(app);
        var worker = NpgsqlDataSource.Create(workerCs!);
        await PostgresRetentionRepository.VerifyWorkerRoleAsync(worker);
        return new Databases(admin, app, worker);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static ReminderDraft Draft(string text, ReminderRepeat repeat = ReminderRepeat.Daily) =>
        new(text, repeat, new TimeOnly(9, 0), repeat == ReminderRepeat.Once ? DateOnly.FromDateTime(Now.UtcDateTime.AddDays(3)) : null, "Europe/Rome");

    private static string Key(int length) =>
        PushSubscriptionRules.Encode(new byte[length].Select((_, i) => i == 0 ? (byte)4 : (byte)7).ToArray());

    private static PushSubscriptionInput Input(string name) =>
        new($"https://fcm.googleapis.com/fcm/send/{name}", Key(65), Key(16));

    [Fact]
    public async Task RemindersAreSealedActorScopedCappedAndBoundToTheirRow()
    {
        await using var db = await DatabasesAsync();
        var crypto = TestEnvelopeCrypto.Create();
        var store = new PostgresReminderStore(db.App, crypto);
        var tenant = "rem-" + Guid.NewGuid().ToString("N");

        var first = await store.AddAsync(tenant, "alice", Draft("Chiama Marco per il preventivo ACME"), Now.AddHours(2), default);
        var second = await store.AddAsync(tenant, "alice", Draft("Dentista", ReminderRepeat.Once), Now.AddDays(3), default);
        var bobs = await store.AddAsync(tenant, "bob", Draft("Pagare l'affitto"), Now.AddHours(1), default);

        Assert.Equal(["Chiama Marco per il preventivo ACME", "Dentista"],
            (await store.ListAsync(tenant, "alice", default)).Select(r => r.Text));
        Assert.Equal(["Pagare l'affitto"], (await store.ListAsync(tenant, "bob", default)).Select(r => r.Text));
        var reread = await store.GetAsync(tenant, "alice", first.Id, default);
        Assert.Equal((ReminderRepeat.Daily, new TimeOnly(9, 0), "Europe/Rome", true), (reread!.Repeat, reread.At, reread.TimeZone, reread.Enabled));

        await using (var raw = db.Admin.CreateCommand(
            "SELECT string_agg(encode(sealed_text, 'escape'), '') FROM reminders WHERE tenant_id = @tenant"))
        {
            raw.Parameters.AddWithValue("tenant", tenant);
            var all = (string)(await raw.ExecuteScalarAsync())!;
            Assert.DoesNotContain("Marco", all);
            Assert.DoesNotContain("Dentista", all);
            Assert.DoesNotContain("affitto", all);
        }

        // Another actor can neither read, switch off, complete nor delete a reminder, even with its id.
        Assert.Null(await store.GetAsync(tenant, "bob", first.Id, default));
        Assert.False(await store.SetEnabledAsync(tenant, "bob", first.Id, false, null, default));
        Assert.False(await store.DeleteAsync(tenant, "bob", first.Id, default));
        await store.CompleteAsync(tenant, "bob", first.Id, Now, null, default);
        Assert.Equal(Now.AddHours(2), (await store.GetAsync(tenant, "alice", first.Id, default))!.NextRunAt!.Value, TimeSpan.FromMilliseconds(1));

        // A ciphertext moved to another row does not open: the AAD binds the reminder id.
        await using (var swap = db.Admin.CreateCommand(
            "UPDATE reminders SET sealed_text = (SELECT sealed_text FROM reminders WHERE id = @from) WHERE id = @to"))
        {
            swap.Parameters.AddWithValue("from", first.Id);
            swap.Parameters.AddWithValue("to", second.Id);
            Assert.Equal(1, await swap.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.GetAsync(tenant, "alice", second.Id, default));

        Assert.True(await store.SetEnabledAsync(tenant, "alice", first.Id, false, null, default));
        Assert.False((await store.GetAsync(tenant, "alice", first.Id, default))!.Enabled);
        Assert.True(await store.DeleteAsync(tenant, "alice", first.Id, default));
        Assert.Null(await store.GetAsync(tenant, "alice", first.Id, default));

        // The number of reminders is bounded per actor.
        for (var i = 0; i < ReminderRules.MaxPerActor; i++)
            await store.AddAsync(tenant, "carol", Draft("n" + i), Now.AddHours(1), default);
        await Assert.ThrowsAsync<TooManyRemindersException>(() => store.AddAsync(tenant, "carol", Draft("one too many"), Now, default));
        Assert.Equal(bobs.Id, Assert.Single(await store.ListAsync(tenant, "bob", default)).Id);
    }

    [Fact]
    public async Task DueRemindersAreClaimedOnceAndOnlyThroughTheWorkerRole()
    {
        await using var db = await DatabasesAsync();
        var store = new PostgresReminderStore(db.App, TestEnvelopeCrypto.Create());
        var claims = new PostgresReminderClaims(db.Worker);
        var tenant = "claim-" + Guid.NewGuid().ToString("N");
        // Drain whatever other tests left due, so the assertions below are about this test's rows.
        while ((await claims.ClaimDueAsync(200, TimeSpan.FromMinutes(5), default)).Count > 0) { }

        var due = await store.AddAsync(tenant, "alice", Draft("scaduto"), Now.AddMinutes(1), default);
        var bobsDue = await store.AddAsync(tenant, "bob", Draft("anche bob"), Now.AddMinutes(1), default);
        var later = await store.AddAsync(tenant, "alice", Draft("domani"), Now.AddHours(1), default);
        var off = await store.AddAsync(tenant, "alice", Draft("spento"), Now.AddMinutes(1), default);
        await store.SetEnabledAsync(tenant, "alice", off.Id, false, null, default);
        foreach (var (owner, id) in new[] { ("alice", due.Id), ("bob", bobsDue.Id), ("alice", off.Id) })
            await using (var actor = await PostgresActorScope.OpenAsync(db.App, tenant, owner, default))
            {
                await using var past = new NpgsqlCommand(
                    "UPDATE reminders SET next_run_at = now() - interval '1 minute' WHERE id = @id", actor.Connection, actor.Transaction);
                past.Parameters.AddWithValue("id", id);
                await past.ExecuteNonQueryAsync();
                await actor.CommitAsync(default);
            }

        var claimed = await claims.ClaimDueAsync(200, TimeSpan.FromMinutes(5), default);
        Assert.Equal(new[] { (tenant, "alice", due.Id), (tenant, "bob", bobsDue.Id) }.OrderBy(x => x.Item3),
            claimed.Where(c => c.TenantId == tenant).Select(c => (c.TenantId, c.OwnerSubject, c.Id)).OrderBy(x => x.Id));
        Assert.DoesNotContain(claimed, c => c.Id == later.Id || c.Id == off.Id);
        // A reminder that is being handled is not handed out again until its claim expires.
        Assert.DoesNotContain(await claims.ClaimDueAsync(200, TimeSpan.FromMinutes(5), default), c => c.TenantId == tenant);
        await using (var expire = db.Admin.CreateCommand(
            "UPDATE reminders SET claimed_until = now() - interval '1 second' WHERE id = @id"))
        {
            expire.Parameters.AddWithValue("id", due.Id);
            await expire.ExecuteNonQueryAsync();
        }
        Assert.Equal([due.Id], (await claims.ClaimDueAsync(200, TimeSpan.FromMinutes(5), default)).Where(c => c.TenantId == tenant).Select(c => c.Id));

        // Completing a run schedules the next one and releases the claim.
        await store.CompleteAsync(tenant, "alice", due.Id, Now, Now.AddDays(1), default);
        var done = await store.GetAsync(tenant, "alice", due.Id, default);
        Assert.Equal(Now.AddDays(1), done!.NextRunAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.NotNull(done.LastSentAt);

        // The worker has no way to read reminders and the API role cannot claim across actors.
        await using (var direct = db.Worker.CreateCommand("SELECT count(*) FROM reminders"))
            await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteScalarAsync());
        await using (var forbidden = db.App.CreateCommand("SELECT * FROM public.tao_claim_due_reminders(10, 60)"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteScalarAsync())).SqlState);
        await using (var unscoped = db.App.CreateCommand("SELECT (SELECT count(*) FROM reminders) + (SELECT count(*) FROM push_subscriptions)"))
            Assert.Equal(0L, (long)(await unscoped.ExecuteScalarAsync())!);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => claims.ClaimDueAsync(0, TimeSpan.FromMinutes(5), default));
    }

    [Fact]
    public async Task PushSubscriptionsAreSealedBoundToTheActorUpdatedInPlaceAndCapped()
    {
        await using var db = await DatabasesAsync();
        var store = new PostgresPushSubscriptionStore(db.App, TestEnvelopeCrypto.Create());
        var tenant = "push-" + Guid.NewGuid().ToString("N");

        var phone = await store.UpsertAsync(tenant, "alice", Input("phone-endpoint-secret"), default);
        var again = await store.UpsertAsync(tenant, "alice", Input("phone-endpoint-secret"), default);
        await store.UpsertAsync(tenant, "bob", Input("phone-endpoint-secret"), default);
        Assert.Equal(phone.Id, again.Id);
        var listed = Assert.Single(await store.ListAsync(tenant, "alice", default));
        Assert.Equal((phone.Id, phone.Endpoint, Key(65), Key(16)), (listed.Id, listed.Endpoint, listed.P256dh, listed.Auth));

        await using (var raw = db.Admin.CreateCommand(
            "SELECT string_agg(encode(sealed_subscription, 'escape'), '') FROM push_subscriptions WHERE tenant_id = @tenant"))
        {
            raw.Parameters.AddWithValue("tenant", tenant);
            Assert.DoesNotContain("phone-endpoint-secret", (string)(await raw.ExecuteScalarAsync())!);
        }

        await store.RecordOutcomeAsync(tenant, "alice", phone.Id, false, default);
        await store.RecordOutcomeAsync(tenant, "alice", phone.Id, false, default);
        Assert.Equal(2, (await store.ListAsync(tenant, "alice", default)).Single().Failures);
        await store.RecordOutcomeAsync(tenant, "bob", phone.Id, true, default); // not bob's row: no effect on alice's
        await store.RecordOutcomeAsync(tenant, "alice", phone.Id, true, default);
        Assert.Equal(0, (await store.ListAsync(tenant, "alice", default)).Single().Failures);

        // The number of browsers is bounded: the oldest make room.
        for (var i = 0; i < PushSubscriptionRules.MaxPerActor + 2; i++)
            await store.UpsertAsync(tenant, "alice", Input("device-" + i), default);
        var kept = await store.ListAsync(tenant, "alice", default);
        Assert.Equal(PushSubscriptionRules.MaxPerActor, kept.Count);
        Assert.DoesNotContain(kept, s => s.Id == phone.Id);

        Assert.False(await store.RemoveAsync(tenant, "bob", kept[0].Id, default));
        Assert.True(await store.RemoveAsync(tenant, "alice", kept[0].Id, default));
        Assert.Single(await store.ListAsync(tenant, "bob", default));
    }

    [Fact]
    public async Task ExportListsTheRemindersAndDevicesButNeverTheKeysAndErasureRemovesThem()
    {
        await using var db = await DatabasesAsync();
        var crypto = TestEnvelopeCrypto.Create();
        var reminders = new PostgresReminderStore(db.App, crypto);
        var devices = new PostgresPushSubscriptionStore(db.App, crypto);
        var data = new PostgresPersonalDataService(db.App, crypto);
        var tenant = "rem-export-" + Guid.NewGuid().ToString("N");
        await reminders.AddAsync(tenant, "alice", Draft("Chiama il commercialista"), Now.AddHours(2), default);
        await reminders.AddAsync(tenant, "bob", Draft("Segreto di Bob"), Now.AddHours(2), default);
        await devices.UpsertAsync(tenant, "alice", Input("alice-device"), default);
        await devices.UpsertAsync(tenant, "bob", Input("bob-device"), default);

        await using (var output = new MemoryStream())
        {
            await data.WriteExportAsync(tenant, "alice", output, default);
            var text = Encoding.UTF8.GetString(output.ToArray());
            Assert.DoesNotContain("Bob", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alice-device", text);
            Assert.DoesNotContain(Key(65), text);
            using var json = JsonDocument.Parse(text);
            var reminder = Assert.Single(json.RootElement.GetProperty("reminders").EnumerateArray());
            Assert.Equal(("Chiama il commercialista", "daily", "09:00", "Europe/Rome"), (
                reminder.GetProperty("text").GetString(), reminder.GetProperty("repeat").GetString(),
                reminder.GetProperty("time").GetString(), reminder.GetProperty("timeZone").GetString()));
            var device = Assert.Single(json.RootElement.GetProperty("pushSubscriptions").EnumerateArray());
            Assert.Equal(("fcm.googleapis.com", "sealed-not-exported"),
                (device.GetProperty("service").GetString(), device.GetProperty("keys").GetString()));
        }

        await data.EraseAsync(tenant, "alice", default);
        Assert.Empty(await reminders.ListAsync(tenant, "alice", default));
        Assert.Empty(await devices.ListAsync(tenant, "alice", default));
        Assert.Single(await reminders.ListAsync(tenant, "bob", default));
        Assert.Single(await devices.ListAsync(tenant, "bob", default));
    }

    [Fact]
    public async Task RetentionRemovesFinishedOneOffRemindersAndDeadSubscriptionsOnly()
    {
        await using var db = await DatabasesAsync();
        var crypto = TestEnvelopeCrypto.Create();
        var reminders = new PostgresReminderStore(db.App, crypto);
        var devices = new PostgresPushSubscriptionStore(db.App, crypto);
        var claims = new PostgresReminderClaims(db.Worker);
        while (await claims.PurgeStaleAsync(500, default) > 0) { }
        var tenant = "rem-ret-" + Guid.NewGuid().ToString("N");

        var finished = await reminders.AddAsync(tenant, "alice", Draft("finito", ReminderRepeat.Once), null, default);
        var recent = await reminders.AddAsync(tenant, "alice", Draft("recente", ReminderRepeat.Once), null, default);
        var daily = await reminders.AddAsync(tenant, "alice", Draft("ogni giorno"), null, default);
        var dead = await devices.UpsertAsync(tenant, "alice", Input("dead"), default);
        var healthy = await devices.UpsertAsync(tenant, "alice", Input("healthy"), default);
        await using (var age = db.Admin.CreateCommand("""
            UPDATE reminders SET created_at = now() - interval '31 days' WHERE id = ANY(@old);
            UPDATE push_subscriptions SET failures = 5 WHERE id = @dead
            """))
        {
            age.Parameters.AddWithValue("old", new[] { finished.Id, daily.Id });
            age.Parameters.AddWithValue("dead", dead.Id);
            await age.ExecuteNonQueryAsync();
        }

        Assert.Equal(2, await claims.PurgeStaleAsync(500, default));
        Assert.Equal(new[] { recent.Id, daily.Id }.Order(), (await reminders.ListAsync(tenant, "alice", default)).Select(r => r.Id).Order());
        Assert.Equal([healthy.Id], (await devices.ListAsync(tenant, "alice", default)).Select(s => s.Id));
        await using var forbidden = db.App.CreateCommand("SELECT public.tao_purge_stale_notifications(10)");
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteScalarAsync())).SqlState);
    }
}
