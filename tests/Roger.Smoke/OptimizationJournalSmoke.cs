using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Roger.Models;
using Roger.Services;

public static class OptimizationJournalSmoke
{
    public static Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Roger-journal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            VerifyRestartAndOrdering(directory);
            VerifyConflictsAndFailures(directory);
            VerifyPreconditionConflicts(directory);
            VerifyPersistenceGates(directory);
            VerifyCorruptAndBoundedRecords(directory);
            Console.WriteLine("PASS optimization journal: durable original values, restart recovery, write-ahead gates, latest-change ordering, conflict refusal, uncertain failures and bounded/corrupt records (fake OS only)");
        }
        finally { Directory.Delete(directory, recursive: true); }
        return Task.CompletedTask;
    }

    private static void VerifyRestartAndOrdering(string directory)
    {
        string file = Path.Combine(directory, "restart.json");
        var backend = new FakeBackend();
        var optimization = Service(backend);
        var journal = new OptimizationJournalService(optimization, file);
        Check(!File.Exists(file) && backend.Writes == 0, "constructing journal is read-only");
        backend.BeforeWrite = () =>
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var latest = json.RootElement.EnumerateArray().Last(e => e.GetProperty("Status").GetString() is "Pending" or "Restoring");
            Check(latest.GetProperty("Status").GetString() is "Pending" or "Restoring", "original/request must be durable before native write");
            Check(latest.GetProperty("BeforeValue").GetString() is not null, "pending entry contains original value");
        };
        var first = journal.ApplyPowerPlan(backend.B, "性能");
        Check(first.Status == OptimizationEntryStatus.Applied && first.BeforeValue == backend.A.ToString("D") && first.AfterValue == backend.B.ToString("D"), "persist exact before/after scheme");
        Check(first.BeforeText == "平衡" && first.AfterText == "性能", "readable saved scheme names");
        int writes = backend.Writes;
        journal = new OptimizationJournalService(optimization, file);
        Check(backend.Writes == writes && journal.LatestRestorable(OptimizationChangeKind.PowerPlan)?.Id == first.Id, "restart never automatically restores but keeps undo");
        var second = journal.ApplyPowerPlan(backend.C, "节能");
        Expect<InvalidOperationException>(() => journal.Restore(first.Id));
        Check(backend.Active == backend.C, "older entry cannot override a later change");
        Check(journal.Restore(second.Id).Status == OptimizationEntryStatus.Restored && backend.Active == backend.B, "newest change restores its own original value");
        Check(journal.Restore(first.Id).Status == OptimizationEntryStatus.Restored && backend.Active == backend.A, "earlier original can be restored after latest is undone");
        Check(journal.LatestRestorable(OptimizationChangeKind.PowerPlan) is null, "restored entries do not apply twice");
        var animation = journal.ApplyAnimations(false);
        journal = new OptimizationJournalService(optimization, file);
        Check(journal.Restore(animation.Id).Status == OptimizationEntryStatus.Restored && backend.Animations, "animation original survives restart");
        writes = backend.Writes;
        var unchanged = journal.ApplyAnimations(true);
        Check(unchanged.Status == OptimizationEntryStatus.NoChange && !unchanged.CanRestore && backend.Writes == writes, "same-value operation creates no native write or fake undo");
        var dns = journal.RecordDnsResult(OptimizationEntryStatus.Completed, "仅记录模拟 DNS 成功");
        Check(!dns.CanRestore, "DNS cache cannot be reconstructed");
        Expect<InvalidOperationException>(() => journal.Restore(dns.Id));
    }

    private static void VerifyConflictsAndFailures(string directory)
    {
        var backend = new FakeBackend();
        var journal = new OptimizationJournalService(Service(backend), Path.Combine(directory, "conflicts.json"));
        var entry = journal.ApplyPowerPlan(backend.B, "性能");
        backend.Active = backend.C; // Simulate an external app/user changing the setting.
        int writes = backend.Writes;
        var conflict = journal.Restore(entry.Id);
        Check(conflict.Status == OptimizationEntryStatus.Conflict && backend.Active == backend.C && backend.Writes == writes, "conflicting current setting is never overwritten");
        backend.Active = backend.B;
        Check(journal.Restore(entry.Id).Status == OptimizationEntryStatus.Restored && backend.Active == backend.A, "explicit retry can restore once current value matches again");
        backend.ThrowBeforeWrite = true;
        entry = journal.ApplyAnimations(false);
        Check(entry.Status == OptimizationEntryStatus.Failed && backend.Animations && !entry.CanRestore, "rejected native change is recorded without imaginary success");
        backend.ThrowBeforeWrite = false;
        backend.ThrowAfterWriteOnce = true;
        entry = journal.ApplyAnimations(false);
        Check(entry.Status == OptimizationEntryStatus.Uncertain && !backend.Animations && entry.CanRestore, "a native error after changing state remains visibly uncertain");
        Check(journal.Restore(entry.Id).Status == OptimizationEntryStatus.Restored && backend.Animations, "uncertain operation checks current value before explicit recovery");
        backend.FailRead = true;
        writes = backend.Writes;
        entry = journal.ApplyPowerPlan(backend.B, "性能");
        Check(entry.Status == OptimizationEntryStatus.Failed && entry.BeforeValue is null && backend.Writes == writes, "missing original value blocks native write");
    }

    private static void VerifyPersistenceGates(string directory)
    {
        var backend = new FakeBackend();
        var optimization = Service(backend);
        var blocked = new OptimizationJournalService(optimization, Path.Combine(directory, "blocked.json"), _ => throw new IOException("fixture write denied"));
        Expect<IOException>(() => blocked.ApplyAnimations(false));
        Check(backend.Writes == 0 && backend.Animations, "failure to store original value prevents native write");

        string file = Path.Combine(directory, "pending.json");
        var durable = new OptimizationJournalService(optimization, file);
        int stores = 0;
        var pending = new OptimizationJournalService(optimization, file, records =>
        {
            stores++;
            if (stores > 1) throw new IOException("fixture final write denied");
            // This test injects only storage failure; it still writes the exact durable pending state.
            var options = new JsonSerializerOptions();
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            File.WriteAllText(file, JsonSerializer.Serialize(records, options));
        });
        Expect<IOException>(() => pending.ApplyPowerPlan(backend.B, "性能"));
        Check(backend.Active == backend.B && pending.Entries[0].Status == OptimizationEntryStatus.Uncertain, "post-write store failure is not reported as unchanged or success");
        var reloaded = new OptimizationJournalService(optimization, file);
        Check(reloaded.Entries[0].Status == OptimizationEntryStatus.Pending && reloaded.LastLoadWarning is not null, "restart retains durable pending request and warning");
        Check(reloaded.Restore(reloaded.Entries[0].Id).Status == OptimizationEntryStatus.Restored && backend.Active == backend.A, "explicit pending recovery compares actual state and restores original");
    }

    private static void VerifyPreconditionConflicts(string directory)
    {
        var power = new FakeBackend();
        power.ReadingPower = count => { if (count == 2) power.Active = power.B; };
        var journal = new OptimizationJournalService(Service(power), Path.Combine(directory, "power-precondition.json"));
        var entry = journal.ApplyPowerPlan(power.C, "节能");
        Check(entry.Status == OptimizationEntryStatus.Failed && !entry.CanRestore && power.Active == power.B && power.Writes == 0,
            "power scheme changed after write-ahead read: conditional setter refuses native write");
        Check(entry.BeforeValue == power.A.ToString("D"), "journal never substitutes an unpersisted original after writing");

        var animations = new FakeBackend();
        animations.ReadingAnimations = count => { if (count == 2) animations.Animations = false; };
        journal = new OptimizationJournalService(Service(animations), Path.Combine(directory, "animation-precondition.json"));
        entry = journal.ApplyAnimations(false);
        Check(entry.Status == OptimizationEntryStatus.Failed && !entry.CanRestore && !animations.Animations && animations.Writes == 0,
            "animation changed after write-ahead read: conditional setter refuses native write even when it now equals target");

        power = new FakeBackend();
        journal = new OptimizationJournalService(Service(power), Path.Combine(directory, "restore-precondition.json"));
        entry = journal.ApplyPowerPlan(power.B, "性能");
        power.PowerReads = 0;
        power.ReadingPower = count => { if (count == 2) power.Active = power.C; };
        int writes = power.Writes;
        var restored = journal.Restore(entry.Id);
        Check(restored.Status == OptimizationEntryStatus.Conflict && power.Active == power.C && power.Writes == writes,
            "restore setter independently rejects a newly observed external scheme");

        animations = new FakeBackend();
        journal = new OptimizationJournalService(Service(animations), Path.Combine(directory, "restore-animation-precondition.json"));
        entry = journal.ApplyAnimations(false);
        animations.AnimationReads = 0;
        animations.ReadingAnimations = count => { if (count == 2) animations.Animations = true; };
        writes = animations.Writes;
        restored = journal.Restore(entry.Id);
        Check(restored.Status == OptimizationEntryStatus.Conflict && animations.Animations && animations.Writes == writes,
            "restore setter independently rejects a newly observed animation value");

        // Reproduce the review case: A is durable, B appears before Set(C), final log write fails.
        string file = Path.Combine(directory, "conflict-final-store-fails.json");
        power = new FakeBackend();
        power.ReadingPower = count => { if (count == 2) power.Active = power.B; };
        int stores = 0;
        journal = new OptimizationJournalService(Service(power), file, records =>
        {
            if (++stores > 1) throw new IOException("fixture conflict result store denied");
            var options = new JsonSerializerOptions();
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            File.WriteAllText(file, JsonSerializer.Serialize(records, options));
        });
        Expect<IOException>(() => journal.ApplyPowerPlan(power.C, "节能"));
        Check(power.Active == power.B && power.Writes == 0 && journal.Entries[0].Status == OptimizationEntryStatus.Failed,
            "failed result store cannot make a known precondition rejection write C or claim success");
        power.ReadingPower = null;
        var reloaded = new OptimizationJournalService(Service(power), file);
        restored = reloaded.Restore(reloaded.Entries[0].Id);
        Check(restored.Status == OptimizationEntryStatus.Conflict && power.Active == power.B && power.Writes == 0,
            "pending A-to-C record after failed result store cannot overwrite the externally selected B");
    }

    private static void VerifyCorruptAndBoundedRecords(string directory)
    {
        string file = Path.Combine(directory, "corrupt.json");
        const string corrupt = "{broken-json";
        File.WriteAllText(file, corrupt);
        var backend = new FakeBackend();
        var journal = new OptimizationJournalService(Service(backend), file);
        Check(journal.Entries.Count == 0 && journal.LastLoadWarning is not null && File.ReadAllText(file) == corrupt && backend.Writes == 0, "corruption never overwrites file or changes settings during load");
        journal.RecordDnsResult(OptimizationEntryStatus.Failed, "模拟记录");
        string preserved = Directory.GetFiles(directory, "corrupt.json.corrupt-*").Single();
        Check(File.ReadAllText(preserved) == corrupt, "invalid original is separately preserved before the next explicit operation");
        for (int i = 0; i < 205; i++) journal.RecordDnsResult(OptimizationEntryStatus.Completed, "离线有界记录 " + i);
        Check(journal.Entries.Count == 200 && new OptimizationJournalService(Service(backend), file).Entries.Count == 200, "persistent retention is bounded and survives reload");
        Check(backend.Writes == 0, "all journal-only operations remain read-only to Windows");
    }

    private static OptimizationService Service(FakeBackend backend) => new(backend, (_, _) => throw new InvalidOperationException("No real DNS command in journal tests"));
    private sealed class FakeBackend : OptimizationService.IOptimizationBackend
    {
        public Guid A { get; } = OptimizationService.BalancedSchemeId;
        public Guid B { get; } = new("b3e7a8e7-c9dd-4da4-9486-f77a7a33f7f0");
        public Guid C { get; } = new("9e59a4ad-034b-4433-b4ef-01b748f3456d");
        public Guid Active { get; set; } = OptimizationService.BalancedSchemeId;
        public bool Animations { get; set; } = true;
        public int Writes;
        public bool ThrowBeforeWrite, ThrowAfterWriteOnce, FailRead;
        public Action? BeforeWrite;
        public int PowerReads, AnimationReads;
        public Action<int>? ReadingPower, ReadingAnimations;
        public IReadOnlyList<OptimizationService.NativePowerPlan> EnumeratePowerPlans() => new[] { new OptimizationService.NativePowerPlan(A, "平衡"), new OptimizationService.NativePowerPlan(B, "性能"), new OptimizationService.NativePowerPlan(C, "节能") };
        public Guid GetActivePowerPlan() { ReadingPower?.Invoke(++PowerReads); return FailRead ? throw new Win32Exception(5) : Active; }
        public bool GetClientAreaAnimations() { ReadingAnimations?.Invoke(++AnimationReads); return FailRead ? throw new Win32Exception(5) : Animations; }
        public void SetActivePowerPlan(Guid id) { Before(); Active = id; After(); }
        public void SetClientAreaAnimations(bool enabled) { Before(); Animations = enabled; After(); }
        private void Before() { BeforeWrite?.Invoke(); if (ThrowBeforeWrite) throw new Win32Exception(5); Writes++; }
        private void After() { if (ThrowAfterWriteOnce) { ThrowAfterWriteOnce = false; throw new Win32Exception(5); } }
    }
    private static void Check(bool value, string detail) { if (!value) throw new InvalidOperationException("Optimization journal smoke: " + detail); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
