using System;
using System.Collections.Generic;

namespace Idem.Core
{
    // Guarda los snapshots de cierre de net-liq por (cuenta, día) y calcula el P&L diario
    // como delta día-a-día del cierre. P&L = cierre[hoy] − cierre[último día registrado de
    // esa cuenta]. Un delta mayor a fundingGuard se ignora (depósito/retiro/cuenta nueva,
    // no es P&L). Puro y testeable; la cáscara NT8 lo puebla desde el UI-thread.
    public sealed class CalendarStore
    {
        // cuenta → (día de cierre → net-liq). SortedDictionary enumera por fecha ascendente,
        // así que encontrar el día anterior registrado es un recorrido lineal simple.
        private readonly Dictionary<string, SortedDictionary<DateTime, double>> _byAccount =
            new Dictionary<string, SortedDictionary<DateTime, double>>();

        public void Record(string account, DateTime date, double netLiq)
        {
            if (!_byAccount.TryGetValue(account, out var m))
            {
                m = new SortedDictionary<DateTime, double>();
                _byAccount[account] = m;
            }
            m[date.Date] = netLiq;
        }

        public double DailyPnl(DateTime date, double fundingGuard)
        {
            date = date.Date;
            double total = 0;
            foreach (var m in _byAccount.Values)
            {
                if (!m.TryGetValue(date, out double close)) continue;
                DateTime? prev = null;
                foreach (var d in m.Keys)
                {
                    if (d < date) prev = d;
                    else break;
                }
                if (prev == null) continue; // primer día registrado → no es P&L
                double delta = close - m[prev.Value];
                if (System.Math.Abs(delta) > fundingGuard) continue; // funding/reset, no P&L
                total += delta;
            }
            return total;
        }

        public List<NetLiqSnapshot> Snapshots()
        {
            var list = new List<NetLiqSnapshot>();
            foreach (var acct in _byAccount)
                foreach (var kv in acct.Value)
                    list.Add(new NetLiqSnapshot(acct.Key, kv.Key, kv.Value));
            return list;
        }

        public Dictionary<DateTime, double> Totals(double fundingGuard)
        {
            var dates = new HashSet<DateTime>();
            foreach (var m in _byAccount.Values)
                foreach (var d in m.Keys)
                    dates.Add(d);

            var result = new Dictionary<DateTime, double>();
            foreach (var d in dates)
                result[d] = DailyPnl(d, fundingGuard);
            return result;
        }
    }
}
