using System;
using System.Globalization;

namespace CodexQuotaTray
{
    // Extracted from the original TrayApplicationContext for characterization tests.
    // The ring and number show the combined 0-100 mean across Gemini and third-party 5-hour pools.
    internal sealed class QuotaDisplayState
    {
        internal int Remaining;
        internal bool Known;
        internal bool CacheChanged;
        internal QuotaWindow ResetWindow;

        internal static QuotaDisplayState Update(QuotaReadResult result, AppSettings settings)
        {
            var active = result != null && result.IsCodexRunning && result.Snapshot != null;
            var snapshot = result == null ? null : result.Snapshot;
            QuotaWindow anchor = null;
            int snapshotRemaining = 0;
            if (snapshot != null)
            {
                snapshotRemaining = snapshot.DisplayRemainingPercent;
                anchor = snapshot.GeminiFiveHourWindow
                    ?? snapshot.ThirdPartyFiveHourWindow
                    ?? snapshot.FiveHourWindow
                    ?? snapshot.GeminiWeeklyWindow
                    ?? snapshot.ThirdPartyWeeklyWindow
                    ?? snapshot.WeeklyWindow;
            }

            var changed = anchor != null && (active || !settings.LastRemainingPercent.HasValue) &&
                settings.LastRemainingPercent != snapshotRemaining;
            if (changed) settings.LastRemainingPercent = snapshotRemaining;
            if (active && snapshot.HasPoolWindows && settings.SaveQuotaSnapshot(snapshot))
            {
                changed = true;
            }

            return new QuotaDisplayState
            {
                Remaining = settings.LastRemainingPercent ?? 0,
                Known = settings.LastRemainingPercent.HasValue,
                CacheChanged = changed,
                ResetWindow = active ? anchor : null
            };
        }
    }

    internal static class QuotaTooltipFormatter
    {
        internal const int MaxNotifyIconTextLength = 127;

        internal static string FormatNotifyIconTooltip(
            QuotaSnapshot snapshot,
            string lastError,
            DateTimeOffset now,
            bool english)
        {
            var full = FormatQuotaTooltip(snapshot, lastError, now, english, false);
            if (full.Length <= MaxNotifyIconTextLength) return full;

            var compact = FormatQuotaTooltip(snapshot, lastError, now, english, true);
            return compact.Length <= MaxNotifyIconTextLength
                ? compact
                : compact.Substring(0, MaxNotifyIconTextLength);
        }

        internal static string FormatQuotaTooltip(
            QuotaSnapshot snapshot,
            string lastError,
            DateTimeOffset now,
            bool english,
            bool compact)
        {
            var gemini5h = snapshot == null ? null : snapshot.GeminiFiveHourWindow;
            var geminiWeekly = snapshot == null ? null : snapshot.GeminiWeeklyWindow;
            var third5h = snapshot == null ? null : snapshot.ThirdPartyFiveHourWindow;
            var thirdWeekly = snapshot == null ? null : snapshot.ThirdPartyWeeklyWindow;

            var lines = string.Join("\n", new[]
            {
                FormatPoolWindowLine(compact ? "G·5h" : "Gemini 5h", gemini5h, now, english, compact),
                FormatPoolWindowLine(compact ? (english ? "G·wk" : "G·周") : (english ? "Gemini wk" : "Gemini 周"), geminiWeekly, now, english, compact),
                FormatPoolWindowLine(compact ? (english ? "3P·5h" : "三·5h") : (english ? "3rd-party 5h" : "第三方 5h"), third5h, now, english, compact),
                FormatPoolWindowLine(compact ? (english ? "3P·wk" : "三·周") : (english ? "3rd-party wk" : "第三方 周"), thirdWeekly, now, english, compact)
            });

            if (string.IsNullOrEmpty(lastError)) return lines;

            bool isOffline = lastError == SessionQuotaReader.NotRunningMessage;
            bool hasCacheTime = snapshot != null && snapshot.CapturedAt > DateTimeOffset.MinValue;
            string timeText = hasCacheTime
                ? snapshot.CapturedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                : null;

            if (isOffline)
            {
                string offlineHeader;
                if (hasCacheTime)
                {
                    offlineHeader = compact
                        ? (english ? "Offline (" + timeText + ")" : "未运行(缓存 " + timeText + ")")
                        : (english ? "Antigravity offline (cached " + timeText + ")" : "Antigravity 未运行（缓存 " + timeText + "）");
                }
                else
                {
                    offlineHeader = compact
                        ? (english ? "Antigravity offline" : "Antigravity 未运行")
                        : (english ? "Antigravity is not running" : "Antigravity 未运行，打开主程序后自动读取");
                }
                return offlineHeader + "\n" + lines;
            }

            if (compact)
            {
                var shortHeader = hasCacheTime
                    ? (english ? "Stale (" + timeText + ")" : "未更新(" + timeText + ")")
                    : (english ? "Read failed" : "读取失败");
                return shortHeader + "\n" + lines;
            }

            var header = hasCacheTime
                ? (english ? "Quota not updated (cached " + timeText + ")" : "额度未更新，显示的是 " + timeText + " 的缓存")
                : (english ? "Could not read quota" : "未能读取额度");
            var reason = lastError.Length > 40 ? lastError.Substring(0, 40) + "…" : lastError;
            return header + "\n" + reason + "\n" + lines;
        }

        private static string FormatPoolWindowLine(
            string label,
            QuotaWindow window,
            DateTimeOffset now,
            bool english,
            bool compact)
        {
            var separator = compact ? " " : (english ? ": " : "：");
            if (window == null)
            {
                return label + separator + (compact ? "--" : (english ? "No data" : "暂无数据"));
            }

            var percentText = window.RemainingPercent.ToString(CultureInfo.InvariantCulture) + "%";
            if (window.IsUntouchedFull || (window.RemainingPercent == 100 && !window.ResetsAt.HasValue))
            {
                return compact
                    ? label + " " + percentText + " " + (english ? "Full" : "满额")
                    : label + separator + percentText + " · " + (english ? "Full" : "满额未计时");
            }

            if (!window.ResetsAt.HasValue)
            {
                return label + separator + percentText;
            }

            if (window.ResetsAt.Value <= now)
            {
                return compact
                    ? label + " " + percentText + " " + (english ? "Due" : "待刷新")
                    : label + separator + percentText + " · " + (english ? "Reset due" : "已到重置时间");
            }

            var minutes = (long)Math.Ceiling((window.ResetsAt.Value - now).TotalMinutes);
            var days = minutes / 1440;
            var hours = minutes / 60 % 24;
            var mins = minutes % 60;

            if (compact)
            {
                var shortReset = english
                    ? days + "d" + hours + "h" + mins + "m"
                    : days + "天" + hours + "时" + mins + "分";
                return label + " " + percentText + " " + shortReset;
            }

            var resetText = english
                ? "Resets in " + days + "d " + hours + "h " + mins + "m"
                : days + "天" + hours + "时" + mins + "分后重置";
            return label + separator + percentText + " · " + resetText;
        }
    }
}
