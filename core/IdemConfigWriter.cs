using System.Globalization;
using System.Text;

namespace Idem.Core
{
    // Serializa un IdemConfig de vuelta al formato .txt (inverso de IdemConfig.Parse).
    // Lo usa la UI del dashboard para guardar los cambios de config.
    public static class IdemConfigWriter
    {
        public static string ToText(IdemConfig cfg)
        {
            var sb = new StringBuilder();
            sb.Append("master=").Append(cfg.MasterAccount ?? "").Append('\n');
            sb.Append("enabled=").Append(cfg.Enabled ? "true" : "false").Append('\n');
            foreach (var s in cfg.Slaves)
                sb.Append("slave=").Append(s.Account).Append(',')
                  .Append(s.DailyLossLimit.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }
    }
}
