using System;

namespace Idem.Core
{
    // Snapshot de cierre de net-liquidation de una cuenta en un día de trading.
    // El P&L del día se calcula como delta día-a-día de estos cierres.
    public struct NetLiqSnapshot
    {
        public string Account;
        public DateTime Date;
        public double NetLiq;

        public NetLiqSnapshot(string account, DateTime date, double netLiq)
        {
            Account = account;
            Date = date;
            NetLiq = netLiq;
        }
    }
}
