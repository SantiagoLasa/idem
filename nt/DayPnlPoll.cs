using System.Collections.Generic;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using Idem.Core;

namespace Idem.Nt
{
    // Lee el P&L del día (realized + unrealized) de cada cuenta en el UI-thread
    // (donde Account.Get funciona; off-thread devuelve 0 — el bug de PropCommand) y
    // alimenta el DayPnlCache. RealizedProfitLoss de NT8 ya resetea por sesión.
    public sealed class DayPnlPoll
    {
        private readonly DayPnlCache _cache;
        private readonly List<Account> _accounts;
        private DispatcherTimer _timer;

        public DayPnlPoll(DayPnlCache cache, List<Account> accounts)
        {
            _cache = cache;
            _accounts = accounts;
        }

        public void Start()
        {
            var disp = System.Windows.Application.Current != null
                ? System.Windows.Application.Current.Dispatcher : null;
            if (disp == null) return;
            disp.InvokeAsync(() =>
            {
                _timer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(1000) };
                _timer.Tick += (s, e) => Poll();
                _timer.Start();
                Poll();
            });
        }

        public void Stop()
        {
            try { _timer?.Stop(); } catch { }
            _timer = null;
        }

        private void Poll()
        {
            foreach (var acc in _accounts)
            {
                try
                {
                    double realized = acc.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                    double unrealized = acc.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                    _cache.Update(acc.Name, realized + unrealized);
                }
                catch { }
            }
        }
    }
}
