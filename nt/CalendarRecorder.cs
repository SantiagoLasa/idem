using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using Idem.Core;

namespace Idem.Nt
{
    // Graba el P&L del día (realized+unrealized de la sesión) de cada cuenta por día de
    // trading (corte 5pm ET) en el CalendarStore, leyendo en el UI-thread (donde Account.Get
    // funciona; off-thread da 0). En cada tick sobrescribe el valor del día en curso, así el
    // guardado es siempre la última lectura de ese día = su cierre, aun si NT8 se cierra a
    // mitad de sesión. Persiste al .txt cada tanto y al cambiar de día. Guardar el P&L directo
    // (no net-liq con delta) hace que el primer día ya muestre el profit sin necesitar baseline.
    public sealed class CalendarRecorder
    {
        private const int TickMs = 10000;      // 10s
        private const int PersistEveryTicks = 6; // ~60s

        private readonly CalendarStore _store;
        private readonly List<Account> _accounts;
        private readonly string _path;
        private DispatcherTimer _timer;
        private DateTime _currentDay;
        private int _ticks;

        public CalendarRecorder(CalendarStore store, List<Account> accounts, string path)
        {
            _store = store;
            _accounts = accounts;
            _path = path;
            _currentDay = TradingDay.EtDate(DateTime.UtcNow);
        }

        public void Start()
        {
            var disp = System.Windows.Application.Current != null
                ? System.Windows.Application.Current.Dispatcher : null;
            if (disp == null) return;
            disp.InvokeAsync(() =>
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
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
            var today = TradingDay.EtDate(DateTime.UtcNow);
            foreach (var acc in _accounts)
            {
                try
                {
                    double realized = acc.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                    double unrealized = acc.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                    _store.Record(acc.Name, today, realized + unrealized);
                }
                catch { }
            }

            bool rolled = today != _currentDay;
            if (rolled) _currentDay = today;

            if (rolled || (++_ticks % PersistEveryTicks) == 0)
                Persist();
        }

        private void Persist()
        {
            try { File.WriteAllText(_path, CalendarSerializer.ToText(_store.Snapshots())); }
            catch { }
        }
    }
}
