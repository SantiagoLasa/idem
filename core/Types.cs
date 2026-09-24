namespace Idem.Core
{
    public enum OrderAction { None, Buy, Sell }

    public struct ReconcileOrder
    {
        public OrderAction Action;
        public int Qty;
    }
}
