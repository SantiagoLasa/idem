using System.Collections.Generic;
using System.Globalization;

namespace Idem.Core
{
    public struct SlaveConfig
    {
        public string Account;
        public double DdLimit;
    }

    // Config del copy: master, slaves (con su límite de DD), colchón del guard, on/off.
    // Formato línea por línea (clave=valor) — sin dependencias de JSON, así compila en
    // NT8 net48 (que no trae System.Text.Json) y en net8.0 (test). Fácil de editar a mano:
    //   master=Sim101
    //   cushion=400
    //   enabled=true
    //   slave=SimAccount1,2500
    //   slave=SimAccount2,3000
    // Líneas vacías y las que empiezan con '#' se ignoran.
    public sealed class IdemConfig
    {
        public string MasterAccount;
        public List<SlaveConfig> Slaves = new List<SlaveConfig>();
        public double Cushion;
        public bool Enabled;

        public static IdemConfig Parse(string text)
        {
            var cfg = new IdemConfig();
            if (text == null) return cfg;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "master":
                        cfg.MasterAccount = val;
                        break;
                    case "cushion":
                        cfg.Cushion = double.Parse(val, CultureInfo.InvariantCulture);
                        break;
                    case "enabled":
                        cfg.Enabled = val == "true" || val == "1";
                        break;
                    case "slave":
                        var parts = val.Split(',');
                        if (parts.Length >= 2)
                            cfg.Slaves.Add(new SlaveConfig
                            {
                                Account = parts[0].Trim(),
                                DdLimit = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture)
                            });
                        break;
                }
            }
            return cfg;
        }
    }
}
