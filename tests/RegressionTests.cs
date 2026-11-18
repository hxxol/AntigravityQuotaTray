using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using CodexQuotaTray;
using CodexQuotaTray.Compatibility;

internal static class RegressionTests
{
    private static int _assertions;
    internal static void Run()
    {
        JsonTypes();
        DisplayCache();
        AntigravitySummaryAndCombinedMean();
        AntigravityCommandLineAndCandidateOrdering();
        AntigravityOfflineSnapshotPersistenceAndFourLineTooltip();
        SettingsRoundTrip();
        TaskbarGeometry();
        Console.WriteLine("New regression checks passed: " + _assertions);
    }
    private static void Assert(bool condition, string message)
    { _assertions++; if (!condition) throw new InvalidOperationException(message); }
    private static void JsonTypes()
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var map = (Dictionary<string, object>)serializer.DeserializeObject("{\"i\":1,\"l\":2147483648,\"d\":1.25,\"e\":1e2,\"a\":[true,null],\"same\":0,\"same\":2}");
        Assert(map["i"] is int && map["l"] is long && map["d"] is decimal && map["e"] is double, "Legacy numeric boxing must survive migration.");
        Assert(map["a"] is object[] && (int)map["same"] == 2, "Arrays and last duplicate property must match Framework.");
        Assert(serializer.DeserializeObject("\"2026-10-15T00:00:00Z\"") is string, "ISO dates must not be implicitly converted.");
        Assert(serializer.DeserializeObject("\"/Date(0)/\"") is string, "Unescaped ASP.NET date remains a string.");
        Assert(serializer.DeserializeObject("\"\\/Date(0)\\/\"") is DateTime, "Escaped ASP.NET date must preserve Framework conversion.");
        var permissive = (Dictionary<string, object>)serializer.DeserializeObject("{x:'text', y:+001}");
        Assert((string)permissive["x"] == "text" && (int)permissive["y"] == 1, "Legacy single quote and bare property input remains accepted.");
        Assert((string)serializer.DeserializeObject("\"\\uD800\"") == "\uFFFD", "Unpaired surrogate must be replaced like Framework.");
        foreach (var invalid in new[] { "[1,]", "{\"x\":1,}", "garbage", "null true", "1e400" })
        {
            bool rejected = false;
            try { serializer.DeserializeObject(invalid); } catch { rejected = true; }
            Assert(rejected, "Malformed/overflow JSON must fail: " + invalid);
        }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Assert((decimal)serializer.DeserializeObject("1.25") == 1.25m, "JSON must be culture-invariant.");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    private static void DisplayCache()
    {
        var settings = new AppSettings();
        var noData = QuotaDisplayState.Update(null, settings);
        Assert(!noData.Known && noData.Remaining == 0 && noData.ResetWindow == null, "First run must not invent quota.");
        var snapshot = new QuotaSnapshot();
        var first = new QuotaWindow { UsedPercent = 30, WindowMinutes = 300, ResetsAt = DateTimeOffset.UtcNow.AddHours(2) };
        var second = new QuotaWindow { UsedPercent = 60, WindowMinutes = 10080 };
        snapshot.Windows.Add(first); snapshot.Windows.Add(second);
        var offline = new QuotaReadResult { IsCodexRunning = false, Snapshot = snapshot };
        first.Kind = QuotaWindowKind.FiveHour; second.Kind = QuotaWindowKind.Weekly;
        var fallback = QuotaDisplayState.Update(offline, settings);
        Assert(fallback.Known && fallback.Remaining == 70 && fallback.ResetWindow == null, "First offline snapshot can seed display but no stale tooltip.");
        settings.LastRemainingPercent = 67;
        var kept = QuotaDisplayState.Update(offline, settings);
        Assert(kept.Remaining == 67 && !kept.CacheChanged, "Offline snapshots must not overwrite last successful display.");
        offline.IsCodexRunning = true;
        var live = QuotaDisplayState.Update(offline, settings);
        Assert(live.Remaining == 70 && live.ResetWindow == first && live.CacheChanged, "Live display must use the 5-hour window, not the weekly one.");
        first.UsedPercent = 10;
        Assert(QuotaDisplayState.Update(offline, settings).Remaining == 90, "Display follows the 5-hour window even when weekly is lower.");
        snapshot.ReserveWindow = new QuotaWindow { UsedPercent = 15, WindowMinutes = 10080 };
        Assert(QuotaDisplayState.Update(offline, settings).Remaining == 90, "Reserve bucket must not replace the 5-hour display.");
        Assert(QuotaDisplayState.Update(new QuotaReadResult(), settings).Remaining == 90, "Failed refresh keeps cache.");
    }
    private static void AntigravitySummaryAndCombinedMean()
    {
        const string summaryJson = "{\"response\":{\"groups\":[" +
            "{\"displayName\":\"Gemini Models\",\"buckets\":[" +
            "{\"bucketId\":\"gemini-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.72,\"resetTime\":\"2026-08-24T02:50:47Z\"}," +
            "{\"bucketId\":\"gemini-5h\",\"window\":\"5h\",\"remainingFraction\":0.80,\"resetTime\":\"2026-08-21T18:06:34Z\"}]}," +
            "{\"displayName\":\"Claude and GPT models\",\"buckets\":[" +
            "{\"bucketId\":\"3p-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.64,\"resetTime\":\"2026-08-28T13:06:34Z\"}," +
            "{\"bucketId\":\"3p-5h\",\"window\":\"5h\",\"remainingFraction\":1,\"resetTime\":\"2026-08-21T18:06:34Z\"}]}]}}";

        QuotaSnapshot snapshot;
        Assert(SessionQuotaReader.TryParseAntigravityQuotaSummary(summaryJson, out snapshot), "Must parse RetrieveUserQuotaSummary response.");
        Assert(snapshot.Windows.Count == 4 && snapshot.HasPoolWindows, "Must extract all four Gemini and third-party windows.");
        Assert(snapshot.GeminiFiveHourWindow != null && snapshot.GeminiFiveHourWindow.RemainingPercent == 80, "Gemini 5h remaining must be 80%.");
        Assert(snapshot.GeminiWeeklyWindow != null && snapshot.GeminiWeeklyWindow.RemainingPercent == 72, "Gemini weekly remaining must be 72%.");
        Assert(snapshot.ThirdPartyFiveHourWindow != null && snapshot.ThirdPartyFiveHourWindow.RemainingPercent == 100, "Third-party 5h remaining must be 100%.");
        Assert(snapshot.ThirdPartyFiveHourWindow.IsUntouchedFull && !snapshot.ThirdPartyFiveHourWindow.ResetsAt.HasValue,
            "Untouched 100% bucket must drop sliding placeholder resetTime.");
        Assert(snapshot.GeminiFiveHourWindow.ResetsAt.HasValue, "Partially used bucket must keep real resetTime.");
        Assert(snapshot.ThirdPartyWeeklyWindow != null && snapshot.ThirdPartyWeeklyWindow.RemainingPercent == 64, "Third-party weekly remaining must be 64%.");
        // Gemini 5h remaining = 80%
        Assert(snapshot.DisplayRemainingPercent == 80, "Ring percentage must display Gemini 5h remaining quota directly.");

        // Weekly-only plan fallback (e.g., Free tier without 5h buckets): Gemini weekly = 72%
        const string weeklyOnlyJson = "{\"response\":{\"groups\":[" +
            "{\"displayName\":\"Gemini Models\",\"buckets\":[{\"bucketId\":\"gemini-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.72,\"resetTime\":\"2026-08-24T02:50:47Z\"}]}," +
            "{\"displayName\":\"Third-party models\",\"buckets\":[{\"bucketId\":\"3p-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.64,\"resetTime\":\"2026-08-28T13:06:34Z\"}]}]}}";
        QuotaSnapshot weeklySnapshot;
        Assert(SessionQuotaReader.TryParseAntigravityQuotaSummary(weeklyOnlyJson, out weeklySnapshot) &&
            weeklySnapshot.DisplayRemainingPercent == 72, "Weekly-only plans must fall back to Gemini weekly quota.");

        // Legacy GetUserStatus fallback
        const string userStatusJson = "{\"userStatus\":{\"cascadeModelConfigData\":{\"clientModelConfigs\":[" +
            "{\"label\":\"Gemini 3 Pro\",\"quotaInfo\":{\"remainingFraction\":0.5,\"resetTime\":\"2026-08-21T18:06:34Z\"}}," +
            "{\"label\":\"GPT-OSS\",\"quotaInfo\":{\"remainingFraction\":1.0,\"resetTime\":\"2026-08-21T18:06:34Z\"}}]}}}";
        QuotaSnapshot fallbackSnapshot;
        Assert(SessionQuotaReader.TryParseAntigravityUserStatusFallback(userStatusJson, out fallbackSnapshot) &&
            fallbackSnapshot.DisplayRemainingPercent == 50, "GetUserStatus fallback must display Gemini 5h quota (50%).");
    }
    private static void AntigravityCommandLineAndCandidateOrdering()
    {
        const string csrf = "11111111-2222-3333-4444-555555555555";
        const string decoy = "99999999-8888-7777-6666-555555555555";
        string token;
        bool lsp;

        Assert(SessionQuotaReader.IsAntigravityLanguageServerExe("language_server.exe") &&
               SessionQuotaReader.IsAntigravityLanguageServerExe("language_server_windows_x64.exe") &&
               SessionQuotaReader.IsAntigravityLanguageServerExe("LANGUAGE_SERVER_WINDOWS_ARM.EXE") &&
               SessionQuotaReader.IsAntigravityLanguageServerExe("agy.exe") &&
               !SessionQuotaReader.IsAntigravityLanguageServerExe("explorer.exe"),
               "Must match Antigravity 2.0 language_server.exe, legacy language_server_windows_*.exe, and agy.exe.");

        Assert(SessionQuotaReader.TryParseLanguageServerCommandLine(
            "\"C:\\Users\\lee\\AppData\\Local\\Programs\\Antigravity\\resources\\bin\\language_server.exe\" --standalone --override_ide_name antigravity --https_server_port 0 --csrf_token " + csrf,
            out token, out lsp) && token == csrf && !lsp,
            "Must accept Antigravity 2.0 standalone language_server.exe command line without --app_data_dir.");

        Assert(SessionQuotaReader.TryParseLanguageServerCommandLine(
            "\"C:\\Program Files\\Antigravity\\language_server_windows_x64.exe\" --extension_server_csrf_token " + decoy + " --csrf_token " + csrf + " --app_data_dir antigravity-ide",
            out token, out lsp) && token == csrf && !lsp,
            "Must extract --csrf_token rather than --extension_server_csrf_token when both are present.");

        LanguageServerCandidate extOnlyCandidate;
        Assert(SessionQuotaReader.TryParseCandidateCommandLine(
            "language_server.exe",
            "language_server.exe --extension_server_csrf_token " + decoy,
            out extOnlyCandidate) && extOnlyCandidate.ExtensionCsrfToken == decoy,
            "Must retain --extension_server_csrf_token as a fallback token.");

        LanguageServerCandidate cliCandidate;
        Assert(SessionQuotaReader.TryParseCandidateCommandLine(
            "agy.exe",
            "\"C:\\Users\\lee\\AppData\\Local\\agy\\bin\\agy.exe\"",
            out cliCandidate) && cliCandidate.IsCliProcess,
            "Must accept tokenless agy.exe CLI candidate.");

        Assert(SessionQuotaReader.TryParseLanguageServerCommandLine(
            "ls.exe --app_data_dir=\"C:\\Users\\lee\\.gemini\\antigravity\" --csrf_token=" + csrf + " --enable_lsp",
            out token, out lsp) && token == csrf && lsp,
            "Must accept full quoted Antigravity 2.0 app_data_dir paths and --enable_lsp.");

        Assert(!SessionQuotaReader.TryParseLanguageServerCommandLine(
            "ls.exe --app_data_dir windsurf --csrf_token " + csrf,
            out token, out lsp),
            "Must ignore non-Antigravity language servers.");

        Assert(!SessionQuotaReader.TryParseLanguageServerCommandLine(
            "language_server.exe --standalone",
            out token, out lsp),
            "Must reject language_server command lines with no CSRF token.");

        // Connect-ES protobuf oneof remaining format + Gemini 4 Argon extra group
        const string oneofSummaryJson = "{\"summary\":{\"groups\":[" +
            "{\"displayName\":\"Gemini 4 Argon\",\"buckets\":[{\"bucketId\":\"gemini-4-argon-5h\",\"window\":\"session\",\"remaining\":{\"case\":\"remainingFraction\",\"value\":0.2}}]}," +
            "{\"displayName\":\"Gemini Pro\",\"buckets\":[{\"bucketId\":\"gemini-5h\",\"window\":\"session\",\"remaining\":{\"case\":\"remainingFraction\",\"value\":0.6}}]}," +
            "{\"displayName\":\"Claude & GPT\",\"buckets\":[{\"bucketId\":\"claude-session\",\"window\":\"5-hour\",\"remaining\":{\"remainingFraction\":0.8}}]}]}}";
        QuotaSnapshot oneofSnapshot;
        Assert(SessionQuotaReader.TryParseAntigravityQuotaSummary(oneofSummaryJson, out oneofSnapshot) &&
               oneofSnapshot.GeminiFiveHourWindow.RemainingPercent == 60 &&
               oneofSnapshot.ThirdPartyFiveHourWindow.RemainingPercent == 80 &&
               oneofSnapshot.DisplayRemainingPercent == 60,
               "Must parse Connect-ES oneof remaining format and prefer ordinary gemini-5h bucket over specialized model buckets.");

        var candidates = new List<LanguageServerCandidate>
        {
            new LanguageServerCandidate { Pid = 800, EnableLsp = true, CsrfToken = csrf },
            new LanguageServerCandidate { Pid = 2400, EnableLsp = false, CsrfToken = csrf },
            new LanguageServerCandidate { Pid = 1200, EnableLsp = false, CsrfToken = csrf }
        };
        SessionQuotaReader.SortLanguageServerCandidates(candidates);
        Assert(candidates[0].Pid == 1200 && candidates[1].Pid == 2400 && candidates[2].Pid == 800,
            "Global non-LSP language servers must be queried before workspace LSP servers.");

        var procTree = new List<(uint Pid, uint ParentPid, string ExeName)>
        {
            (100, 4, "explorer.exe"),
            (500, 100, "Antigravity.exe"),
            (510, 500, "Antigravity.exe"),
            (520, 510, "custom_sidecar.exe"),
            (900, 100, "other_app.exe")
        };
        var treeSelected = SessionQuotaReader.SelectTargetProcesses(procTree, new HashSet<uint> { 520, 900 });
        Assert(treeSelected.Count == 1 && treeSelected[0].Pid == 520 && treeSelected[0].IsAppDescendant,
            "Listening descendants of Antigravity.exe must be discovered even if the sidecar binary name changes.");
    }
    private static void AntigravityOfflineSnapshotPersistenceAndFourLineTooltip()
    {
        var now = DateTimeOffset.Parse("2026-11-18T15:00:00+08:00");
        var snapshot = new QuotaSnapshot { CapturedAt = now.AddMinutes(-10), LimitId = "antigravity" };
        snapshot.Windows.Add(new QuotaWindow
        {
            Pool = QuotaPoolKind.Gemini, Kind = QuotaWindowKind.FiveHour, WindowMinutes = 300,
            UsedPercent = 15, ResetsAt = now.AddHours(3).AddMinutes(12)
        });
        snapshot.Windows.Add(new QuotaWindow
        {
            Pool = QuotaPoolKind.Gemini, Kind = QuotaWindowKind.Weekly, WindowMinutes = 10080,
            UsedPercent = 28, ResetsAt = now.AddDays(4).AddHours(11).AddMinutes(5)
        });
        snapshot.Windows.Add(new QuotaWindow
        {
            Pool = QuotaPoolKind.ThirdParty, Kind = QuotaWindowKind.FiveHour, WindowMinutes = 300,
            UsedPercent = 0, IsUntouchedFull = true
        });
        snapshot.Windows.Add(new QuotaWindow
        {
            Pool = QuotaPoolKind.ThirdParty, Kind = QuotaWindowKind.Weekly, WindowMinutes = 10080,
            UsedPercent = 36, ResetsAt = now.AddDays(5).AddHours(8).AddMinutes(40)
        });

        var liveText = QuotaTooltipFormatter.FormatQuotaTooltip(snapshot, null, now, false, false);
        var liveLines = liveText.Split('\n');
        Assert(liveLines.Length == 4, "Live tooltip must have exactly 4 lines.");
        Assert(liveLines[0] == "Gemini 5h：85% · 0天3时12分后重置", "Line 1 must format Gemini 5h.");
        Assert(liveLines[1] == "Gemini 周：72% · 4天11时5分后重置", "Line 2 must format Gemini weekly.");
        Assert(liveLines[2] == "第三方 5h：100% · 满额未计时", "Line 3 must format untouched third-party 5h.");
        Assert(liveLines[3] == "第三方 周：64% · 5天8时40分后重置", "Line 4 must format third-party weekly.");

        var notifyLive = QuotaTooltipFormatter.FormatNotifyIconTooltip(snapshot, null, now, false);
        Assert(notifyLive.Length <= QuotaTooltipFormatter.MaxNotifyIconTextLength && notifyLive.Split('\n').Length == 4,
            "Live NotifyIcon tooltip must fit within 127 chars and keep all 4 lines.");

        var notifyOffline = QuotaTooltipFormatter.FormatNotifyIconTooltip(snapshot, SessionQuotaReader.NotRunningMessage, now, false);
        Assert(notifyOffline.Length <= QuotaTooltipFormatter.MaxNotifyIconTextLength && notifyOffline.Split('\n').Length == 5,
            "Offline NotifyIcon tooltip must fit within 127 chars and keep header + 4 pool lines.");

        // Round-trip offline cache through settings.ini
        var directory = Path.Combine(Path.GetTempPath(), "antigravity-cache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.ini");
        try
        {
            var settings = new AppSettings();
            var state = QuotaDisplayState.Update(new QuotaReadResult { IsCodexRunning = true, Snapshot = snapshot }, settings);
            // Gemini 5h = 85%
            Assert(state.Known && state.Remaining == 85 && state.CacheChanged, "Live dual-pool update must store Gemini 5h remaining and snapshot.");
            settings.Save(path);

            var reloaded = AppSettings.Load(path);
            var cachedSnapshot = reloaded.LoadQuotaSnapshot();
            Assert(reloaded.LastRemainingPercent == 85 && cachedSnapshot != null && cachedSnapshot.Windows.Count == 4,
                "Reloaded settings must restore both the ring percentage and all 4 cached windows when Antigravity is closed.");
            Assert(cachedSnapshot.GeminiFiveHourWindow.RemainingPercent == 85 &&
                   cachedSnapshot.GeminiWeeklyWindow.RemainingPercent == 72 &&
                   cachedSnapshot.ThirdPartyFiveHourWindow.IsUntouchedFull &&
                   cachedSnapshot.ThirdPartyWeeklyWindow.RemainingPercent == 64,
                   "Restored offline snapshot must preserve each window's percentage and full flag.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void SettingsRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quota-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.ini");
        try
        {
            File.WriteAllLines(path, new[] { "TrayRingWidth=5", "TrayFontSize=17", "LastRemainingPercent=82", "TrayRingColor=-65536", "Language=zh" });
            var settings = AppSettings.Load(path);
            Assert(settings.TrayRingWidth == 5 && settings.TrayFontSize == 17 && settings.LastRemainingPercent == 82, "Legacy settings and quota cache must migrate.");
            Assert(!settings.FloatingVisible && settings.FloatingDiameter == 256, "Migration should not create an unrequested visible floating window.");
            settings.FloatingVisible = true; settings.FloatingDiameter = 512;
            settings.UiRecoveryVersion = 1;
            settings.FloatingLeft = -1400; settings.FloatingTop = 200;
            settings.FloatingRingColor = Color.Lime; settings.FloatingFontColor = Color.Blue;
            settings.FloatingFontSize = 120; settings.FloatingFontWeight = 500;
            settings.FloatingRingWidth = 19; settings.FloatingRingScalePercent = 75;
            settings.Save(path);
            var restored = AppSettings.Load(path);
            Assert(restored.UiRecoveryVersion == 1, "One-time recovery onboarding must survive restart.");
            Assert(restored.FloatingVisible && restored.FloatingDiameter == 512 && restored.FloatingLeft == -1400 && restored.FloatingTop == 200, "Floating state must survive restart, including negative monitor coordinates.");
            Assert(restored.FloatingRingColor.ToArgb() == Color.Lime.ToArgb() && restored.FloatingFontColor.ToArgb() == Color.Blue.ToArgb(), "Floating colors are independent.");
            Assert(restored.TrayRingColor.ToArgb() == Color.Red.ToArgb() && restored.TrayFontSize == 17, "Floating changes must not mutate tray settings.");
            Assert(restored.FloatingFontSize == 120 && restored.FloatingFontWeight == 500 && restored.FloatingRingWidth == 19 && restored.FloatingRingScalePercent == 75, "Floating appearance must persist.");
            File.WriteAllLines(path, new[] { "FloatingDiameter=300", "FloatingFontWeight=1", "FloatingRingScalePercent=500" });
            var invalid = AppSettings.Load(path);
            Assert(invalid.FloatingDiameter == 256 && invalid.FloatingFontWeight == 700 && invalid.FloatingRingScalePercent == 100, "Invalid settings must not create arbitrary resize values.");
            Assert(!File.Exists(path + ".tmp"), "Successful atomic save must clean temporary file.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void TaskbarGeometry()
    {
        Rectangle placement;
        var bar = new Rectangle(0, 1040, 1920, 40);
        var tray = new Rectangle(1700, 1040, 220, 40);
        Assert(TaskbarPlacement.EdgeSize(bar, 1) == 28 && TaskbarPlacement.EdgeSize(new Rectangle(0, 0, 1920, 72), 1.5f) == 54, "Original large-icon sizing formula must remain intact.");
        var layout = new TaskbarLayoutSnapshot(new IntPtr(1), bar, tray, new[] { tray }, 96);
        Assert(TaskbarPlacement.TryPlace(layout, out placement) && placement.X == 1664 && placement.Width == 28, "Placement should follow the fresh tray leading edge.");
        var moved = new Rectangle(1800, 1040, 120, 40);
        Assert(TaskbarPlacement.TryPlace(new TaskbarLayoutSnapshot(new IntPtr(1), bar, moved, new[] { moved }, 96), out placement) && placement.X == 1764, "Removing an icon must use new geometry, never old cache.");
        Assert(!TaskbarPlacement.TryPlace(TaskbarLayoutSnapshot.Empty, out placement), "Failed/missing Explorer probe invalidates geometry.");
        var blocked = new Rectangle(0, 1040, 1700, 40);
        Assert(!TaskbarPlacement.TryPlace(new TaskbarLayoutSnapshot(new IntPtr(2), bar, tray, new[] { tray, blocked }, 96), out placement), "Crowded taskbar must not cover genuine app icons.");
        var vertical = new Rectangle(-60, 0, 60, 1200);
        var verticalTray = new Rectangle(-60, 1000, 60, 200);
        Assert(TaskbarPlacement.TryPlace(new TaskbarLayoutSnapshot(new IntPtr(3), vertical, verticalTray, new[] { verticalTray }, 96), out placement) && placement.Y == 944 && placement.Width == 48, "Vertical and negative-coordinate taskbars must use physical coordinates.");
    }
}
