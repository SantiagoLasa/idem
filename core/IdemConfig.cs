using System.Collections.Generic;
using System.Text.Json;

namespace Idem.Core
{
    public struct SlaveConfig
    {
        public string Account;
        public double DdLimit;
    }

    // Config del copy: master, slaves (con su límite de DD), colchón del guard, on/off.
    // Parse sin estado; la cáscara NT8 lee el archivo y llama Parse.
    public sealed class IdemConfig
    {
        public string MasterAccount;
        public List<SlaveConfig> Slaves = new List<SlaveConfig>();
        public double Cushion;
        public bool Enabled;

        public static IdemConfig Parse(string json)
        {
            var cfg = new IdemConfig();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            cfg.MasterAccount = root.GetProperty("master").GetString();
            cfg.Cushion = root.GetProperty("cushion").GetDouble();
            cfg.Enabled = root.GetProperty("enabled").GetBoolean();
            foreach (var s in root.GetProperty("slaves").EnumerateArray())
                cfg.Slaves.Add(new SlaveConfig
                {
                    Account = s.GetProperty("account").GetString(),
                    DdLimit = s.GetProperty("ddLimit").GetDouble()
                });
            return cfg;
        }
    }
}
