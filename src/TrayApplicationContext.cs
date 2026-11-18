using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace CodexQuotaTray
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly AppSettings _settings;
        private readonly SessionQuotaReader _reader = new SessionQuotaReader();
        private readonly CodexCompletionMonitor _completionMonitor = new CodexCompletionMonitor();
        private readonly TaskbarWidgetForm _widget;
        private readonly FloatingQuotaWindow _floating;
        private readonly SettingsWindow _settingsWindow;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _floatingItem;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private readonly System.Windows.Forms.Timer _tooltipTimer;
        private readonly System.Windows.Forms.Timer _notificationTimer;
        private readonly System.Windows.Forms.Timer _flashTimer;
        private readonly WakeWindow _wake;
        private readonly NotifyIcon _recoveryIcon;
        private Icon _recoveryImage;
        private const int CurrentUiRecoveryVersion = 1;
        private const int RefreshIntervalMilliseconds = 60000;
        private const int MaximumRefreshIntervalMilliseconds = 600000;
        private const int MaxNotifyIconTextLength = 127;
        private QuotaReadResult _lastResult;
        private QuotaSnapshot _lastGoodSnapshot;
        private string _lastError;
        private bool _hasUnread;
        private bool _flash;
        private bool _exiting;
        private bool _resourcesDisposed;
        private int _refreshBusy;

        public TrayApplicationContext(bool openSettings = false, bool recover = false)
        {
            try
            {
                _settings = AppSettings.Load();
                _lastGoodSnapshot = _settings.LoadQuotaSnapshot();
                if (_lastGoodSnapshot != null && (_lastGoodSnapshot.HasPoolWindows || !_settings.LastRemainingPercent.HasValue))
                {
                    _settings.LastRemainingPercent = _lastGoodSnapshot.DisplayRemainingPercent;
                }

                _widget = new TaskbarWidgetForm();
                _widget.ApplySettings(_settings);
                var dispatcherHandle = _widget.Handle;
                _floating = new FloatingQuotaWindow(_settings);
                _floating.PositionChanged += SaveSettings;
                _settingsWindow = new SettingsWindow(_settings);
                System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(_settingsWindow);
                _settingsWindow.SettingsChanged += ApplySettingsChange;
                _settingsWindow.ExitRequested += delegate { ExitThread(); };

                _menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
                _floatingItem = new ToolStripMenuItem(T("悬浮窗", "Floating window")) { Checked = _settings.FloatingVisible };
                _floatingItem.Click += delegate { _settings.FloatingVisible = !_settings.FloatingVisible; ApplySettingsChange(); };
                _menu.Items.Add(_floatingItem);
                _menu.Items.Add(T("设置", "Settings"), null, delegate { ShowSettings(); });
                _menu.Items.Add(T("退出", "Exit"), null, delegate { ExitThread(); });
                _widget.RingClick += delegate { AcknowledgeCompletion(); ShowMenu(); };
                _menu.Opening += delegate { _floatingItem.Checked = _settings.FloatingVisible; _widget.SuppressZOrderRefresh = true; };
                _menu.Closed += delegate { _widget.SuppressZOrderRefresh = false; };
                _recoveryIcon = new NotifyIcon
                {
                    Text = T("Antigravity 额度 · 点击打开设置", "Antigravity Quota · Click for settings"),
                    ContextMenuStrip = _menu
                };
                _recoveryIcon.MouseClick += delegate(object sender, MouseEventArgs args)
                { if (args.Button == MouseButtons.Left) ShowSettings(); };
                _widget.VisibleChanged += delegate { UpdateRecoveryAccess(); };
                _wake = new WakeWindow(recoverUi => _widget.BeginInvoke(new Action(() => HandleWake(recoverUi))));

                _refreshTimer = new System.Windows.Forms.Timer { Interval = RefreshIntervalMilliseconds };
                _refreshTimer.Tick += delegate { RefreshInBackground(); };
                _refreshTimer.Start();
                _widget.ResetTextProvider = BuildResetText;
                _floating.ResetTextProvider = BuildResetText;
                _tooltipTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _tooltipTimer.Tick += delegate { _widget.RefreshResetTooltip(); _floating.RefreshResetTooltip(); UpdateRecoveryAccess(); };
                _tooltipTimer.Start();
                _notificationTimer = new System.Windows.Forms.Timer { Interval = CodexCompletionMonitor.IdleIntervalMilliseconds };
                _notificationTimer.Tick += delegate { PollCompletion(); };
                _flashTimer = new System.Windows.Forms.Timer { Interval = 600 };
                _flashTimer.Tick += delegate { _flash = !_flash; ApplyVisualResult(_lastResult); };
                if (_settings.CompletionReminderEnabled) { _completionMonitor.Enable(); _notificationTimer.Start(); }

                // Show persisted offline data immediately before the first background read completes.
                ApplyVisualResult(null);
                _widget.SetVisible(true);
                ApplyFloatingVisibility();
                UpdateRecoveryAccess();
                _widget.BeginInvoke(new Action(() =>
                {
                    if (recover) RecoverUi();
                    if (openSettings || recover || _settings.UiRecoveryVersion < CurrentUiRecoveryVersion) ShowSettings();
                }));
                RefreshInBackground();
            }
            catch
            {
                Cleanup(false);
                throw;
            }
        }

        private void ApplySettingsChange()
        {
            SaveSettings();
            _floatingItem.Checked = _settings.FloatingVisible;
            _settingsWindow.RefreshFromSettings();
            _widget.ApplySettings(_settings);
            _floating.ApplySettings();
            ApplyFloatingVisibility();
            ApplyVisualResult(_lastResult);
        }

        private void SaveSettings()
        {
            try { _settings.Save(); }
            catch (Exception exception) when (exception is System.IO.IOException || exception is UnauthorizedAccessException)
            { System.Diagnostics.Trace.WriteLine("Settings could not be saved: " + exception.Message); }
        }

        private void ApplyFloatingVisibility()
        {
            if (_settings.FloatingVisible) _floating.Show();
            else _floating.Hide();
        }

        private void HandleWake(bool recover)
        {
            if (recover) RecoverUi();
            ShowSettings();
        }

        private void RecoverUi()
        {
            RuntimeLog.Event("ui.recover");
            var work = Screen.PrimaryScreen.WorkingArea;
            _settings.FloatingLeft = work.Left + Math.Max(0, (work.Width - _settings.FloatingDiameter) / 2);
            _settings.FloatingTop = work.Top + Math.Max(0, (work.Height - _settings.FloatingDiameter) / 2);
            _settings.FloatingVisible = true;
            ApplySettingsChange();
        }

        private void UpdateRecoveryAccess()
        {
            if (_recoveryIcon == null || _exiting) return;
            UpdateTrayTooltip();
            bool needed = true;
            if (_recoveryIcon.Visible != needed)
            {
                _recoveryIcon.Visible = needed;
                RuntimeLog.Event(needed ? "taskbar.hidden.recovery-icon-shown" : "taskbar.visible.recovery-icon-hidden");
            }
        }

        private void UpdateTrayTooltip()
        {
            var snapshot = _lastResult != null && _lastResult.Snapshot != null ? _lastResult.Snapshot : _lastGoodSnapshot;
            var text = QuotaTooltipFormatter.FormatNotifyIconTooltip(snapshot, _lastError, DateTimeOffset.Now, _settings.IsEnglish);
            if (_recoveryIcon.Text != text) _recoveryIcon.Text = text;
        }

        private string BuildResetText()
        {
            var snapshot = _lastResult != null && _lastResult.Snapshot != null ? _lastResult.Snapshot : _lastGoodSnapshot;
            return QuotaTooltipFormatter.FormatQuotaTooltip(snapshot, _lastError, DateTimeOffset.Now, _settings.IsEnglish, false);
        }

        private void ShowSettings()
        {
            if (_exiting) return;
            _menu.Close();
            _settingsWindow.Show();
            if (_settingsWindow.WindowState == System.Windows.WindowState.Minimized)
                _settingsWindow.WindowState = System.Windows.WindowState.Normal;
            _settingsWindow.Activate();
            RuntimeLog.Event("ui.settings-shown");
            if (_settings.UiRecoveryVersion < CurrentUiRecoveryVersion)
            {
                _settings.UiRecoveryVersion = CurrentUiRecoveryVersion;
                SaveSettings();
            }
        }

        private void ShowMenu()
        {
            if (_exiting) return;
            if (_menu.Visible) { _menu.Close(); return; }
            _floatingItem.Checked = _settings.FloatingVisible;
            RuntimeLog.Event("ui.ring-menu");
            _widget.Activate();
            _menu.Show(_widget, new Point(_widget.Width / 2, 0), ToolStripDropDownDirection.AboveLeft);
        }

        private void RefreshInBackground()
        {
            if (_exiting || Interlocked.CompareExchange(ref _refreshBusy, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                QuotaReadResult result;
                try { result = _reader.ReadLatest(); }
                catch (Exception exception)
                {
                    // Exception messages can contain local paths; log the type and show a fixed message.
                    RuntimeLog.Exception("quota.read-exception", exception);
                    result = new QuotaReadResult { Error = "读取额度时出现未知错误" };
                }
                try
                {
                    if (!_exiting && !_widget.IsDisposed && _widget.IsHandleCreated)
                    {
                        _widget.BeginInvoke(new Action(() =>
                        {
                            if (_exiting) return;
                            _lastResult = result;
                            TrackRefreshOutcome(result);
                            ApplyVisualResult(result);
                        }));
                    }
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                finally { Interlocked.Exchange(ref _refreshBusy, 0); }
            });
        }

        // Poll the local language server once a minute; keep the normal pace when Antigravity is
        // simply closed, and back off up to ten minutes only on unexpected protocol/read errors.
        private void TrackRefreshOutcome(QuotaReadResult result)
        {
            if (result.Snapshot != null)
            {
                _lastGoodSnapshot = result.Snapshot;
                if (_lastError != null) RuntimeLog.Event("quota.read-recovered");
                _lastError = null;
                _refreshTimer.Interval = RefreshIntervalMilliseconds;
                return;
            }
            var error = string.IsNullOrWhiteSpace(result.Error) ? "读取额度时出现未知错误" : result.Error;
            var logDetail = "reason=" + error + (string.IsNullOrWhiteSpace(result.Diagnostics) ? string.Empty : " " + result.Diagnostics);
            if (error != _lastError) RuntimeLog.Event("quota.read-failed", logDetail);
            _lastError = error;
            _refreshTimer.Interval = result.KeepNormalPollInterval
                ? RefreshIntervalMilliseconds
                : Math.Min(_refreshTimer.Interval * 2, MaximumRefreshIntervalMilliseconds);
        }

        private void ApplyVisualResult(QuotaReadResult result)
        {
            var state = QuotaDisplayState.Update(result, _settings);
            if (state.CacheChanged) SaveSettings();
            _widget.SetVisual(state.Remaining, state.Known, _hasUnread && _flash);
            _floating.SetVisual(state.Remaining, state.Known, _hasUnread && _flash);

            var snapshot = (result != null ? result.Snapshot : null) ?? _lastGoodSnapshot;
            var resetWindow = state.ResetWindow
                ?? (snapshot != null
                    ? (snapshot.GeminiFiveHourWindow ?? snapshot.ThirdPartyFiveHourWindow ?? snapshot.FiveHourWindow ?? snapshot.WeeklyWindow ?? new QuotaWindow())
                    : (_lastError != null ? new QuotaWindow() : null));
            _widget.SetResetWindow(resetWindow);
            _floating.SetResetWindow(resetWindow);

            var previous = _recoveryImage;
            _recoveryImage = RingIconFactory.Create(state.Remaining, state.Known, ThemePalette.FromSettings(_settings), false, _settings);
            _recoveryIcon.Icon = _recoveryImage;
            previous?.Dispose();
            UpdateRecoveryAccess();
        }

        private void PollCompletion()
        {
            var result = _completionMonitor.Poll();
            _hasUnread = result.HasUnread;
            _notificationTimer.Interval = CodexCompletionMonitor.PollInterval(_hasUnread);
            if (_hasUnread) { if (!_flashTimer.Enabled) { _flash = true; _flashTimer.Start(); } }
            else { _flashTimer.Stop(); _flash = false; }
            if (result.StateChanged) ApplyVisualResult(_lastResult);
        }

        private void AcknowledgeCompletion()
        {
            if (!_hasUnread) return;
            _completionMonitor.AcknowledgeAll();
            _hasUnread = _flash = false;
            _flashTimer.Stop();
            _notificationTimer.Interval = CodexCompletionMonitor.PollInterval(false);
            ApplyVisualResult(_lastResult);
        }

        private string T(string chinese, string english) { return _settings.IsEnglish ? english : chinese; }

        protected override void ExitThreadCore()
        {
            Cleanup(true);
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cleanup(false);
            base.Dispose(disposing);
        }

        private void Cleanup(bool cleanExit)
        {
            if (_resourcesDisposed) return;
            _resourcesDisposed = true;
            _exiting = true;
            RuntimeLog.Event(cleanExit ? "app.clean-exit" : "app.resources-disposed");
            if (cleanExit && _settings != null) TryCleanup("settings-save", SaveSettings);
            foreach (var timer in new[] { _refreshTimer, _tooltipTimer, _notificationTimer, _flashTimer })
            {
                if (timer != null) TryCleanup("timer", () => { timer.Stop(); timer.Dispose(); });
            }
            TryCleanup("wake", () => _wake?.Dispose());
            TryCleanup("recovery-icon", () =>
            {
                if (_recoveryIcon != null) { _recoveryIcon.Visible = false; _recoveryIcon.Dispose(); }
                _recoveryImage?.Dispose();
            });
            TryCleanup("menu", () => _menu?.Dispose());
            TryCleanup("settings-window", () => _settingsWindow?.ClosePermanently());
            TryCleanup("floating-window", () => _floating?.Close());
            TryCleanup("taskbar-window", () => _widget?.Dispose());
        }

        private static void TryCleanup(string stage, Action action)
        {
            try { action(); }
            catch (Exception exception) { RuntimeLog.Exception("cleanup." + stage, exception); }
        }
    }
}
