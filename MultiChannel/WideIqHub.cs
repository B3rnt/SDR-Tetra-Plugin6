using SDRSharp.Common;
using SDRSharp.Radio;
using System;
using System.Collections.Generic;

namespace SDRSharp.Tetra.MultiChannel
{
    /// <summary>
    /// Shared wideband IQ hub.
    /// Registers exactly one stream hook per SDR# process (per ISharpControl instance),
    /// and fan-outs incoming IQ buffers to subscribed sinks without copying.
    /// This enables multiple plugin instances (via Plugins.xml) without multiplying RawIQ hooks.
    /// </summary>
    public unsafe sealed class WideIqHub : IDisposable
    {
        private static readonly object _staticLock = new();
        private static WideIqHub _instance;
        private static int _refCount;

        public static WideIqHub Acquire(ISharpControl control)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));

            lock (_staticLock)
            {
                if (_instance == null)
                    _instance = new WideIqHub(control);

                _refCount++;
                return _instance;
            }
        }

        public static void Release()
        {
            lock (_staticLock)
            {
                if (_refCount > 0) _refCount--;
                if (_refCount == 0 && _instance != null)
                {
                    _instance.Dispose();
                    _instance = null;
                }
            }
        }

        private readonly ISharpControl _control;
        private readonly WideIqProcessor _proc;

        private readonly object _lock = new();
        private IWideIqSink[] _sinks = Array.Empty<IWideIqSink>();

        public double LastSampleRate { get; private set; }

        private WideIqHub(ISharpControl control)
        {
            _control = control;

            _proc = new WideIqProcessor { Enabled = true };
            _proc.IQReady += OnIqReady;

            // Pre-VFO stream. This is required for true multichannel: offsets are computed from center frequency.
            _control.RegisterStreamHook(_proc, ProcessorType.RawIQ);
        }

        public void AddSink(IWideIqSink sink)
        {
            if (sink == null) return;
            lock (_lock)
            {
                var list = new List<IWideIqSink>(_sinks);
                if (!list.Contains(sink))
                {
                    list.Add(sink);
                    _sinks = list.ToArray();
                }
            }
        }

        public void RemoveSink(IWideIqSink sink)
        {
            if (sink == null) return;
            lock (_lock)
            {
                var list = new List<IWideIqSink>(_sinks);
                if (list.Remove(sink))
                    _sinks = list.ToArray();
            }
        }

        private void OnIqReady(Complex* samples, double samplerate, int length)
        {
            if (length <= 0) return;

            if (samplerate <= 1 || double.IsNaN(samplerate) || double.IsInfinity(samplerate))
            {
                var fs = TryGetSampleRateHz(_control);
                if (fs > 1) samplerate = fs;
                else return;
            }

            LastSampleRate = samplerate;

            // Snapshot sinks without holding lock during DSP
            var sinks = _sinks;
            for (int i = 0; i < sinks.Length; i++)
            {
                try { sinks[i].OnWideIq(samples, samplerate, length); }
                catch { /* isolate channel failures */ }
            }
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

            // Some builds expose the underlying source/front-end via Source/Frontend/Device
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

        public void Dispose()
        {
            // No public unregister hook in SDR# SDK; we just stop dispatching.
            _proc.Enabled = false;
            _proc.IQReady -= OnIqReady;

            lock (_lock)
                _sinks = Array.Empty<IWideIqSink>();
        }
    }
}
