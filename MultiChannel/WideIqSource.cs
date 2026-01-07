using SDRSharp.Common;
using SDRSharp.Radio;
using System;
using System.Collections.Generic;

namespace SDRSharp.Tetra.MultiChannel
{
    /// <summary>
    /// Backwards-compatible façade used by existing code.
    /// Internally shares a single RawIQ hook via <see cref="WideIqHub"/> so multiple plugin instances
    /// (added multiple times in Plugins.xml) don't multiply device stream hooks.
    /// </summary>
    public unsafe class WideIqSource : IDisposable
    {
        private readonly WideIqHub _hub;
        private readonly List<IWideIqSink> _ownedSinks = new();

        public double LastSampleRate => _hub.LastSampleRate;

        public WideIqSource(ISharpControl control)
        {
            _hub = WideIqHub.Acquire(control);
        }

        public void AddSink(IWideIqSink sink)
        {
            if (sink == null) return;
            _ownedSinks.Add(sink);
            _hub.AddSink(sink);
        }

        public void RemoveSink(IWideIqSink sink)
        {
            if (sink == null) return;
            _ownedSinks.Remove(sink);
            _hub.RemoveSink(sink);
        }

        public void Dispose()
        {
            // Unsubscribe any sinks owned by this source
            for (int i = 0; i < _ownedSinks.Count; i++)
            {
                try { _hub.RemoveSink(_ownedSinks[i]); } catch { }
            }
            _ownedSinks.Clear();

            WideIqHub.Release();
        }
    }

    public unsafe interface IWideIqSink
    {
        void OnWideIq(Complex* samples, double samplerate, int length);
    }
}
