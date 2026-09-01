using System;
using System.Collections.Generic;
using System.Threading;

namespace ConsoleClient
{
    /// <summary>Serializes database writes and coalesces replaceable state updates.</summary>
    internal sealed class DatabaseWriteQueue : IDisposable
    {
        private sealed class WorkItem { public Action Action; public string Key; }
        private readonly object _gate = new object();
        private readonly LinkedList<WorkItem> _pending = new LinkedList<WorkItem>();
        private readonly Dictionary<string, LinkedListNode<WorkItem>> _keyed = new Dictionary<string, LinkedListNode<WorkItem>>(StringComparer.Ordinal);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _worker;
        private readonly int _maximumPending;
        private bool _accepting = true;
        private bool _completed;
        private long _discarded;

        public DatabaseWriteQueue(int maximumPending = 65536)
        {
            if (maximumPending < 1) throw new ArgumentOutOfRangeException("maximumPending");
            _maximumPending = maximumPending;
            _worker = new Thread(Work) { IsBackground = true, Name = "Meshtastic database writer" };
            _worker.Start();
        }

        public int PendingCount { get { lock (_gate) return _pending.Count; } }
        public long DiscardedCount { get { lock (_gate) return _discarded; } }

        public bool Enqueue(Action action, string coalesceKey = null)
        {
            if (action == null) throw new ArgumentNullException("action");
            lock (_gate)
            {
                if (!_accepting) return false;
                LinkedListNode<WorkItem> existing;
                if (!String.IsNullOrEmpty(coalesceKey) && _keyed.TryGetValue(coalesceKey, out existing))
                {
                    existing.Value.Action = action;
                    return true;
                }
                if (_pending.Count >= _maximumPending)
                {
                    var candidate = _pending.First;
                    while (candidate != null && String.IsNullOrEmpty(candidate.Value.Key)) candidate = candidate.Next;
                    if (candidate == null) { _discarded++; return false; }
                    _keyed.Remove(candidate.Value.Key);
                    _pending.Remove(candidate);
                    _discarded++;
                }
                var node = _pending.AddLast(new WorkItem { Action = action, Key = coalesceKey });
                if (!String.IsNullOrEmpty(coalesceKey)) _keyed[coalesceKey] = node;
            }
            _wake.Set();
            return true;
        }

        public bool CompleteAndWait(TimeSpan timeout)
        {
            lock (_gate) _accepting = false;
            _wake.Set();
            return _worker.Join(timeout);
        }

        private void Work()
        {
            while (true)
            {
                WorkItem item = null;
                lock (_gate)
                {
                    if (_pending.Count > 0)
                    {
                        var node = _pending.First;
                        _pending.RemoveFirst();
                        item = node.Value;
                        if (!String.IsNullOrEmpty(item.Key)) _keyed.Remove(item.Key);
                    }
                    else if (!_accepting) { _completed = true; return; }
                }
                if (item == null) { _wake.WaitOne(); continue; }
                try { item.Action(); } catch { }
            }
        }

        public void Dispose()
        {
            if (!CompleteAndWait(TimeSpan.FromSeconds(5))) return;
            lock (_gate) { if (!_completed) return; }
            _wake.Dispose();
        }
    }
}