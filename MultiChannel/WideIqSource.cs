using SDRSharp.Common;
using SDRSharp.Radio;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace SDRSharp.Tetra.MultiChannel
{
    public unsafe class WideIqSource : IDisposable
    {
        private readonly ISharpControl _control;

        // Two processors: prefer RawIQ, fallback to BasebandIQ
        private readonly WideIqProcessor _rawProc;
        private readonly WideIqProcessor _bbProc;

        private readonly object _lock = new();
        private readonly Queue<(Complex[] buf, int len, double fs)> _queue = new();
        private readonly List<IWideIqSink> _sinks = new();

        private Thread _worker;
        private volatile bool _running;

        public double LastSampleRate { get; private set; }

        private enum ActiveStream { Unknown, RawIQ, BasebandIQ }
        private volatile ActiveStream _active = ActiveStream.Unknown;

        // Heuristics / stability detection
        private int _rawBadCount = 0;
        private int _rawGoodCount = 0;

        // Tune these if needed
        private const int RawGoodThreshold = 3;   // callbacks with sane fs before we "lock" to Raw
        private const int RawBadThreshold  = 10;  // callbacks with fs<=1 / NaN before we switch to Baseband

        public WideIqSource(ISharpControl control)
        {
            _control = control;

            _rawProc = new WideIqProcessor();
            _rawProc.IQReady += (p, fs, len) => OnIqReady(ActiveStream.RawIQ, p, fs, len);
            _rawProc.Enabled = true;

            _bbProc = new WideIqProcessor();
            _bbProc.IQReady += (p, fs, len) => OnIqReady(ActiveStream.BasebandIQ, p, fs, len);
            _bbProc.Enabled = true;

            // Register both. We will pick the best at runtime.
            _control.RegisterStreamHook(_rawProc, ProcessorType.RawIQ);
            _control.RegisterStreamHook(_bbProc, ProcessorType.BasebandIQ);

            _running = true;
            _worker = new Thread(Worker) { IsBackground = true, Name = "TetraWideIQ" };
            _worker.Start();
        }

        public void AddSink(IWideIqSink sink)
        {
            lock (_lock)
            {
                if (!_sinks.Contains(sink))
                    _sinks.Add(sink);
            }
        }

        public void RemoveSink(IWideIqSink sink)
        {
            lock (_lock)
            {
                _sinks.Remove(sink);
            }
        }

        private void OnIqReady(ActiveStream stream, Complex* samples, double samplerate, int length)
        {
            if (!_running || length <= 0) return;

            // Normalize samplerate if missing/invalid (Airspy RawIQ often hits this)
            if (!IsSaneSampleRate(samplerate))
            {
                var fsFallback = TryGetSampleRateHz(_control);
                if (IsSaneSampleRate(fsFallback))
                    samplerate = fsFallback;
            }

            // Decide active stream
            // 1) If unknown -> try to lock to RawIQ if it looks good quickly; else allow BasebandIQ.
            // 2) If locked to RawIQ but it keeps being bad -> switch to BasebandIQ.
            if (_active == ActiveStream.Unknown)
            {
                if (stream == ActiveStream.RawIQ)
                {
                    if (IsSaneSampleRate(samplerate)) _rawGoodCount++;
                    else _rawBadCount++;

                    if (_rawGoodCount >= RawGoodThreshold)
                        _active = ActiveStream.RawIQ;

                    // If RawIQ seems broken, allow BasebandIQ to take over
                    if (_rawBadCount >= RawBadThreshold)
                        _active = ActiveStream.BasebandIQ;
                }
                else if (stream == ActiveStream.BasebandIQ)
                {
                    // If baseband is sane and RawIQ is not proving itself, we can go baseband
                    if (IsSaneSampleRate(samplerate) && _rawGoodCount == 0 && _rawBadCount >= 3)
                        _active = ActiveStream.BasebandIQ;

                    // Also: if baseband is the first sane stream we see, pick it.
                    if (IsSaneSampleRate(samplerate) && _rawGoodCount == 0 && _rawBadCount == 0)
                        _active = ActiveStream.BasebandIQ;
                }
            }
            else if (_active == ActiveStream.RawIQ)
            {
                // If we are using RawIQ but it's now consistently invalid, fall back
                if (stream == ActiveStream.RawIQ)
                {
                    if (!IsSaneSampleRate(samplerate)) _rawBadCount++;
                    else _rawBadCount = 0;

                    if (_rawBadCount >= RawBadThreshold)
                        _active = ActiveStream.BasebandIQ;
                }
            }

            // Only forward samples from the active stream
            if (_active != stream)
                return;

            if (!IsSaneSampleRate(samplerate))
                return; // Don't poison downstream DDC with 0/NaN

            LastSampleRate = samplerate;

            // Copy samples into managed buffer (pooled)
            var arr = ArrayPool<Complex>.Shared.Rent(length);
            for (int i = 0; i < length; i++)
                arr[i] = samples[i];

            lock (_lock)
            {
                _queue.Enqueue((arr, length, samplerate));
                Monitor.Pulse(_lock);

                // Limit backlog
                while (_queue.Count > 8)
                {
                    var old = _queue.Dequeue();
                    ArrayPool<Complex>.Shared.Return(old.buf);
                }
            }
        }

        private static bool IsSaneSampleRate(double fs)
        {
            if (double.IsNaN(fs) || double.IsInfinity(fs)) return false;
            // sanity: >= 8 kHz and <= 50 MHz (very generous bounds)
            return fs >= 8000 && fs <= 50_000_000;
        }

        private static double TryGetSampleRateHz(ISharpControl control)
        {
            if (control == null) return 0;

            // Common property names on the control itself
            var t = control.GetType();
            foreach (var name in new[]
            {
                "SampleRate",
                "InputSampleRate",
                "BasebandSampleRate",
                "SamplingRate",
                "DeviceSampleRate",
                "OutputSampleRate"
            })
            {
                var pi = t.GetProperty(name);
                if (pi == null) continue;

                try
                {
                    var v = pi.GetValue(control, null);
                    if (v is int i) return i;
                    if (v is long l) return l;
                    if (v is double d) return d;
                    if (v is float f) return f;
                }
                catch { }
            }

            // Some builds expose underlying source/front-end via Source/Frontend/Device
            var srcProp = t.GetProperty("Source") ?? t.GetProperty("Frontend") ?? t.GetProperty("Device");
            if (srcProp != null)
            {
                try
                {
                    var src = srcProp.GetValue(control, null);
                    if (src != null)
                    {
                        var st = src.GetType();
                        foreach (var name in new[]
                        {
                            "SampleRate",
                            "InputSampleRate",
                            "BasebandSampleRate",
                            "SamplingRate",
                            "DeviceSampleRate",
                            "OutputSampleRate"
                        })
                        {
                            var pi = st.GetProperty(name);
                            if (pi == null) continue;

                            try
                            {
                                var v = pi.GetValue(src, null);
                                if (v is int i) return i;
                                if (v is long l) return l;
                                if (v is double d) return d;
                                if (v is float f) return f;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            return 0;
        }

        private void Worker()
        {
            while (_running)
            {
                (Complex[] buf, int len, double fs) item;
                List<IWideIqSink> sinksSnapshot;

                lock (_lock)
                {
                    while (_queue.Count == 0 && _running)
                        Monitor.Wait(_lock, 200);

                    if (!_running) break;
                    if (_queue.Count == 0) continue;

                    item = _queue.Dequeue();
                    sinksSnapshot = new List<IWideIqSink>(_sinks);
                }

                fixed (Complex* p = item.buf)
                {
                    for (int i = 0; i < sinksSnapshot.Count; i++)
                    {
                        try { sinksSnapshot[i].OnWideIq(p, item.fs, item.len); }
                        catch { /* isolate channel failures */ }
                    }
                }

                ArrayPool<Complex>.Shared.Return(item.buf);
            }
        }

        public void Dispose()
        {
            _running = false;
            lock (_lock) { Monitor.PulseAll(_lock); }
            try { _worker?.Join(500); } catch { }

            // SDR# doesn't provide an unregister hook in the public API.
            // We simply stop dispatching.
        }
    }

    public unsafe interface IWideIqSink
    {
        void OnWideIq(Complex* samples, double samplerate, int length);
    }
}
