// Code Obfuscator — background work loop with a main-thread watchdog.
//
// The v2 engine did every transform synchronously inside one EditorWindow button handler, so a
// single slow file froze the whole editor with no way to cancel. Here:
//   • pure text work runs on a dedicated background thread (no Unity API allowed in `body`);
//   • results are queued back and the MAIN thread performs every file write / AssetDatabase call;
//   • the main thread polls once per EditorApplication.update tick, shows a cancelable progress
//     bar, and if the worker sits on one item longer than the per-file budget it records a skip,
//     abandons that thread (best-effort Abort) and starts a fresh thread at the next item.
// A stuck item therefore costs at most `budget` seconds instead of the whole session.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CodeObfuscator
{
    /// Thread-safe progress cell written by workers, read by the UI.
    public class ObfProgress
    {
        volatile string _stage = "", _current = "";
        volatile int _done, _total;
        public void Set(string stage, string current, int done, int total) { _stage = stage ?? ""; _current = current ?? ""; _done = done; _total = total; }
        public string Stage => _stage;
        public string Current => _current;
        public int Done => _done;
        public int Total => _total;
    }

    public struct ObfWorkResult<TResult>
    {
        public int index;
        public TResult result;
        public Exception error;
    }

    public class ObfWorkLoop<TItem, TResult>
    {
        readonly IList<TItem> _items;
        readonly Func<TItem, CancellationToken, TResult> _body;
        readonly ConcurrentQueue<ObfWorkResult<TResult>> _results = new ConcurrentQueue<ObfWorkResult<TResult>>();
        readonly HashSet<int> _skipped = new HashSet<int>();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        Thread _thread;
        int _gen;                       // bumped on every (re)start; stale threads never publish
        int _nextStart;
        volatile int _currentIndex = -1;
        long _currentStart;
        volatile bool _finished;
        int _restarts;

        public ObfWorkLoop(IList<TItem> items, Func<TItem, CancellationToken, TResult> body)
        {
            _items = items; _body = body;
            if (items.Count == 0) _finished = true;
        }

        public int Count => _items.Count;
        public int CurrentIndex => _currentIndex;
        public int Restarts => _restarts;
        public bool Finished => _finished && _results.IsEmpty;
        public IReadOnlyCollection<int> Skipped => _skipped;

        public double CurrentElapsedSec
        {
            get
            {
                long s = Interlocked.Read(ref _currentStart);
                return s == 0 || _currentIndex < 0 ? 0 : (Stopwatch.GetTimestamp() - s) / (double)Stopwatch.Frequency;
            }
        }

        public void Start()
        {
            if (_items.Count == 0 || _nextStart >= _items.Count) { _finished = true; return; }
            int gen = Interlocked.Increment(ref _gen);
            int start = _nextStart;
            var ct = _cts.Token;
            _thread = new Thread(() =>
            {
                for (int i = start; i < _items.Count; i++)
                {
                    if (ct.IsCancellationRequested || Volatile.Read(ref _gen) != gen) return;
                    _currentIndex = i;
                    Interlocked.Exchange(ref _currentStart, Stopwatch.GetTimestamp());
                    var r = new ObfWorkResult<TResult> { index = i };
                    try { r.result = _body(_items[i], ct); }
                    catch (ThreadAbortException) { return; }
                    catch (Exception ex) { r.error = ex; }
                    if (Volatile.Read(ref _gen) != gen) return; // abandoned while working: drop the result
                    _results.Enqueue(r);
                }
                if (Volatile.Read(ref _gen) == gen) { _currentIndex = -1; _finished = true; }
            }) { IsBackground = true, Name = "CodeObfuscator.Worker" };
            _thread.Start();
        }

        public bool TryDequeue(out ObfWorkResult<TResult> r)
        {
            while (_results.TryDequeue(out r))
            {
                if (_skipped.Contains(r.index)) continue; // late result from an abandoned thread
                return true;
            }
            return false;
        }

        /// Main thread: give up on the item the worker is currently stuck on and continue after it.
        public int SkipCurrentAndRestart()
        {
            int idx = _currentIndex;
            if (idx < 0) return -1;
            _skipped.Add(idx);
            Abandon();
            _nextStart = idx + 1;
            _restarts++;
            Start();
            return idx;
        }

        public void Cancel()
        {
            _cts.Cancel();
            Abandon();
            _finished = true;
        }

        void Abandon()
        {
            Interlocked.Increment(ref _gen);
            var t = _thread; _thread = null;
            if (t == null) return;
            try { if (t.IsAlive) t.Abort(); } catch { /* Abort unsupported on this runtime: thread stays orphaned but can never publish */ }
        }
    }

    /// Runs one long job (scan, verify…) on a background thread with a deadline; same watchdog idea.
    public class ObfJob<T>
    {
        readonly Func<CancellationToken, T> _body;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        Thread _thread;
        volatile bool _done;
        T _result; Exception _error;
        readonly Stopwatch _sw = new Stopwatch();

        public ObfJob(Func<CancellationToken, T> body) { _body = body; }

        public void Start()
        {
            _sw.Start();
            var ct = _cts.Token;
            _thread = new Thread(() =>
            {
                try { _result = _body(ct); }
                catch (ThreadAbortException) { return; }
                catch (Exception ex) { _error = ex; }
                _done = true;
            }) { IsBackground = true, Name = "CodeObfuscator.Job" };
            _thread.Start();
        }

        public bool Done => _done;
        public double ElapsedSec => _sw.Elapsed.TotalSeconds;
        public T Result => _result;
        public Exception Error => _error;

        public void Cancel()
        {
            _cts.Cancel();
            var t = _thread; _thread = null;
            try { if (t != null && t.IsAlive) t.Abort(); } catch { }
        }
    }
}
