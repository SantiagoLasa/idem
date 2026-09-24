using System.Collections.Generic;

namespace Idem.Core
{
    public struct FleetInput
    {
        public string Account;
        public bool IsMaster;
        public int Net;
        public double DayPnl;
        public double DailyLossLimit;
    }

    public struct FleetRow
    {
        public string Account;
        public bool IsMaster;
        public int Net;
        public double DayPnl;
        public double DailyLossLimit;
        public bool GuardBlocking;
    }

    // Arma las filas del panel de flota. Marca qué slaves está frenando el guard
    // (perdieron su tope del día). El master nunca se marca. Puro y testeable.
    public static class FleetView
    {
        public static List<FleetRow> Build(IReadOnlyList<FleetInput> rows)
        {
            var result = new List<FleetRow>(rows.Count);
            foreach (var r in rows)
            {
                bool blocking = !r.IsMaster && r.DayPnl <= -r.DailyLossLimit;
                result.Add(new FleetRow
                {
                    Account = r.Account,
                    IsMaster = r.IsMaster,
                    Net = r.Net,
                    DayPnl = r.DayPnl,
                    DailyLossLimit = r.DailyLossLimit,
                    GuardBlocking = blocking
                });
            }
            return result;
        }
    }
}
