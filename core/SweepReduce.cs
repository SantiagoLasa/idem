using System;

namespace Idem.Core
{
    // Red de seguridad "reduce-only": el sweep SÓLO cierra exposición de más del slave (lo que
    // el master ya no tiene), nunca abre. Las entradas las hace exclusivamente el order-mirror.
    // Esto mata la doble-entrada (el sweep adelantándose al fill del límite espejo) que hacía
    // que los slaves sobre-operaran ~3,5x (incidente 2026-10-02). Target = el neto del slave
    // acercado al master pero sólo si reduce |slaveNet|; si no, se queda donde está.
    public static class SweepReduce
    {
        public static int Target(int masterNet, int slaveNet)
        {
            if (slaveNet == 0) return 0;

            bool sameSign = (slaveNet > 0 && masterNet > 0) || (slaveNet < 0 && masterNet < 0);
            if (sameSign)
            {
                int mag = Math.Min(Math.Abs(slaveNet), Math.Abs(masterNet));
                return slaveNet > 0 ? mag : -mag;
            }

            return 0; // master flat o signo opuesto → cerrar el slave (nunca abrir la contraria)
        }
    }
}
