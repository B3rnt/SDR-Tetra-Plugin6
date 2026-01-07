using SDRSharp.Common;
using System.Threading;
using System.Windows.Forms;
using SDRSharp.Tetra.MultiChannel;

namespace SDRSharp.Tetra
{
    /// <summary>
    /// Instance plugin: can be added multiple times to Plugins.xml.
    /// Each instance has its own channel list/settings file, while sharing a single RawIQ hook via WideIqHub.
    /// </summary>
    public class TetraInstancePlugin : ISharpPlugin
    {
        private static int _counter;

        private readonly int _instanceNumber;
        private ISharpControl _controlInterface;
        private TetraMultiPanel _panel;

        public TetraInstancePlugin()
        {
            _instanceNumber = Interlocked.Increment(ref _counter);
        }

        public UserControl Gui => _panel;

        public string DisplayName => $"TETRA Demodulator #{_instanceNumber}";

        public void Initialize(ISharpControl control)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            _controlInterface = control;
            // instance-specific settings
            _panel = new TetraMultiPanel(_controlInterface, $"instance{_instanceNumber}");
        }

        public void Close()
        {
            _panel?.Shutdown();
        }
    }
}
