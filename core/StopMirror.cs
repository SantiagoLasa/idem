namespace Idem.Core
{
    public enum StopActionKind { None, Place, Cancel }

    public struct StopAction
    {
        public StopActionKind Kind;
        public double Price;
        public int Qty;
    }

    // Reconciler del stop de protección: dado el stop del master y el estado del slave,
    // decide colocar / cancelar / nada. Idempotente y por barrido → sin huérfanos ni OCO.
    // Modificar = Place (el executor cancela el viejo antes de colocar). |slaveNet| = qty.
    public static class StopMirror
    {
        public static StopAction Decide(bool hasMasterStop, double masterStopPrice,
            int slaveNet, bool hasSlaveStop, double slaveStopPrice, int slaveStopQty)
        {
            int absNet = slaveNet < 0 ? -slaveNet : slaveNet;

            if (absNet == 0 || !hasMasterStop)
                return hasSlaveStop
                    ? new StopAction { Kind = StopActionKind.Cancel }
                    : new StopAction { Kind = StopActionKind.None };

            if (hasSlaveStop && slaveStopPrice == masterStopPrice && slaveStopQty == absNet)
                return new StopAction { Kind = StopActionKind.None };

            return new StopAction { Kind = StopActionKind.Place, Price = masterStopPrice, Qty = absNet };
        }
    }
}
