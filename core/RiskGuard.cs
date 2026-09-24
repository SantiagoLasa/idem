using System;

namespace Idem.Core
{
    // Guard de drawdown liviano: una resta y una comparación. Bloquea SÓLO órdenes
    // que aumentan la exposición absoluta (entradas) cuando la cuenta está a menos
    // de `cushion` dólares de su límite de DD. Las reducciones/salidas SIEMPRE pasan:
    // siempre tenés que poder cerrar. Sin heurístico de stop por instrumento.
    public static class RiskGuard
    {
        public static bool ShouldBlock(int slaveNetBefore, int slaveNetAfter,
            double currentDrawdown, double ddLimit, double cushion)
        {
            bool increasesExposure = Math.Abs(slaveNetAfter) > Math.Abs(slaveNetBefore);
            if (!increasesExposure) return false; // salidas/reducciones nunca se bloquean
            return currentDrawdown + cushion >= ddLimit;
        }
    }
}
