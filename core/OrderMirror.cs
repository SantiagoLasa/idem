using System;

namespace Idem.Core
{
    public enum MirrorSide { Buy, Sell }

    public struct MirrorDecision
    {
        public bool Place;
        public int Qty;

        public MirrorDecision(bool place, int qty)
        {
            Place = place;
            Qty = qty;
        }
    }

    // Decisión pura del copy a nivel de orden: dado una orden del master y el estado de un slave,
    // decide si colocar la orden espejo (y con qué cantidad) o saltearla por el guard. Las
    // entradas (aumentan exposición) se bloquean si el slave está pasado de su tope diario; las
    // salidas nunca. El mapeo de OCO permite linkear las patas de un bracket por slave.
    public static class OrderMirrorDecision
    {
        public static bool IsEntryOrder(MirrorSide side, int slaveNet)
        {
            // Buy agranda exposición cuando el net es >= 0 (flat o long); si está short, cubre.
            // Sell es simétrico.
            return side == MirrorSide.Buy ? slaveNet >= 0 : slaveNet <= 0;
        }

        public static MirrorDecision Decide(MirrorSide side, int masterQty, int slaveNet,
            double slaveDayPnl, double slaveDailyLossLimit, double ratio)
        {
            bool blocked = slaveDayPnl <= -slaveDailyLossLimit;
            if (IsEntryOrder(side, slaveNet) && blocked)
                return new MirrorDecision(false, 0);

            int qty = (int)Math.Round(masterQty * ratio);
            return new MirrorDecision(true, qty);
        }

        public static string SlaveOco(string masterOco, string slaveName)
        {
            if (string.IsNullOrEmpty(masterOco)) return string.Empty;
            return masterOco + "|" + slaveName;
        }
    }
}
