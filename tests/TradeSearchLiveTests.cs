using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EliteStudio.Market;

namespace EliteStudio.Tests;

/// <summary>
/// The one test that goes out to the internet: the Haul Overlay's own self-test and Studio's search,
/// one right after the other, from the same systems with the same settings. It is skipped unless
/// the environment variable ELITE_STUDIO_LIVE is 1, the old tool is still in Documents, and the
/// game is not running. To see the comparison it prints, run the test program itself:
/// <code>
/// $env:ELITE_STUDIO_LIVE = '1'
/// &amp; "$env:USERPROFILE\.elite-studio\build\&lt;checkout folder&gt;\bin\EliteStudio.Tests\debug\EliteStudio.Tests.exe" -class EliteStudio.Tests.TradeSearchLiveTests -showLiveOutput
/// </code>
/// Prices move by the minute and the two searches run seconds apart, so the test is strict where
/// both saw the same prices (every number of such a trade must agree) and lenient about which
/// trades made the list.
/// </summary>
public class TradeSearchLiveTests(ITestOutputHelper output)
{
    static readonly string[] Systems = ["Sol", "Agni", "LHS 3447"];

    // Hold, jump ranges and pad are pinned so that neither side depends on the ship in the journal or on saved settings.
    static readonly SearchRequest Pinned = new()
    {
        Mode = "near", Pad = 3, BuyLy = 15, MaxLy = 30, MaxAgeHours = 48, MaxStarLs = 5000, MinSupply = 100, MinDemand = 100,
        Carriers = false, Surface = "odyssey", Cargo = 400, RankBy = "hour", Returns = true, Jump = 14, JumpEmpty = 27,
    };

    const string PinnedForOldTool =
        "startMode=near;rankBy=hour;returns=true;jumpRange=14;jumpEmpty=27;pad=L;buyLy=15;maxLy=30;maxAgeHours=48;maxStarLs=5000;" +
        "minSupply=100;minDemand=100;carriers=false;surface=odyssey;cargoOverride=400;srcPower=any;dstPower=any;srcState=any;dstState=any";

    static readonly string[] TextColumns = ["CommodityId", "BuyStation", "BuySystem", "SellStation", "SellSystem", "ReturnId", "BuyPower", "SellPower"];

    [Fact]
    public async Task Live_the_same_trades_as_the_haul_overlay()
    {
        string oldTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Elite Haul Overlay", "app", "overlay.ps1");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ELITE_STUDIO_LIVE") == "1", "Set ELITE_STUDIO_LIVE=1 to compare with the Haul Overlay over the internet.");
        Assert.SkipUnless(File.Exists(oldTool), "The Haul Overlay is not at " + oldTool);
        Assert.SkipWhen(Process.GetProcessesByName("EliteDangerous64").Length > 0, "Not while the game is running.");

        var report = new StringBuilder();
        var problems = new List<string>();
        foreach (string system in Systems)
        {
            OldRun old = OldRun.Parse(await RunOldTool(oldTool, system));
            // A client of its own for every system, as each self-test is a program of its own: with one for
            // all three, Studio would count one request fewer from the second system on (it remembers the price overview).
            using var data = new MarketData(null);
            var search = new TradeSearch(data);
            SearchResult ours = await search.Run(Pinned with { StarSystem = system }, TestContext.Current.CancellationToken);
            Compare(system, old, ours, report, problems);
        }
        output.WriteLine(report.ToString());
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems) + Environment.NewLine + report);
    }

    static void Compare(string system, OldRun old, SearchResult ours, StringBuilder report, List<string> problems)
    {
        void Problem(string what) => problems.Add($"{system}: {what}");
        static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        report.AppendLine(CultureInfo.InvariantCulture, $"== {system} ==")
            .AppendLine(CultureInfo.InvariantCulture, $"Haul Overlay: ok={old.Ok} {old.Error} buyable={old.Buyable} scanned={old.Scanned} candidates={old.Candidates} trades={old.Trades} requests={old.Requests}")
            .AppendLine(CultureInfo.InvariantCulture, $"Elite Studio: ok={ours.Ok} {ours.Error} buyable={ours.Exports} scanned={ours.Scanned} candidates={ours.Candidates} trades={ours.Trades.Count} requests={ours.Urls.Count}");
        if (!old.Ok || !ours.Ok) { Problem("a search failed"); return; }

        // The price overview changes once a day, so both scan the same commodities; the rest follows live prices.
        if (old.Scanned != ours.Scanned) Problem($"scanned {old.Scanned} against {ours.Scanned} commodities");
        if (Math.Abs(old.Candidates - ours.Candidates) > 3) Problem($"looked closely at {old.Candidates} against {ours.Candidates} commodities");
        if (Math.Abs(old.Buyable - ours.Exports) > 2) Problem($"{old.Buyable} against {ours.Exports} commodities on sale in the system");
        if (Math.Abs(old.Trades - ours.Trades.Count) > 5) Problem($"{old.Trades} against {ours.Trades.Count} trades");
        if (Math.Abs(old.Requests - ours.Urls.Count) > Math.Max(10, old.Requests / 10)) Problem($"{old.Requests} against {ours.Urls.Count} requests");

        int found = 0, same = 0;
        for (int i = 0; i < old.Rows.Count; i++)
        {
            Dictionary<string, string> theirs = old.Rows[i];
            long Whole(string column) => long.Parse(theirs[column], CultureInfo.InvariantCulture);
            double Decimal(string column) => double.Parse(theirs[column].Replace(',', '.'), CultureInfo.InvariantCulture);
            string trade = $"{theirs["CommodityId"]}: {theirs["BuyStation"]} ({theirs["BuySystem"]}) -> {theirs["SellStation"]} ({theirs["SellSystem"]})";
            TradeRow? mine = ours.Trades.FirstOrDefault(row =>
                row.CommodityId == theirs["CommodityId"] && row.BuyStation == theirs["BuyStation"] && row.BuySystem == theirs["BuySystem"]
                && row.SellStation == theirs["SellStation"] && row.SellSystem == theirs["SellSystem"]);
            string verdict;
            if (mine is null)
            {
                verdict = "not in Studio's list";
            }
            else
            {
                found++;
                var moved = new List<string>();
                void Check(string what, double theirNumber, double myNumber, List<string> differences)
                {
                    if (Math.Abs(theirNumber - myNumber) > 1e-9) differences.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {theirNumber} -> {myNumber}"));
                }
                Check("buy price", Whole("BuyPrice"), mine.BuyPrice, moved);
                Check("stock", Whole("Stock"), mine.Stock, moved);
                Check("sell price", Whole("SellPrice"), mine.SellPrice, moved);
                Check("demand", Whole("Demand"), mine.Demand, moved);
                var wrong = new List<string>();
                if (moved.Count == 0)
                {
                    // the same prices on both sides: everything worked out from them must agree
                    Check("from you", Decimal("FromYou"), mine.BuyDistanceLy, wrong);
                    Check("distance", Decimal("DistanceLy"), mine.DistanceLy, wrong);
                    Check("jumps out", Whole("JOut"), mine.JumpsOut, wrong);
                    Check("profit per ton", Whole("ProfitPerTon"), mine.ProfitPerTon, wrong);
                    Check("tons", Whole("Units"), mine.Units, wrong);
                    Check("profit per trip", Whole("ProfitPerTrip"), mine.ProfitPerTrip, wrong);
                    if (theirs["ReturnId"] == mine.ReturnId && Whole("RoundTripProfit") == mine.RoundTripProfit)
                    {
                        Check("jumps back", Whole("JBack"), mine.JumpsBack, wrong);
                        Check("minutes", Decimal("Minutes"), mine.Minutes, wrong);
                        if (Math.Abs(Whole("PerHour") - mine.PerHour) > 1) wrong.Add($"per hour {Whole("PerHour")} -> {mine.PerHour}");
                        if (wrong.Count == 0) same++;
                    }
                    else
                    {
                        moved.Add($"load back {theirs["ReturnId"]} {Number(Whole("RoundTripProfit"))} -> {mine.ReturnId} {Number(mine.RoundTripProfit)}");
                    }
                }
                if (wrong.Count > 0) Problem($"{trade}: same prices, other numbers: {string.Join(", ", wrong)}");
                verdict = wrong.Count > 0 ? "WRONG: " + string.Join(", ", wrong)
                    : moved.Count > 0 ? $"Studio's #{mine.Rank}, prices moved: {string.Join(", ", moved)}"
                    : $"Studio's #{mine.Rank}, every number the same ({Number(mine.PerHour)} cr/h, {mine.Minutes.ToString(CultureInfo.InvariantCulture)} min)";
            }
            report.AppendLine(CultureInfo.InvariantCulture, $"  {i + 1,2} {trade}: {verdict}");
        }
        report.AppendLine(CultureInfo.InvariantCulture, $"  {found} of the old tool's first {old.Rows.Count} trades are in Studio's list, {same} with every number the same.").AppendLine();
        if (old.Rows.Count >= 5 && found * 10 < old.Rows.Count * 7) Problem($"only {found} of the old tool's first {old.Rows.Count} trades are in Studio's list");
    }

    static async Task<string> RunOldTool(string script, string system)
    {
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-SelfTest", "-System", system, "-Override", PinnedForOldTool])
            start.ArgumentList.Add(argument);
        using Process tool = Process.Start(start)!;
        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            Task<string> printed = tool.StandardOutput.ReadToEndAsync(patience.Token);
            Task<string> complaints = tool.StandardError.ReadToEndAsync(patience.Token);
            await tool.WaitForExitAsync(patience.Token);
            return await printed + await complaints;
        }
        catch (OperationCanceledException)
        {
            tool.Kill(entireProcessTree: true);
            throw new TimeoutException($"The Haul Overlay's self-test for {system} did not finish in four minutes.");
        }
    }

    /// <summary>What the old tool's self-test printed: its "Search:" line and the table of its first 14 trades.</summary>
    sealed record OldRun(bool Ok, string Error, int Buyable, int Scanned, int Candidates, int Trades, int Requests, List<Dictionary<string, string>> Rows)
    {
        public static OldRun Parse(string printed)
        {
            Match line = Regex.Match(printed, @"^Search: ok=(True|False) error=(.*?) buyable=(\d+) scanned=(\d+) candidates=(\d+) trades=(\d+) in \d+ ms, (\d+) requests", RegexOptions.Multiline);
            Assert.True(line.Success, "The Haul Overlay printed no search line:" + Environment.NewLine + printed);
            int Count(int group) => int.Parse(line.Groups[group].Value, CultureInfo.InvariantCulture);

            string[] lines = [.. printed.Split('\n').Select(text => text.TrimEnd('\r'))];
            var rows = new List<Dictionary<string, string>>();
            int titles = Array.FindIndex(lines, text => text.StartsWith("CommodityId", StringComparison.Ordinal));
            if (titles >= 0)
            {
                // The line of dashes under the titles shows where every title, and so every column, sits.
                (int Start, int End, string Title)[] columns =
                    [.. Regex.Matches(lines[titles + 1], "-+").Select(dashes => (dashes.Index, dashes.Index + dashes.Length, lines[titles].Substring(dashes.Index, dashes.Length).Trim()))];
                for (int i = titles + 2; i < lines.Length && lines[i].Trim().Length > 0; i++) rows.Add(Cells(lines[i].PadRight(columns[^1].End), columns));
            }
            return new OldRun(line.Groups[1].Value == "True", line.Groups[2].Value, Count(3), Count(4), Count(5), Count(6), Count(7), rows);
        }

        // A number ends flush right under its title and holds no spaces. A name starts flush left under its
        // title, may hold spaces, and runs on until the next column begins.
        static Dictionary<string, string> Cells(string line, (int Start, int End, string Title)[] columns)
        {
            int NumberStart(int end)
            {
                int start = end;
                while (start > 0 && line[start - 1] != ' ') start--;
                return start;
            }
            var cells = new Dictionary<string, string>();
            for (int i = 0; i < columns.Length; i++)
            {
                bool name = TextColumns.Contains(columns[i].Title);
                int start = name ? columns[i].Start : NumberStart(columns[i].End);
                int end = !name ? columns[i].End
                    : i + 1 == columns.Length ? line.Length
                    : TextColumns.Contains(columns[i + 1].Title) ? columns[i + 1].Start : NumberStart(columns[i + 1].End);
                cells[columns[i].Title] = line[start..Math.Max(start, end)].Trim();
            }
            return cells;
        }
    }
}
