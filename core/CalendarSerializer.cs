using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Idem.Core
{
    // Persistencia línea-por-línea del calendario (como idem-config.txt, sin JSON — NT8
    // net48 no trae System.Text.Json). Formato: snap=<cuenta>|<yyyy-MM-dd>|<netLiq>.
    // Cultura invariante para que el decimal no dependa del locale de la máquina.
    public static class CalendarSerializer
    {
        public static List<NetLiqSnapshot> Parse(string text)
        {
            var list = new List<NetLiqSnapshot>();
            if (string.IsNullOrEmpty(text)) return list;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (!line.StartsWith("snap=")) continue;

                var parts = line.Substring(5).Split('|');
                if (parts.Length != 3) continue;
                if (!DateTime.TryParseExact(parts[1], "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                if (!double.TryParse(parts[2], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var netLiq)) continue;

                list.Add(new NetLiqSnapshot(parts[0], date, netLiq));
            }
            return list;
        }

        public static string ToText(IEnumerable<NetLiqSnapshot> snapshots)
        {
            var sb = new StringBuilder();
            foreach (var s in snapshots)
            {
                sb.Append("snap=").Append(s.Account).Append('|')
                  .Append(s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
                  .Append(s.NetLiq.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return sb.ToString();
        }
    }
}
