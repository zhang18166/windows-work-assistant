using System.Threading;
namespace AutoImeSwitcher
{
    // UI 线程改变规则/前台/开关时推进版本；后台只读版本，不跨线程读取可变配置。
    internal sealed class OperationEpoch
    {
        private long _value;
        private int _stopped;
        internal long Current { get { return Interlocked.Read(ref _value); } }
        internal long Advance() { return Interlocked.Increment(ref _value); }
        internal bool IsCurrent(long value) { return Volatile.Read(ref _stopped) == 0 && Interlocked.Read(ref _value) == value; }
        internal void Stop() { Interlocked.Exchange(ref _stopped, 1); Advance(); }
    }
}
