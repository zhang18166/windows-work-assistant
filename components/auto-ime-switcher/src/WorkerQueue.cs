using System;
using System.Collections.Generic;
using System.Threading;
namespace AutoImeSwitcher
{
    internal sealed class WorkerQueue : IDisposable
    {
        private sealed class Work { internal string Key; internal Action Action; }
        private readonly LinkedList<Work> _queue = new LinkedList<Work>();
        private readonly object _sync = new object();
        private readonly Thread _thread;
        private readonly Action _onStopped;
        private bool _stopping;
        public int QueuedCount { get { lock (_sync) return _queue.Count; } }
        public WorkerQueue(string name, Action onStopped = null)
        {
            _onStopped = onStopped;
            _thread = new Thread(Loop) { IsBackground = true, Name = name };
            _thread.Start();
        }
        public void Enqueue(Action action) { EnqueueLatest(null, action); }
        public void EnqueueLatest(string key, Action action)
        {
            if (action == null) return;
            lock (_sync)
            {
                if (_stopping) return;
                if (key != null) RemoveKey(key);
                _queue.AddLast(new Work { Key = key, Action = action });
                Monitor.Pulse(_sync);
            }
        }
        private void RemoveKey(string key)
        {
            var node = _queue.First;
            while (node != null) { var next = node.Next; if (node.Value.Key == key) _queue.Remove(node); node = next; }
        }
        public void CancelPending(string key) { lock (_sync) RemoveKey(key); }
        private void Loop()
        {
            try { LoopCore(); }
            finally { if (_onStopped != null) _onStopped(); }
        }
        private void LoopCore()
        {
            while (true)
            {
                Action action;
                lock (_sync)
                {
                    while (!_stopping && _queue.Count == 0) Monitor.Wait(_sync);
                    if (_stopping) return;
                    action = _queue.First.Value.Action; _queue.RemoveFirst();
                }
                try { action(); } catch (Exception ex) { Logger.Error("后台任务异常: " + ex.Message); }
            }
        }
        public void Dispose()
        {
            lock (_sync) { _stopping = true; _queue.Clear(); Monitor.PulseAll(_sync); }
            // 调用方先撤销当前任务；仅等待其完成配对清理，绝不排空旧切换任务。
            if (Thread.CurrentThread != _thread) _thread.Join(1500);
        }
    }
}
