using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace SDRSharp.Tetra.MultiChannel
{
    public static class ChannelSettingsStore
    {
        private const string DefaultFileName = "SDRSharp.Tetra.MultiChannels.xml";

        private static string GetFileName(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
                return DefaultFileName;

            // Keep it filesystem-safe
            foreach (var c in Path.GetInvalidFileNameChars())
                instanceId = instanceId.Replace(c, '_');

            return $"SDRSharp.Tetra.MultiChannels.{instanceId}.xml";
        }

        public static string GetSettingsPath(string instanceId = null)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SDRSharp");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, GetFileName(instanceId));
        }

        public static List<ChannelSettings> Load(string instanceId = null)
        {
            var path = GetSettingsPath(instanceId);
            if (!File.Exists(path))
                return new List<ChannelSettings>();

            try
            {
                using var fs = File.OpenRead(path);
                var ser = new XmlSerializer(typeof(List<ChannelSettings>));
                return (List<ChannelSettings>)ser.Deserialize(fs);
            }
            catch
            {
                return new List<ChannelSettings>();
            }
        }

        public static void Save(List<ChannelSettings> channels, string instanceId = null)
        {
            var path = GetSettingsPath(instanceId);
            using var fs = File.Create(path);
            var ser = new XmlSerializer(typeof(List<ChannelSettings>));
            ser.Serialize(fs, channels);
        }
    }
}
