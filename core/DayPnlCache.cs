using System.Collections.Generic;

namespace Idem.Core
{
    // Cache del P&L del día (realized + unrealized) por cuenta. La cáscara NT8 lo
    // actualiza desde el UI-thread (donde Account.Get funciona); el guard lo lee desde
    // cualquier thread. Simple: NT8 ya resetea RealizedProfitLoss por sesión, así que
    // no hace falta trackear HWM ni resets — es sólo el número que NT8 reporta.
    public sealed class DayPnlCache
    {
        private readonly Dictionary<string, double> _pnl = new Dictionary<string, double>();
        private readonly object _lock = new object();

        public void Update(string account, double dayPnl)
        {
            lock (_lock) _pnl[account] = dayPnl;
        }

        public double Get(string account)
        {
            lock (_lock)
            {
                _pnl.TryGetValue(account, out double v);
                return v;
            }
        }
    }
}
