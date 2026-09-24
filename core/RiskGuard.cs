using System;

namespace Idem.Core
{
    // Stop de pérdida diaria: bloquea entradas NUEVAS cuando el P&L del día
    // (realized + unrealized) llegó a -dailyLossLimit. Ej: dailyLossLimit=250 →
    // bloquea a dayPnl <= -250. NO es proximidad al drawdown: es un tope de pérdida
    // del día para dejar de operar. Las reducciones/salidas SIEMPRE pasan.
    public static class RiskGuard
    {
        public static bool ShouldBlock(int slaveNetBefore, int slaveNetAfter,
            double dayPnl, double dailyLossLimit)
        {
            bool increasesExposure = Math.Abs(slaveNetAfter) > Math.Abs(slaveNetBefore);
            if (!increasesExposure) return false; // salidas/reducciones nunca se bloquean
            return dayPnl <= -dailyLossLimit;
        }
    }
}
