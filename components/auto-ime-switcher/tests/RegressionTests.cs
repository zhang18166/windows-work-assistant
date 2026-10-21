using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace AutoImeSwitcher
{
    internal sealed class FakeIme : IImePlatform
    {
        public IntPtr ForegroundValue = new IntPtr(1), FocusValue = new IntPtr(2), LayoutValue = new IntPtr(0x804);
        public bool WindowValid = true, Held, SendOk = true, ApplyTap = true, ApplyWrite = true;
        public bool? Open = true, Caps = false;
        public int Taps, Writes, Reads;
        public Action OnWait, OnRead;
        public byte LastKey;
        public IntPtr LastWritten;
        public IntPtr Foreground { get { return ForegroundValue; } }
        public bool ModifiersDown { get { return Held; } }
        public bool IsWindowOf(IntPtr window, uint thread, uint process)
        { return WindowValid && (window == new IntPtr(1) || window == new IntPtr(2)) && thread == 10 && process == 20; }
        public IntPtr Focus(uint thread) { return FocusValue; }
        public IntPtr Layout(uint thread) { return LayoutValue; }
        public bool? ReadOpen(IntPtr window) { Reads++; if (OnRead != null) OnRead(); return Open; }
        public bool WriteOpen(IntPtr window, bool open)
        { Writes++; LastWritten = window; if (SendOk && ApplyWrite) Open = open; return SendOk; }
        public bool? ReadCapsLock() { return Caps; }
        public bool Tap(byte key)
        { Taps++; LastKey = key; if (SendOk && ApplyTap) { if (key == 0x14) Caps = !Caps; else Open = !Open; } return SendOk; }
        public bool RequestLayout(IntPtr window, IntPtr layout) { return SendOk; }
        public void Wait(int milliseconds) { if (OnWait != null) OnWait(); }
    }

    internal static class RegressionTests
    {
        private static int _passed;
        private static FakeIme _fake;
        private static ImeRestorePoint _restore;
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }
        private static void Test(string name, Action action)
        {
            _fake = new FakeIme(); ImeController.Platform = _fake; _restore = null;
            action(); _passed++; Console.WriteLine("PASS " + name);
        }
        private static SwitchResult Ensure(SwitchMethod method, Func<bool> guard = null)
        { return ImeController.EnsureEnglish(new IntPtr(1), 10, 20, method, guard ?? (() => true), out _restore); }
        private static void Switched()
        { Check(Ensure(SwitchMethod.ImeOpenClose) == SwitchResult.Switched && _restore != null, "Expected verified switch and restore ticket"); }
        private static void Main()
        {
            Logger.Enabled = false;
            Test("caps already on is not toggled off", () => {
                _fake.Caps = true; Check(Ensure(SwitchMethod.CapsLockKey) == SwitchResult.AlreadyEnglish && _fake.Taps == 0, "Caps toggled off"); });
            Test("caps off toggles exactly once", () => {
                Check(Ensure(SwitchMethod.CapsLockKey) == SwitchResult.Switched && _fake.Taps == 1 && _fake.LastKey == 0x14 && _fake.Caps == true, "Caps not enabled once"); });
            Test("unknown caps never injects", () => {
                _fake.Caps = null; Check(Ensure(SwitchMethod.CapsLockKey) == SwitchResult.UnknownState && _fake.Taps == 0, "Unknown caps injected"); });
            Test("caps failed verification is not success", () => {
                _fake.ApplyTap = false; Check(Ensure(SwitchMethod.CapsLockKey) == SwitchResult.RequestedButNotVerified, "False caps success"); });
            Test("unknown IME never falls back to Shift", () => {
                _fake.Open = null; Check(Ensure(SwitchMethod.ImeOpenClose) == SwitchResult.UnknownState && _fake.Taps == 0 && _fake.Writes == 0, "Implicit fallback"); });
            Test("unknown Shift state never injects", () => {
                _fake.Open = null; Check(Ensure(SwitchMethod.ShiftKey) == SwitchResult.UnknownState && _fake.Taps == 0, "Blind Shift"); });
            Test("already English gets no restore ticket", () => {
                _fake.Open = false; Check(Ensure(SwitchMethod.ImeOpenClose) == SwitchResult.AlreadyEnglish && _restore == null && _fake.Writes == 0, "Unowned restore"); });
            Test("Shift switches once and verifies", () => {
                Check(Ensure(SwitchMethod.ShiftKey) == SwitchResult.Switched && _fake.Taps == 1 && _restore != null, "Shift verification failed"); });
            Test("unresponsive Shift never gets a second tap", () => {
                _fake.ApplyTap = false; Check(Ensure(SwitchMethod.ShiftKey) == SwitchResult.RequestedButNotVerified && _fake.Taps == 1, "Shift retried"); });
            Test("foreground changes before injection cancel", () => {
                _fake.OnRead = () => _fake.ForegroundValue = new IntPtr(9);
                Check(Ensure(SwitchMethod.ShiftKey) == SwitchResult.Skipped && _fake.Taps == 0, "Wrong foreground input"); });
            Test("foreground changes after first Shift never retry", () => {
                _fake.OnWait = () => _fake.ForegroundValue = new IntPtr(9);
                Check(Ensure(SwitchMethod.ShiftKey) == SwitchResult.Skipped && _fake.Taps == 1 && _restore == null, "Retry in new window"); });
            Test("focus changes before injection cancel", () => {
                _fake.OnRead = () => _fake.FocusValue = new IntPtr(3);
                Check(Ensure(SwitchMethod.ShiftKey) == SwitchResult.Skipped && _fake.Taps == 0, "Wrong focus input"); });
            Test("held modifiers prevent all simulated input", () => {
                _fake.Held = true; Check(Ensure(SwitchMethod.ShiftKey) == SwitchResult.Skipped && _fake.Taps == 0, "Modifier overwritten");
                Check(Ensure(SwitchMethod.CapsLockKey) == SwitchResult.Skipped && _fake.Taps == 0, "Caps with modifiers"); });
            Test("AltTab settles after modifier release without synthetic keyup", () => {
                _fake.Held = true; _fake.OnWait = () => _fake.Held = false;
                Check(Ensure(SwitchMethod.ShiftKey) == SwitchResult.Switched && _fake.Taps == 1, "Did not settle after release"); });
            Test("stale task cannot write", () => {
                Check(Ensure(SwitchMethod.ImeOpenClose, () => false) == SwitchResult.Skipped && _fake.Writes == 0, "Stale write"); });
            Test("disabled during retry stops subsequent writes", () => {
                var epoch = new OperationEpoch(); long value = epoch.Advance(); _fake.ApplyWrite = false;
                _fake.OnWait = () => epoch.Advance();
                Check(Ensure(SwitchMethod.ImeOpenClose, () => epoch.IsCurrent(value)) == SwitchResult.Skipped && _fake.Writes == 1, "Retry after disable"); });
            Test("reused window identity is rejected", () => {
                _fake.WindowValid = false; Check(Ensure(SwitchMethod.ImeOpenClose) == SwitchResult.NoWindow && _fake.Writes == 0, "Reused handle accepted"); });
            Test("failed write does not report success", () => {
                _fake.SendOk = false; Check(Ensure(SwitchMethod.ImeOpenClose) == SwitchResult.SendFailed && _restore == null, "False write success"); });
            Test("restore targets old focus without global keys", () => {
                Switched(); _fake.ForegroundValue = new IntPtr(9);
                Check(ImeController.RestorePrevious(_restore, () => true) && _fake.LastWritten == new IntPtr(2) && _fake.Taps == 0, "Global restore injection"); });
            Test("restore ticket is consumed only once", () => {
                Switched(); Check(ImeController.RestorePrevious(_restore, () => true), "First restore failed"); int writes = _fake.Writes;
                Check(!ImeController.RestorePrevious(_restore, () => true) && _fake.Writes == writes, "Ticket reused"); });
            Test("restoration waits for AltTab release without global keys", () => {
                Switched(); _fake.Held = true; _fake.OnWait = () => _fake.Held = false;
                Check(ImeController.RestorePrevious(_restore, () => true) && _fake.Taps == 0, "Restore did not settle"); });
            Test("unknown restore state never injects", () => {
                Switched(); _fake.Open = null; int writes = _fake.Writes;
                Check(!ImeController.RestorePrevious(_restore, () => true) && _fake.Writes == writes && _fake.Taps == 0, "Blind restore"); });
            Test("manual switching invalidates restoration", () => {
                Switched(); ImeController.NoteManualModeInput(); int writes = _fake.Writes;
                Check(!ImeController.RestorePrevious(_restore, () => true) && _fake.Writes == writes, "Manual choice overwritten"); });
            Test("observed manual mode change invalidates ticket permanently", () => {
                Switched(); _fake.Open = true; ImeController.ObserveRestorePoint(_restore); _fake.Open = false;
                Check(!ImeController.RestorePrevious(_restore, () => true), "Changed state restored"); });
            Test("keyboard layout change invalidates restoration", () => {
                Switched(); _fake.LayoutValue = new IntPtr(0x409);
                Check(!ImeController.RestorePrevious(_restore, () => true), "Different layout restored"); });
            Test("cancelled restoration never writes", () => {
                Switched(); int writes = _fake.Writes;
                Check(!ImeController.RestorePrevious(_restore, () => false) && _fake.Writes == writes, "Cancelled restore"); });
            Test("indicator reflects Chinese English unknown and CAPS", () => {
                Check(ImeController.ReadIndicator(new IntPtr(2), 10, SwitchMethod.ImeOpenClose) == "中", "Chinese state hidden");
                _fake.Open = false; Check(ImeController.ReadIndicator(new IntPtr(2), 10, SwitchMethod.ImeOpenClose) == "EN", "English state hidden");
                _fake.Open = null; Check(ImeController.ReadIndicator(new IntPtr(2), 10, SwitchMethod.ImeOpenClose) == "未知", "Unknown shown as English");
                _fake.Caps = true; Check(ImeController.ReadIndicator(new IntPtr(2), 10, SwitchMethod.CapsLockKey) == "CAPS", "Caps falsely shown as English"); });
            Test("same window title changes are delivered and duplicates suppressed", () => {
                var watcher = new ForegroundWatcher(); var info = new ForegroundInfo { Hwnd = new IntPtr(1), Pid = 20, ThreadId = 10, Title = "零件.SLDPRT" };
                Check(watcher.AcceptsChange(info), "Initial event lost");
                Check(!watcher.AcceptsChange(info), "Duplicate event repeated"); info.Title = "装配体.SLDASM";
                Check(watcher.AcceptsChange(info), "Same HWND title change lost"); });
            Test("title rule distinguishes documents in same process", () => {
                var method = typeof(AppContext).GetMethod("MatchesRules", BindingFlags.Static | BindingFlags.NonPublic);
                var rules = new[] { new TargetRule("SLDWORKS.exe", "SW") { TitleContains = ".SLDASM" } };
                var info = new ForegroundInfo { ProcessName = "SLDWORKS.exe", Title = "零件.SLDPRT" };
                Check(!(bool)method.Invoke(null, new object[] { info, rules }), "Part matches assembly rule"); info.Title = "装配体.SLDASM *";
                Check((bool)method.Invoke(null, new object[] { info, rules }), "Assembly rule missed"); });
            Test("missing config fields receive defaults and saved false survives", () => {
                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"autoSwitchEnabled\":false}")))
                {
                    var config = (AppConfig)serializer.ReadObject(stream);
                    Check(!config.AutoSwitchEnabled && config.ShowIndicator && config.IndicatorX == -1 && config.SwitchMethodValue == SwitchMethod.ImeOpenClose && config.Rules != null, "Config defaults lost");
                }
            });
            Test("epoch changes and shutdown revoke tasks", () => {
                var epoch = new OperationEpoch(); long old = epoch.Advance(); Check(epoch.IsCurrent(old), "Fresh token invalid");
                long current = epoch.Advance(); Check(!epoch.IsCurrent(old) && epoch.IsCurrent(current), "Old token valid"); epoch.Stop(); Check(!epoch.IsCurrent(current), "Shutdown token valid"); });
            Test("queue coalesces latest task and cancels pending work", QueueCoalescing);
            Test("queue disposal discards pending tasks", QueueDisposal);
            Test("activity distinguishes no action from verified and unverified switching", () => {
                string unchanged = ActivityJournal.DescribeResult(SwitchMethod.ShiftKey, SwitchResult.AlreadyEnglish);
                string unverified = ActivityJournal.DescribeResult(SwitchMethod.ShiftKey, SwitchResult.RequestedButNotVerified);
                string caps = ActivityJournal.DescribeResult(SwitchMethod.CapsLockKey, SwitchResult.Switched);
                Check(unchanged.Contains("未执行切换") && unverified.Contains("未验证成功") && !caps.Contains("英文"), "Misleading activity result"); });
            Test("stale completion cannot overwrite new foreground activity", () => {
                var journal = new ActivityJournal();
                var a = new ForegroundInfo { Hwnd = new IntPtr(1), Pid = 20, ProcessName = "CAD.exe" };
                var b = new ForegroundInfo { Hwnd = new IntPtr(3), Pid = 30, ProcessName = "Other.exe" };
                journal.Begin(1, a, true, true); journal.Begin(2, b, true, false);
                journal.Complete(1, "CAD.exe", SwitchMethod.ShiftKey, SwitchResult.Switched);
                var snapshot = journal.Snapshot(b, true, false, "中", "Other");
                Check(snapshot.CurrentAction.Contains("不执行切换") && snapshot.LastAction == "尚无切换记录", "Old task claimed new success"); });
            Test("history names its original program and paused state remains explicit", () => {
                var journal = new ActivityJournal();
                var info = new ForegroundInfo { Hwnd = new IntPtr(1), Pid = 20, ProcessName = "CAD.exe" };
                journal.Begin(1, info, true, true); journal.Complete(1, "CAD.exe", SwitchMethod.ImeOpenClose, SwitchResult.Switched);
                journal.Begin(2, info, false, true);
                var snapshot = journal.Snapshot(info, false, true, "EN", "CAD");
                Check(!snapshot.Enabled && snapshot.CurrentAction.Contains("暂停") && snapshot.LastAction.Contains("CAD.exe") && snapshot.InputState == "EN", "Paused state hides actual input/history"); });
            Test("reset to bottom right remains inside taskbar work area", () => {
                var area = new System.Drawing.Rectangle(0, 0, 1920, 1040);
                var size = new System.Drawing.Size(504, 264);
                var point = FloatingIndicator.BottomRight(area, size, 24);
                Check(point.X == 1392 && point.Y == 752 && area.Contains(new System.Drawing.Rectangle(point, size)), "Reset outside working area"); });
            Test("reset supports negative monitor coordinates and tiny work areas", () => {
                var area = new System.Drawing.Rectangle(-1920, -100, 1920, 1040);
                var point = FloatingIndicator.BottomRight(area, new System.Drawing.Size(336, 176), 16);
                Check(point.X == -352 && point.Y == 748, "Negative monitor placement wrong");
                Check(FloatingIndicator.BottomRight(new System.Drawing.Rectangle(0, 0, 200, 100), new System.Drawing.Size(336, 176), 16) == System.Drawing.Point.Empty, "Small desktop not clamped"); });
            Test("card renders correctly sized readable surfaces across DPI scales", () => {
                var snapshot = new ActivitySnapshot { ProcessName = "CAD.exe", DisplayName = "SOLIDWORKS", Matched = true, InputState = "EN", CurrentAction = "已是英文，未执行切换" };
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                    using (var image = ActivityCardPainter.Render(snapshot, null, scale))
                        Check(image.Width == (int)(168 * scale) && image.Height == (int)(34 * scale) && image.GetPixel(image.Width / 2, image.Height / 2).A == 255, "Mini strip size/background regression"); });
            Test("manual state changes do not leave a misleading current success message", () => {
                var journal = new ActivityJournal();
                var info = new ForegroundInfo { Hwnd = new IntPtr(1), Pid = 20, ProcessName = "CAD.exe" };
                journal.Begin(1, info, true, true); journal.Complete(1, "CAD.exe", SwitchMethod.ImeOpenClose, SwitchResult.Switched);
                var snapshot = journal.Snapshot(info, true, true, "中", "CAD");
                Check(snapshot.CurrentAction.Contains("保留当前选择") && snapshot.LastAction.Contains("已切换英文"), "Current state confused with historical success"); });
            Test("Caps indicator reports the lock even under an English layout", () => {
                _fake.LayoutValue = new IntPtr(0x409); _fake.Caps = false;
                Check(ImeController.ReadIndicator(new IntPtr(2), 10, SwitchMethod.CapsLockKey) == "caps关", "English layout hides Caps state"); });
            Test("compact labels never turn unverified or unchanged into success", () => {
                var snapshot = new ActivitySnapshot { CurrentAction = "已发出请求，未验证成功" };
                Check(snapshot.ShortAction == "未验证", "Unverified displayed as success");
                snapshot.CurrentAction = "已是英文，未执行切换"; Check(snapshot.ShortAction == "原本英文", "No action displayed as switched");
                snapshot.Enabled = false; Check(snapshot.ShortAction == "已暂停", "Paused state not visible"); });
            Test("compact view retains full program and history in details", () => {
                var snapshot = new ActivitySnapshot { DisplayName = "SOLIDWORKS", ProcessName = "SLDWORKS.exe", InputState = "EN", Matched = true,
                    CurrentAction = "已切换英文（已验证）", LastAction = "10:30:00 · SLDWORKS.exe · 已切换英文" };
                Check(snapshot.DetailsText.Contains("SLDWORKS.exe") && snapshot.DetailsText.Contains("10:30:00") && snapshot.DetailsText.Contains("已验证") && snapshot.DetailsText.Contains("已匹配"), "Details lost after compacting"); });
            Test("background transparency leaves text and program icon opaque across DPI scales", () => {
                var snapshot = new ActivitySnapshot { Enabled = true, Matched = true, InputState = "EN", CurrentAction = "已切换英文（已验证）" };
                using (var icon = new System.Drawing.Bitmap(18, 18))
                {
                    using (var g = System.Drawing.Graphics.FromImage(icon)) g.Clear(System.Drawing.Color.FromArgb(210, 30, 170));
                    foreach (float scale in new[] { 1f, 1.5f, 2f })
                        foreach (int percent in new[] { 0, 30, 50, 100 })
                            using (var image = ActivityCardPainter.Render(snapshot, icon, scale, percent))
                            {
                                var background = image.GetPixel((int)(30 * scale), (int)(17 * scale));
                                Check(background.A == FloatingIndicator.TransparencyToAlpha(percent), "Background alpha not applied independently");
                                var iconPixel = image.GetPixel((int)(16 * scale), (int)(16 * scale));
                                Check(iconPixel.A == 255 && iconPixel.R == 210 && iconPixel.G == 30 && iconPixel.B == 170, "Icon faded with background");
                                int opaqueText = 0, opaqueStatus = 0;
                                for (int y = 3; y < (int)(31 * scale); y++)
                                    for (int x = (int)(36 * scale); x < (int)(160 * scale); x++)
                                    {
                                        var pixel = image.GetPixel(x, y);
                                        if (x < (int)(76 * scale) && pixel.A == 255 && pixel.R > 230 && pixel.G > 230 && pixel.B > 230) opaqueText++;
                                        if (x > (int)(88 * scale) && pixel.A == 255 && pixel.G > 195 && pixel.R < 155) opaqueStatus++;
                                    }
                                Check(opaqueText > 0 && opaqueStatus > 0, "Text/status faded with background");
                                if (percent == 100)
                                    Check(image.GetPixel((int)(79 * scale), (int)(17 * scale)).A == 0, "Divider remains when background is transparent");
                            }
                }
            });
            Test("transparency endpoints and midpoints map to background alpha", () => {
                Check(FloatingIndicator.TransparencyToAlpha(0) == 255 && FloatingIndicator.TransparencyToAlpha(100) == 0 &&
                    FloatingIndicator.TransparencyToAlpha(50) == 128 && FloatingIndicator.TransparencyToAlpha(30) == 179, "Transparency semantics reversed");
                int previous = 256;
                for (int percentage = 0; percentage <= 100; percentage++)
                { int alpha = FloatingIndicator.TransparencyToAlpha(percentage); Check(alpha <= previous, "Alpha is not monotonic"); previous = alpha; }
            });
            Test("legacy configs default to visible transparency and explicit zero survives", () => {
                var serializer = new DataContractJsonSerializer(typeof(AppConfig));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}")))
                    Check(((AppConfig)serializer.ReadObject(stream)).IndicatorTransparencyPercent == 30, "Legacy transparency default lost");
                foreach (int percentage in new[] { 0, 42, 100 })
                {
                    using (var stream = new MemoryStream())
                    {
                        serializer.WriteObject(stream, new AppConfig { IndicatorTransparencyPercent = percentage }); stream.Position = 0;
                        Check(((AppConfig)serializer.ReadObject(stream)).IndicatorTransparencyPercent == percentage, "Saved percentage did not survive reload");
                    }
                }
            });
            Test("invalid transparency config is clamped safely", () => {
                var config = new AppConfig { IndicatorTransparencyPercent = -15 };
                Check(config.IndicatorTransparencyPercent == 0 && FloatingIndicator.TransparencyToAlpha(-15) == 255, "Negative transparency not clamped");
                config.IndicatorTransparencyPercent = 250;
                Check(config.IndicatorTransparencyPercent == 100 && FloatingIndicator.TransparencyToAlpha(250) == 0, "Oversized transparency not clamped");
            });
            Test("find indicator recovers a hidden fully transparent window", () => {
                var config = new AppConfig { ShowIndicator = false, IndicatorTopMost = false, IndicatorTransparencyPercent = 100, IndicatorClickThrough = true };
                config.RestoreIndicatorVisibility();
                Check(config.ShowIndicator && config.IndicatorTopMost && config.IndicatorTransparencyPercent == 30 && config.IndicatorClickThrough,
                    "Invisible indicator cannot be recovered or unrelated preference changed");
            });
            Console.WriteLine("RESULT " + _passed + " passed; no live input or user configuration accessed.");
        }
        private static void QueueCoalescing()
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var done = new ManualResetEvent(false))
            using (var queue = new WorkerQueue("Regression-queue"))
            {
                queue.Enqueue(() => { entered.Set(); release.WaitOne(); }); Check(entered.WaitOne(2000), "Worker not started");
                int result = 0;
                queue.EnqueueLatest("switch", () => result = 1); queue.EnqueueLatest("switch", () => result = 2);
                queue.EnqueueLatest("state", () => result = 99); queue.CancelPending("state");
                queue.Enqueue(() => done.Set()); Check(queue.QueuedCount == 2, "Queue did not coalesce");
                release.Set(); Check(done.WaitOne(2000) && result == 2, "Wrong queued task executed");
            }
        }
        private static void QueueDisposal()
        {
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var disposalEntered = new ManualResetEvent(false))
            {
                var queue = new WorkerQueue("Regression-dispose"); int executed = 0;
                queue.Enqueue(() => { entered.Set(); release.WaitOne(); }); Check(entered.WaitOne(2000), "Worker not started");
                queue.Enqueue(() => Interlocked.Increment(ref executed));
                var disposer = new Thread(() => { disposalEntered.Set(); queue.Dispose(); }); disposer.Start();
                Check(disposalEntered.WaitOne(2000), "Disposer not started");
                // Dispose clears pending work before waiting for the running action.
                Check(SpinWait.SpinUntil(() => queue.QueuedCount == 0, 2000), "Pending work not cleared");
                release.Set(); Check(disposer.Join(2000) && executed == 0, "Pending task ran during shutdown");
            }
        }
    }
}
