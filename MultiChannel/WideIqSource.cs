using SDRSharp.Common;
using SDRSharp.Radio;
using System;
using System.Collections.Generic;

namespace SDRSharp.Tetra.MultiChannel
{
    /// <summary>
    /// Wideband IQ source for the multichannel decoder.
    ///
    /// IMPORTANT: For true multichannel operation the stream must be PRE-VFO.
    /// If you hook a post-VFO stream (e.g. DecimatedAndFilteredIQ), SDR# will
    /// frequency-shift the signal based on the currently selected VFO. That
    /// makes *all* channels follow whatever you click in the waterfall.
    ///
    /// We therefore hook ProcessorType.RawIQ and do per-channel DDC in each sink.
    ///
    /// Performance: this class does NOT copy IQ buffers. It dispatches directly
    /// to sinks on the SDR# callback thread. Sinks must process synchronously and
    /// must not store the pointer after returning.
    /// </summary>
    public unsafe class WideIqSource : IDisposable
    {
        private readonly ISharpControl _control;
        private readonly WideIqProcessor _proc;

        private readonly object _lock = new();
        private readonly List<IWideIqSink> _sinks = new();

        public double LastSampleRate { get; private set; }

        public WideIqSource(ISharpControl control)
        {
            _control = control;
            _proc = new WideIqProcessor();
            _proc.IQReady += OnIqReady;
            _proc.Enabled = true;

            // Pre-VFO stream required for true multichannel
            _control.RegisterStreamHook(_proc, ProcessorType.RawIQ);
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

        private void OnIqReady(Complex* samples, double samplerate, int length)
        {
            if (length <= 0) return;

            // Some SDR# builds/devices don't populate IIQProcessor.SampleRate for RawIQ.
            // Fall back to probing common control properties.
            if (!IsSaneSampleRate(samplerate))
            {
                var sr = TryGetSampleRateHz(_control);
                if (IsSaneSampleRate(sr))
                    samplerate = sr;
            }

            if (!IsSaneSampleRate(samplerate))
                return;

            LastSampleRate = samplerate;

            IWideIqSink[] snapshot;
            lock (_lock)
            {
                if (_sinks.Count == 0) return;
                snapshot = _sinks.ToArray();
            }

            // Dispatch directly; no copying.
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i].OnWideIq(samples, samplerate, length); }
                catch { /* isolate channel failures */ }
            }
        }

        private static bool IsSaneSampleRate(double fs)
        {
            if (double.IsNaN(fs) || double.IsInfinity(fs)) return false;
            return fs >= 8_000 && fs <= 50_000_000;
        }

        private static double TryGetSampleRateHz(ISharpControl control)
        {
            if (control == null) return 0;

            try
            {
                var t = control.GetType();

                // SDR# Common exposes InputSampleRate in many builds.
                foreach (var name in new[]
                {
                    "InputSampleRate",
                    "SampleRate",
                    "Samplerate",
                    "DeviceSampleRate",
                    "RadioSampleRate",
                    "IFSampleRate"
                })
                {
                    var p = t.GetProperty(name);
                    if (p == null) continue;
                    var v = p.GetValue(control, null);
                    if (v is int i) return i;
                    if (v is long l) return l;
                    if (v is double d) return d;
                    if (v is float f) return f;
                }

                // Heuristic: any property containing "Sample" and "Rate".
                foreach (var p in t.GetProperties())
                {
                    var n = p.Name;
                    if (n.IndexOf("Sample", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        n.IndexOf("Rate", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var v = p.GetValue(control, null);
                        if (v is int i) return i;
                        if (v is long l) return l;
                        if (v is double d) return d;
                        if (v is float f) return f;
                    }
                }
            }
            catch { }

            return 0;
        }

        public void Dispose()
        {
            // SDR# doesn't provide an unregister hook in the public API.
            // We just stop dispatching by clearing sinks and detaching.
            try { _proc.IQReady -= OnIqReady; } catch { }
            lock (_lock) { _sinks.Clear(); }
        }
    }

    public unsafe interface IWideIqSink
    {
        void OnWideIq(Complex* samples, double samplerate, int length);
    }
}
