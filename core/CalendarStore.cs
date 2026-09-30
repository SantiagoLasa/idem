using System;
using System.Collections.Generic;

namespace Idem.Core
{
    // Guarda el P&L de cada día de trading por cuenta (realized+unrealized de la sesión) y lo
    // suma para el total del día. Se guarda DIRECTO — sin deltas ni baseline — así el primer
    // día que operás ya muestra el profit, aunque Idem arranque tarde o reinicies NT8. La
    // cáscara NT8 sobrescribe el valor del día en curso en cada tick (el último = el cierre).
    public sealed class CalendarStore
    {
        // cuenta → (día → P&L de ese día).
        private readonly Dictionary<string, SortedDictionary<DateTime, double>> _byAccount =
            new Dictionary<string, SortedDictionary<DateTime, double>>();

        public void Record(string account, DateTime date, double dayPnl)
        {
            if (!_byAccount.TryGetValue(account, out var m))
            {
                m = new SortedDictionary<DateTime, double>();
                _byAccount[account] = m;
            }
            m[date.Date] = dayPnl;
        }

        public double DailyPnl(DateTime date)
        {
            date = date.Date;
            double total = 0;
            foreach (var m in _byAccount.Values)
                if (m.TryGetValue(date, out double pnl)) total += pnl;
            return total;
        }

        public List<DaySnapshot> Snapshots()
        {
            var list = new List<DaySnapshot>();
            foreach (var acct in _byAccount)
                foreach (var kv in acct.Value)
                    list.Add(new DaySnapshot(acct.Key, kv.Key, kv.Value));
            return list;
        }

        public Dictionary<DateTime, double> Totals()
        {
            var dates = new HashSet<DateTime>();
            foreach (var m in _byAccount.Values)
                foreach (var d in m.Keys)
                    dates.Add(d);

            var result = new Dictionary<DateTime, double>();
            foreach (var d in dates)
                result[d] = DailyPnl(d);
            return result;
        }
    }
}
