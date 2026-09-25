using System;

namespace Idem.Core
{
    public enum HeatLevel { Loss, Flat, Gain }

    // Coloreo puro del calendario: nivel por signo del P&L e intensidad 0..1 para el
    // degradé, escalada contra el máximo absoluto del mes.
    public static class Heatmap
    {
        public static HeatLevel Bucket(double pnl)
        {
            if (pnl > 0) return HeatLevel.Gain;
            if (pnl < 0) return HeatLevel.Loss;
            return HeatLevel.Flat;
        }

        public static double Intensity(double pnl, double maxAbs)
        {
            if (maxAbs <= 0) return 0;
            double v = Math.Abs(pnl) / maxAbs;
            return v > 1 ? 1 : v;
        }
    }
}
