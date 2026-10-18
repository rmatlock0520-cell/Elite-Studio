using System.Text.Json.Nodes;
using EliteStudio.Nav;

namespace EliteStudio.Tests;

/// <summary>
/// The 3D map's memory of checks (<see cref="CheckMemory"/>): one answer to "is this system recorded?" for every check
/// (target, route, Within reach, Nav scan), kept in heatmap\checks.json for as long as it is likely to hold. The clock
/// is the test's own, moved on by hand.
/// </summary>
public sealed class CheckMemoryTests : IDisposable
{
    readonly string temp = Directory.CreateTempSubdirectory("EliteStudioCheckMemory-").FullName;
    DateTime clock = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => Directory.Delete(temp, recursive: true);

    string File => Path.Combine(temp, CheckMemory.FileName);

    /// <summary>A start of Studio: the memory in the 3D map's folder, on the test's clock.</summary>
    CheckMemory Start() => new(temp) { Now = () => clock, HeldWait = TimeSpan.Zero };

    [Fact]
    public void An_answer_is_kept_by_the_systems_number_else_by_its_name_whatever_the_capitals_and_spaces()
    {
        CheckMemory memory = Start();
        Assert.Null(memory.Get("Stop 9", 7_000_009));

        CheckEntry kept = memory.Put("  Stop 9 ", 7_000_009, CheckMemory.Recorded, CheckMemory.BySpansh, "found by Spansh's system search");
        Assert.Equal(new CheckEntry("Stop 9", 7_000_009, CheckMemory.Recorded, clock, CheckMemory.BySpansh, "found by Spansh's system search", null), kept);
        Assert.Equal(kept, memory.Get("any name at all", 7_000_009));   // by its number
        Assert.Equal(kept, memory.Get("STOP   9", 0));                   // by its name, when no number is given
        Assert.Null(memory.Get("Stop 9", 123));                          // the same name with another number is another system

        // A system checked by its name alone (a catalogue name read off a picture), then by its number: kept once, under its number.
        memory.Put("HIP 2001", 0, CheckMemory.NotRecorded, CheckMemory.BySpansh, "not found by Spansh's system search");
        Assert.Equal((CheckMemory.NotRecorded, 0L), (memory.Get("hip 2001", 0)!.Result, memory.Get("hip 2001", 0)!.Id64));
        memory.Put("HIP 2001", 5223218694992, CheckMemory.Recorded, CheckMemory.ByDetailed, "in the detailed map");
        Assert.Equal(2, memory.Count);
        Assert.Equal((CheckMemory.Recorded, 5223218694992L), (memory.Get("hip 2001", 0)!.Result, memory.Get("hip 2001", 0)!.Id64));

        // Checked by its name after its number was seen: the number is kept.
        clock += TimeSpan.FromMinutes(1);
        memory.Put("stop 9", 0, CheckMemory.Failed, null, null, "Spansh could not be reached.");
        Assert.Equal((CheckMemory.Failed, 7_000_009L, "Spansh could not be reached."), (memory.Get(null, 7_000_009)!.Result, memory.Get(null, 7_000_009)!.Id64, memory.Get(null, 7_000_009)!.Reason));
        Assert.Equal(2, memory.Count);

        Assert.Throws<ArgumentException>(() => memory.Put(" ", 5, CheckMemory.Recorded, null, null));
        Assert.Throws<ArgumentException>(() => memory.Put("Sol", 5, "maybe", null, null));
    }

    [Fact]
    public void Each_kind_of_answer_is_kept_as_long_as_it_is_likely_to_hold()
    {
        CheckMemory memory = Start();
        Assert.Equal(1, memory.RecordedDays);   // the owner's choice of 15 October 2026
        memory.Put("Recorded One", 1, CheckMemory.Recorded, CheckMemory.BySpansh, "found by Spansh's system search");
        memory.Put("Not Recorded One", 2, CheckMemory.NotRecorded, CheckMemory.BySpansh, "not found by Spansh's system search");
        memory.Put("Failed One", 3, CheckMemory.Failed, CheckMemory.BySpansh, null, "Spansh could not be reached.");
        DateTime at = clock;
        bool Kept(long id64) => memory.Get(null, id64) is not null;

        // Could not be checked: ten minutes.
        clock = at + TimeSpan.FromMinutes(9.9);
        Assert.Equal((true, true, true), (Kept(1), Kept(2), Kept(3)));
        clock = at + TimeSpan.FromMinutes(10);
        Assert.Equal((true, true, false), (Kept(1), Kept(2), Kept(3)));

        // Not recorded: a day, after which the next sighting checks once more. Recorded: a day too, unless the player chose longer.
        clock = at + TimeSpan.FromHours(23.9);
        Assert.Equal((true, true), (Kept(1), Kept(2)));
        clock = at + TimeSpan.FromHours(24);
        Assert.Equal((false, false), (Kept(1), Kept(2)));

        // 7 or 30 days apply to what is kept already.
        memory.RecordedDays = 7;
        Assert.True(Kept(1));
        clock = at + TimeSpan.FromDays(6.9);
        Assert.True(Kept(1));
        clock = at + TimeSpan.FromDays(7);
        Assert.False(Kept(1));
        memory.RecordedDays = 30;
        clock = at + TimeSpan.FromDays(29.9);
        Assert.Equal((true, false, false), (Kept(1), Kept(2), Kept(3)));
        clock = at + TimeSpan.FromDays(30);
        Assert.False(Kept(1));

        // Nothing else is offered.
        ArgumentException wrong = Assert.Throws<ArgumentException>(() => memory.RecordedDays = 3);
        Assert.StartsWith(CheckMemory.NoSuchDays, wrong.Message, StringComparison.Ordinal);
        Assert.Equal(30, memory.RecordedDays);
    }

    [Fact]
    public async Task The_answers_are_kept_on_disk_for_the_next_start_and_those_out_of_date_are_left_out()
    {
        CheckMemory memory = Start();
        memory.Put("Stop 9", 7_000_009, CheckMemory.Recorded, CheckMemory.BySpansh, "found by Spansh's system search");
        memory.Put("Col 285 Sector WO-J a38-0", 0, CheckMemory.NotRecorded, CheckMemory.BySpansh, "not found by Spansh's system search");
        memory.Put("Failed One", 3, CheckMemory.Failed, null, null, "Studio could not check this system.");
        Assert.False(System.IO.File.Exists(File));   // written by the save, once for them all
        await memory.SaveAsync();

        // One line, the numbers as text (they can be larger than a web page holds exactly).
        string text = System.IO.File.ReadAllText(File);
        Assert.Contains("\"id64\":\"7000009\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', text);
        Assert.Equal(3, JsonNode.Parse(text)!.AsArray().Count);
        Assert.False(System.IO.File.Exists(File + ".tmp"));

        // The next start of Studio knows them.
        CheckMemory next = Start();
        Assert.Equal((3, CheckMemory.Recorded, "found by Spansh's system search"), (next.Count, next.Get("stop 9", 0)!.Result, next.Get("stop 9", 0)!.How));
        Assert.Equal(CheckMemory.NotRecorded, next.Get("COL 285 SECTOR WO-J A38-0", 0)!.Result);

        // Eleven minutes on, the failure is out of date: the next write leaves it out. The file lists the oldest first
        // (those of one moment by name).
        clock += TimeSpan.FromMinutes(11);
        next.Put("Stop 10", 7_000_010, CheckMemory.Recorded, CheckMemory.ByDetailed, "in the detailed map");
        await next.SaveAsync();
        Assert.Equal(["Col 285 Sector WO-J a38-0", "Stop 9", "Stop 10"],
            JsonNode.Parse(System.IO.File.ReadAllText(File))!.AsArray().Select(entry => (string)entry!["name"]!));
        Assert.Equal(3, Start().Count);

        // A day on, only what is still fresh comes back, whatever the file holds.
        clock += TimeSpan.FromHours(23.9);
        Assert.Equal(["Stop 10"], new[] { "Stop 9", "Col 285 Sector WO-J a38-0", "Stop 10" }.Where(name => Start().Get(name, 0) is not null));
    }

    [Fact]
    public async Task A_damaged_file_is_moved_aside_never_written_over_and_the_memory_starts_afresh()
    {
        Directory.CreateDirectory(temp);
        System.IO.File.WriteAllText(File, "[{\"name\":\"Stop 9\",\"id64\":\"70000");   // cut short
        CheckMemory memory = Start();

        Assert.Null(memory.Get("Stop 9", 7_000_009));
        string aside = Assert.Single(Directory.GetFiles(temp, CheckMemory.FileName + ".unreadable-*"));
        Assert.Equal("[{\"name\":\"Stop 9\",\"id64\":\"70000", System.IO.File.ReadAllText(aside));   // kept as it was, for the owner to send in
        memory.Put("Stop 9", 7_000_009, CheckMemory.Recorded, CheckMemory.BySpansh, "found by Spansh's system search");
        await memory.SaveAsync();
        Assert.Equal(1, Start().Count);
        Assert.Equal("[{\"name\":\"Stop 9\",\"id64\":\"70000", System.IO.File.ReadAllText(aside));

        // An answer typed into the file by hand that Studio cannot use is passed over; the rest are kept.
        System.IO.File.WriteAllText(File, $$"""[{"name":"Odd","id64":"5","result":"perhaps","at":"{{clock:O}}"},null,{"name":"Fine","id64":"6","result":"recorded","at":"{{clock:O}}"}]""");
        Assert.Equal(1, Start().Count);
    }

    [Fact]
    public async Task A_file_another_program_holds_is_never_written_over_and_what_was_learnt_meanwhile_is_kept_with_it()
    {
        CheckMemory before = Start();
        before.Put("Stop 9", 7_000_009, CheckMemory.Recorded, CheckMemory.BySpansh, "found by Spansh's system search");
        await before.SaveAsync();
        string written = System.IO.File.ReadAllText(File);

        CheckMemory memory = Start();
        using (new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Held: nothing known from it yet, and nothing written over it.
            Assert.Null(memory.Get("Stop 9", 7_000_009));
            memory.Put("Stop 10", 7_000_010, CheckMemory.NotRecorded, CheckMemory.BySpansh, "not found by Spansh's system search");
            await memory.SaveAsync();
        }
        Assert.Equal(written, System.IO.File.ReadAllText(File));

        // Let go: read, with what was learnt meanwhile, and both written.
        Assert.Equal(CheckMemory.Recorded, memory.Get("Stop 9", 7_000_009)!.Result);
        await memory.SaveAsync();
        Assert.Equal(2, Start().Count);
    }

    [Fact]
    public async Task Forget_all_checks_empties_the_memory_and_its_file()
    {
        CheckMemory memory = Start();
        memory.Put("Stop 9", 7_000_009, CheckMemory.Recorded, CheckMemory.BySpansh, "found by Spansh's system search");
        memory.Put("Stop 10", 7_000_010, CheckMemory.Failed, CheckMemory.BySpansh, null, "Spansh could not be reached.");
        memory.Put("Stop 11", 7_000_011, CheckMemory.NotRecorded, CheckMemory.BySpansh, "not found by Spansh's system search");
        await memory.SaveAsync();
        clock += TimeSpan.FromMinutes(11);   // the failure is out of date: not counted

        Assert.Equal(2, memory.Forget());
        await memory.SaveAsync();

        Assert.Equal((0, (CheckEntry?)null), (memory.Count, memory.Get("Stop 9", 7_000_009)));
        Assert.Equal("[]", System.IO.File.ReadAllText(File));
        Assert.Equal(0, Start().Count);
    }
}
