using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace CodexQuotaTray
{
    internal enum ActivityRange
    {
        Hours5,
        Hours24,
        Days7,
        Days30
    }

    internal sealed class AppSettings
    {
        public int? LastRemainingPercent { get; set; }
        public long? LastCapturedAtUnix { get; set; }
        public string CachedQuotaWindows { get; set; }
        public int UiRecoveryVersion { get; set; }
        public bool FloatingVisible { get; set; }
        public int FloatingDiameter { get; set; }
        public int? FloatingLeft { get; set; }
        public int? FloatingTop { get; set; }
        public Color FloatingRingColor { get; set; }
        public Color FloatingFontColor { get; set; }
        public int FloatingFontSize { get; set; }
        public int FloatingFontWeight { get; set; }
        public int FloatingRingWidth { get; set; }
        public int FloatingRingScalePercent { get; set; }
        public AppSettings()
        {
            Reset();
        }

        // Icon dimensions are measured on the existing 32px canvas.
        public int TrayRingWidth { get; set; }
        public Color TrayRingColor { get; set; }
        public Color TrayFontColor { get; set; }
        public int TrayFontSize { get; set; }

        public bool WidgetVisible { get; set; }
        public bool QuotaPrimaryEmphasis { get; set; }
        public bool CompletionReminderEnabled { get; set; }
        public bool GlassEnabled { get; set; }
        public int GlassOpacityPercent { get; set; }
        public string ThemeId { get; set; }
        public string AppearanceMode { get; set; }
        public Color CustomPrimary { get; set; }
        public Color CustomSecondary { get; set; }
        public string Language { get; set; }
        public ActivityRange ActivityRange { get; set; }
        public int PopupScalePercent { get; set; }
        public int FontScalePercent { get; set; }
        public int WidgetScalePercent { get; set; }

        public bool IsEnglish
        {
            get
            {
                if (string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(Language, "zh", StringComparison.OrdinalIgnoreCase)) return false;
                return !SystemLanguageName().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
        }

        public void Reset()
        {
            LastRemainingPercent = null;
            LastCapturedAtUnix = null;
            CachedQuotaWindows = null;
            UiRecoveryVersion = 0;
            FloatingVisible = false;
            FloatingDiameter = 256;
            FloatingLeft = null;
            FloatingTop = null;
            FloatingRingColor = Color.FromArgb(67, 194, 221);
            FloatingFontColor = Color.White;
            FloatingFontSize = 100;
            FloatingFontWeight = 700;
            FloatingRingWidth = 12;
            FloatingRingScalePercent = 100;
            TrayRingWidth = 3;
            TrayRingColor = Color.FromArgb(67, 194, 221);
            TrayFontColor = ThemePalette.SystemUsesLightTaskbar() ? Color.FromArgb(25, 34, 43) : Color.White;
            TrayFontSize = 14;
            WidgetVisible = true;
            QuotaPrimaryEmphasis = true;
            CompletionReminderEnabled = false;
            GlassEnabled = true;
            GlassOpacityPercent = 40;
            ThemeId = "cyan";
            AppearanceMode = "light";
            CustomPrimary = Color.FromArgb(67, 194, 221);
            CustomSecondary = Color.FromArgb(65, 174, 235);
            Language = "system";
            ActivityRange = ActivityRange.Days30;
            PopupScalePercent = 100;
            FontScalePercent = 100;
            WidgetScalePercent = 100;
        }

        public static AppSettings Load()
        {
            var path = GetPath();
            if (!File.Exists(path)) ImportLegacySettings(path);
            return Load(path);
        }

        // Copy visual preferences once from an existing sibling tray folder, without importing
        // another provider's cached quota numbers.
        private static void ImportLegacySettings(string path)
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (var legacyFolder in new[] { "CodexMeter" })
                {
                    var legacyPath = Path.Combine(localAppData, legacyFolder, "settings.ini");
                    if (!File.Exists(legacyPath)) continue;
                    var lines = File.ReadAllLines(legacyPath)
                        .Where(line =>
                        {
                            var trimmed = line.TrimStart();
                            return !trimmed.StartsWith("LastRemainingPercent=", StringComparison.OrdinalIgnoreCase) &&
                                   !trimmed.StartsWith("LastCapturedAtUnix=", StringComparison.OrdinalIgnoreCase) &&
                                   !trimmed.StartsWith("CachedQuotaWindows=", StringComparison.OrdinalIgnoreCase);
                        })
                        .ToArray();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllLines(path, lines);
                    return;
                }
            }
            catch
            {
                // Falling back to defaults is acceptable; the import never blocks startup.
            }
        }

        internal static AppSettings Load(string path)
        {
            var settings = new AppSettings();
            if (!File.Exists(path))
            {
                return settings;
            }

            try
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(path))
                {
                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }

                bool widgetVisible;
                if (TryGet(values, "WidgetVisible", out widgetVisible)) settings.WidgetVisible = widgetVisible;
                if (TryGet(values, "FloatingVisible", out widgetVisible)) settings.FloatingVisible = widgetVisible;
                if (TryGet(values, "QuotaPrimaryEmphasis", out widgetVisible)) settings.QuotaPrimaryEmphasis = widgetVisible;
                if (TryGet(values, "CompletionReminderEnabled", out widgetVisible)) settings.CompletionReminderEnabled = widgetVisible;
                if (TryGet(values, "GlassEnabled", out widgetVisible)) settings.GlassEnabled = widgetVisible;

                string text;
                if (values.TryGetValue("Theme", out text) && ThemePalette.IsKnownTheme(text)) settings.ThemeId = text;
                if (values.TryGetValue("Appearance", out text) && (text == "system" || text == "dark" || text == "light"))
                    settings.AppearanceMode = text;
                if (values.TryGetValue("Language", out text) && (text == "system" || text == "zh" || text == "en")) settings.Language = text;

                int seconds;
                if (TryGet(values, "UiRecoveryVersion", out seconds) && seconds >= 0) settings.UiRecoveryVersion = seconds;
                if (TryGet(values, "LastRemainingPercent", out seconds) && IsInRange(seconds, 0, 100))
                    settings.LastRemainingPercent = seconds;
                long capturedUnix;
                if (TryGetLong(values, "LastCapturedAtUnix", out capturedUnix) && capturedUnix > 0)
                    settings.LastCapturedAtUnix = capturedUnix;
                if (values.TryGetValue("CachedQuotaWindows", out text) && !string.IsNullOrWhiteSpace(text))
                    settings.CachedQuotaWindows = text;
                if (TryGet(values, "GlassOpacityPercent", out seconds) && IsInRange(seconds, 5, 95))
                    settings.GlassOpacityPercent = seconds;

                ActivityRange range;
                if (values.TryGetValue("ActivityRange", out text) && Enum.TryParse(text, true, out range))
                    settings.ActivityRange = range;

                if (TryGet(values, "PopupScalePercent", out seconds) && IsInRange(seconds, 80, 120)) settings.PopupScalePercent = seconds;
                if (TryGet(values, "FontScalePercent", out seconds) && IsInRange(seconds, 80, 120)) settings.FontScalePercent = seconds;
                if (TryGet(values, "WidgetScalePercent", out seconds) && IsInRange(seconds, 70, 130)) settings.WidgetScalePercent = seconds;
                if (TryGet(values, "TrayRingWidth", out seconds) && IsInRange(seconds, 1, 6)) settings.TrayRingWidth = seconds;
                if (TryGet(values, "TrayFontSize", out seconds) && IsInRange(seconds, 8, 20)) settings.TrayFontSize = seconds;
                if (TryGet(values, "FloatingDiameter", out seconds) && (seconds == 128 || seconds == 256 || seconds == 512)) settings.FloatingDiameter = seconds;
                if (TryGet(values, "FloatingLeft", out seconds)) settings.FloatingLeft = seconds;
                if (TryGet(values, "FloatingTop", out seconds)) settings.FloatingTop = seconds;
                if (TryGet(values, "FloatingFontSize", out seconds) && IsInRange(seconds, 32, 160)) settings.FloatingFontSize = seconds;
                if (TryGet(values, "FloatingFontWeight", out seconds) && (seconds == 400 || seconds == 500 || seconds == 600 || seconds == 700 || seconds == 800)) settings.FloatingFontWeight = seconds;
                if (TryGet(values, "FloatingRingWidth", out seconds) && IsInRange(seconds, 2, 32)) settings.FloatingRingWidth = seconds;
                if (TryGet(values, "FloatingRingScalePercent", out seconds) && IsInRange(seconds, 50, 100)) settings.FloatingRingScalePercent = seconds;
                int argb;
                if (TryGet(values, "FloatingRingColor", out argb)) settings.FloatingRingColor = Color.FromArgb(255, Color.FromArgb(argb));
                if (TryGet(values, "FloatingFontColor", out argb)) settings.FloatingFontColor = Color.FromArgb(255, Color.FromArgb(argb));
                if (TryGet(values, "TrayRingColor", out argb)) settings.TrayRingColor = Color.FromArgb(255, Color.FromArgb(argb));
                if (TryGet(values, "TrayFontColor", out argb)) settings.TrayFontColor = Color.FromArgb(255, Color.FromArgb(argb));
                if (TryGet(values, "CustomPrimary", out argb)) settings.CustomPrimary = Color.FromArgb(argb);
                if (TryGet(values, "CustomSecondary", out argb)) settings.CustomSecondary = Color.FromArgb(argb);
            }
            catch
            {
                settings.Reset();
            }

            return settings;
        }

        internal bool SaveQuotaSnapshot(QuotaSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Windows.Count == 0) return false;
            var entries = new List<string>();
            foreach (var window in snapshot.Windows)
            {
                if (window.Pool == QuotaPoolKind.Default) continue;
                var poolCode = window.Pool == QuotaPoolKind.Gemini ? "gemini" : "thirdparty";
                var resetUnix = window.ResetsAt.HasValue ? window.ResetsAt.Value.ToUnixTimeSeconds() : 0L;
                var fullFlag = window.IsUntouchedFull ? "1" : "0";
                entries.Add(string.Join(",", new[]
                {
                    poolCode,
                    window.WindowMinutes.ToString(CultureInfo.InvariantCulture),
                    window.UsedPercent.ToString("0.####", CultureInfo.InvariantCulture),
                    resetUnix.ToString(CultureInfo.InvariantCulture),
                    fullFlag
                }));
            }
            if (entries.Count == 0) return false;

            var encoded = string.Join("|", entries);
            var capturedUnix = snapshot.CapturedAt > DateTimeOffset.MinValue
                ? snapshot.CapturedAt.ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var contentChanged = !string.Equals(CachedQuotaWindows, encoded, StringComparison.Ordinal);
            var timestampStale = !LastCapturedAtUnix.HasValue || Math.Abs(capturedUnix - LastCapturedAtUnix.Value) >= 300;
            CachedQuotaWindows = encoded;
            LastCapturedAtUnix = capturedUnix;
            return contentChanged || timestampStale;
        }

        internal QuotaSnapshot LoadQuotaSnapshot()
        {
            if (string.IsNullOrWhiteSpace(CachedQuotaWindows)) return null;
            try
            {
                var snapshot = new QuotaSnapshot
                {
                    CapturedAt = LastCapturedAtUnix.HasValue && LastCapturedAtUnix.Value > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(LastCapturedAtUnix.Value)
                        : DateTimeOffset.MinValue,
                    LimitId = "antigravity"
                };

                foreach (var rawEntry in CachedQuotaWindows.Split('|'))
                {
                    if (string.IsNullOrWhiteSpace(rawEntry)) continue;
                    var parts = rawEntry.Split(',');
                    int minutes;
                    double used;
                    long resetUnix;
                    if (parts.Length < 5 ||
                        !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) ||
                        !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out used) ||
                        !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out resetUnix) ||
                        minutes <= 0 || double.IsNaN(used) || double.IsInfinity(used))
                    {
                        continue;
                    }

                    var pool = string.Equals(parts[0], "gemini", StringComparison.OrdinalIgnoreCase)
                        ? QuotaPoolKind.Gemini
                        : string.Equals(parts[0], "thirdparty", StringComparison.OrdinalIgnoreCase)
                            ? QuotaPoolKind.ThirdParty
                            : QuotaPoolKind.Default;
                    if (pool == QuotaPoolKind.Default) continue;

                    var full = parts[4] == "1";
                    snapshot.Windows.Add(new QuotaWindow
                    {
                        Pool = pool,
                        Name = (pool == QuotaPoolKind.Gemini ? "Gemini" : "ThirdParty") + ":" + minutes,
                        Key = (pool == QuotaPoolKind.Gemini ? "gemini:" : "thirdparty:") + minutes.ToString(CultureInfo.InvariantCulture),
                        Kind = SessionQuotaReader.ClassifyWindow(minutes),
                        WindowMinutes = minutes,
                        UsedPercent = Math.Max(0d, Math.Min(100d, used)),
                        ResetsAt = !full && resetUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetUnix) : (DateTimeOffset?)null,
                        IsUntouchedFull = full
                    });
                }

                return snapshot.Windows.Count > 0 ? snapshot : null;
            }
            catch
            {
                return null;
            }
        }

        public void Save()
        {
            Save(GetPath());
        }

        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var temporaryPath = path + ".tmp";
            File.WriteAllLines(temporaryPath, new[]
            {
                "SettingsVersion=2",
                "UiRecoveryVersion=" + UiRecoveryVersion.ToString(CultureInfo.InvariantCulture),
                "FloatingVisible=" + FloatingVisible,
                "FloatingDiameter=" + FloatingDiameter.ToString(CultureInfo.InvariantCulture),
                "FloatingLeft=" + (FloatingLeft.HasValue ? FloatingLeft.Value.ToString(CultureInfo.InvariantCulture) : ""),
                "FloatingTop=" + (FloatingTop.HasValue ? FloatingTop.Value.ToString(CultureInfo.InvariantCulture) : ""),
                "FloatingRingColor=" + FloatingRingColor.ToArgb().ToString(CultureInfo.InvariantCulture),
                "FloatingFontColor=" + FloatingFontColor.ToArgb().ToString(CultureInfo.InvariantCulture),
                "FloatingFontSize=" + FloatingFontSize.ToString(CultureInfo.InvariantCulture),
                "FloatingFontWeight=" + FloatingFontWeight.ToString(CultureInfo.InvariantCulture),
                "FloatingRingWidth=" + FloatingRingWidth.ToString(CultureInfo.InvariantCulture),
                "FloatingRingScalePercent=" + FloatingRingScalePercent.ToString(CultureInfo.InvariantCulture),
                "LastRemainingPercent=" + (LastRemainingPercent.HasValue ? LastRemainingPercent.Value.ToString(CultureInfo.InvariantCulture) : ""),
                "LastCapturedAtUnix=" + (LastCapturedAtUnix.HasValue ? LastCapturedAtUnix.Value.ToString(CultureInfo.InvariantCulture) : ""),
                "CachedQuotaWindows=" + (CachedQuotaWindows ?? ""),
                "TrayRingWidth=" + TrayRingWidth.ToString(CultureInfo.InvariantCulture),
                "TrayRingColor=" + TrayRingColor.ToArgb().ToString(CultureInfo.InvariantCulture),
                "TrayFontColor=" + TrayFontColor.ToArgb().ToString(CultureInfo.InvariantCulture),
                "TrayFontSize=" + TrayFontSize.ToString(CultureInfo.InvariantCulture),
                "WidgetVisible=" + WidgetVisible,
                "QuotaPrimaryEmphasis=" + QuotaPrimaryEmphasis,
                "CompletionReminderEnabled=" + CompletionReminderEnabled,
                "GlassEnabled=" + GlassEnabled,
                "GlassOpacityPercent=" + GlassOpacityPercent,
                "Theme=" + ThemeId,
                "Appearance=" + AppearanceMode,
                "CustomPrimary=" + CustomPrimary.ToArgb().ToString(CultureInfo.InvariantCulture),
                "CustomSecondary=" + CustomSecondary.ToArgb().ToString(CultureInfo.InvariantCulture),
                "Language=" + Language,
                "ActivityRange=" + ActivityRange,
                "PopupScalePercent=" + PopupScalePercent,
                "FontScalePercent=" + FontScalePercent,
                "WidgetScalePercent=" + WidgetScalePercent
            });
            // Same-directory replacement: a crash cannot leave a half-written settings file.
            File.Move(temporaryPath, path, true);
        }

        private static string GetPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AntigravityMeter",
                "settings.ini");
        }

        private static bool TryGet(Dictionary<string, string> values, string key, out bool parsed)
        {
            string value;
            parsed = false;
            return values.TryGetValue(key, out value) && bool.TryParse(value, out parsed);
        }

        private static bool TryGet(Dictionary<string, string> values, string key, out int parsed)
        {
            string value;
            parsed = 0;
            return values.TryGetValue(key, out value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool TryGetLong(Dictionary<string, string> values, string key, out long parsed)
        {
            string value;
            parsed = 0;
            return values.TryGetValue(key, out value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        }

        private static bool IsInRange(int value, int minimum, int maximum)
        {
            return value >= minimum && value <= maximum;
        }

        internal static string SystemLanguageName()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile"))
                {
                    var languages = key == null ? null : key.GetValue("Languages") as string[];
                    if (languages != null && languages.Length > 0 && !string.IsNullOrWhiteSpace(languages[0]))
                        return languages[0];
                }
            }
            catch { }

            return CultureInfo.InstalledUICulture.Name;
        }
    }
}
