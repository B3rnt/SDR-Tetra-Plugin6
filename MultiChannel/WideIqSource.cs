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

        // Two processors: prefer RawIQ, fallback to DecimatedAndFilteredIQ
        private readonly WideIqProcessor _rawProc;
        private readonly WideIqProcessor _dfProc;

        private readonly object _lock = new();
        private readonly Queue<(Complex[] buf, int len, double fs)> _queue = new();
        private readonly List<IWideIqSink> _sinks = new();

        private Thread _worker;
        private volatile bool _running;

        public double LastSampleRate { get; private set; }

        private enum ActiveStream { Unknown, RawIQ, DecimatedFilteredIQ }
        private volatile ActiveStream _active = ActiveStream.Unknown;

        // Heuristics / stability detection
        private int _rawBadCount = 0;
        private int _rawGoodCount = 0;

        // Tune these if needed
        private const int RawGoodThreshold = 3;   // sane RawIQ callbacks before we "lock" to Raw
        private const int RawBadThreshold  = 10;  // invalid RawIQ callbacks before we switch

        public WideIqSource(ISharpControl control)
        {
            _control = control;

            _rawProc = new WideIqProcessor();
            _rawProc.IQReady += (p, fs, len) => OnIqReady(ActiveStream.RawIQ, p, fs, len);
            _rawProc.Enabled = true;

            _dfProc = new WideIqProcessor();
            _dfProc.IQReady += (p, fs, len) => OnIqReady(ActiveStream.DecimatedFilteredIQ, p, fs, len);
            _dfProc.Enabled = true;

            // Register both. We pick the best at runtime.
            _control.RegisterStreamHook(_rawProc, ProcessorType.RawIQ);

            // This is the stable "baseband/filtered" IQ in this SDR# SDK:
            _control.RegisterStreamHook(_dfProc, ProcessorType.DecimatedAndFilteredIQ);

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

            // Some SDR# frontends/device plugins may invoke RawIQ hooks with samplerate = 0.
            // Fallback to control/device properties if needed.
            if (!IsSaneSampleRate(samplerate))
            {
                var fsFallback = TryGetSampleRateHz(_control);
                if (IsSaneSampleRate(fsFallback))
                    samplerate = fsFallback;
            }

            // Decide active stream
            if (_active == ActiveStream.Unknown)
            {
                if (stream == ActiveStream.RawIQ)
                {
                    if (IsSaneSampleRate(samplerate)) _rawGoodCount++;
                    else _rawBadCount++;

                    if (_rawGoodCount >= RawGoodThreshold)
                        _active = ActiveStream.RawIQ;

                    if (_rawBadCount >= RawBadThreshold)
                        _active = ActiveStream.DecimatedFilteredIQ;
                }
                else if (stream == ActiveStream.DecimatedFilteredIQ)
                {
                    // If the decimated/filtered stream is sane and RawIQ isn't proving itself, take it.
                    if (IsSaneSampleRate(samplerate) && _rawGoodCount == 0 && _rawBadCount >= 3)
                        _active = ActiveStream.DecimatedFilteredIQ;

                    // Also: if this is the first sane stream we see, pick it.
                    if (IsSaneSampleRate(samplerate) && _rawGoodCount == 0 && _rawBadCount == 0)
                        _active = ActiveStream.DecimatedFilteredIQ;
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
                        _active = ActiveStream.DecimatedFilteredIQ;
                }
            }

            // Only forward samples from the active stream
            if (_active != stream)
                return;

            if (!IsSaneSampleRate(samplerate))
                return; // don't poison downstream DDC with 0/NaN

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
            // sanity bounds
            return fs >= 8000 && fs <= 50_000_000;
        }

        private static double TryGetSampleRateHz(ISharpControl control)
        {
            if (control == null) return 0;

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
        }
    }

    public unsafe interface IWideIqSink
    {
        void OnWideIq(Complex* samples, double samplerate, int length);
    }
}
