using System.Collections.Generic;
using System.Globalization;

namespace Idem.Core
{
    public struct SlaveConfig
    {
        public string Account;
        public double DailyLossLimit;  // pérdida del día a la que frenar entradas
    }

    // Config del copy: master, slaves (con su tope de pérdida diaria), on/off.
    // Formato línea por línea (clave=valor) — sin dependencias de JSON (NT8 net48 no
    // trae System.Text.Json). Fácil de editar a mano:
    //   master=Sim101
    //   enabled=true
    //   slave=SimAccount1,250
    //   slave=SimAccount2,500
    // Líneas vacías y las que empiezan con '#' se ignoran.
    public sealed class IdemConfig
    {
        public string MasterAccount;
        public List<SlaveConfig> Slaves = new List<SlaveConfig>();
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
                    case "enabled":
                        cfg.Enabled = val == "true" || val == "1";
                        break;
                    case "slave":
                        var parts = val.Split(',');
                        if (parts.Length >= 2)
                            cfg.Slaves.Add(new SlaveConfig
                            {
                                Account = parts[0].Trim(),
                                DailyLossLimit = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture)
                            });
                        break;
                }
            }
            return cfg;
        }
    }
}
