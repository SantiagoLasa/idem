namespace Idem.Core
{
    // Corazón del copy: reconciliar el slave a un target conocido, no contar fills.
    // target = tamaño del master (1:1); delta = target - net_real_del_slave.
    // Idempotente: delta 0 → ninguna orden. Entrada y salida son el mismo cálculo:
    // cerrar es reconciliar a un target más chico. El signo del delta decide el lado.
    public static class Reconciler
    {
        public static ReconcileOrder Compute(int masterNet, int slaveNet, int ratio = 1)
        {
            int target = Sizing.SlaveTarget(masterNet, ratio);
            int delta = target - slaveNet;
            if (delta == 0) return new ReconcileOrder { Action = OrderAction.None, Qty = 0 };
            if (delta > 0) return new ReconcileOrder { Action = OrderAction.Buy, Qty = delta };
            return new ReconcileOrder { Action = OrderAction.Sell, Qty = -delta };
        }
    }
}
