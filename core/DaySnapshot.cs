using System;

namespace Idem.Core
{
    // P&L de un día de trading para una cuenta (realized+unrealized de la sesión, la misma
    // fuente que hace correcto el "hoy"). Se guarda directo por día — sin deltas ni baseline,
    // así el primer día que operás ya muestra el profit aunque Idem arranque tarde o reinicies.
    public struct DaySnapshot
    {
        public string Account;
        public DateTime Date;
        public double Pnl;

        public DaySnapshot(string account, DateTime date, double pnl)
        {
            Account = account;
            Date = date;
            Pnl = pnl;
        }
    }
}
