using System.Linq;

namespace CodexQuotaTray
{
    // Extracted from the original TrayApplicationContext for characterization tests.
    // The ring and number always show the 5-hour window's remaining quota.
    internal sealed class QuotaDisplayState
    {
        internal int Remaining;
        internal bool Known;
        internal bool CacheChanged;
        internal QuotaWindow ResetWindow;

        internal static QuotaDisplayState Update(QuotaReadResult result, AppSettings settings)
        {
            var active = result != null && result.IsCodexRunning && result.Snapshot != null;
            // Display the 5-hour window; the weekly window is shown as text in the hover tooltip.
            var window = result == null || result.Snapshot == null ? null : result.Snapshot.FiveHourWindow;
            var changed = window != null && (active || !settings.LastRemainingPercent.HasValue) &&
                settings.LastRemainingPercent != window.RemainingPercent;
            if (changed) settings.LastRemainingPercent = window.RemainingPercent;
            return new QuotaDisplayState
            {
                Remaining = settings.LastRemainingPercent ?? 0,
                Known = settings.LastRemainingPercent.HasValue,
                CacheChanged = changed,
                ResetWindow = active ? window : null
            };
        }
    }
}
