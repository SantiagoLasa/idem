using System.Linq;
using NinjaTrader.Cbi;
using Idem.Core;
using NtOrderAction = NinjaTrader.Cbi.OrderAction;

namespace Idem.Nt
{
    // Espeja el stop de protección: por barrido, cada slave con posición debe tener UN
    // stop Idem al precio del stop del master. Reconcile-style (idempotente): sin huérfanos.
    // El stop del master lo pone el usuario; Idem sólo lo copia. Modificar = cancel+place.
    public sealed class StopExecutor
    {
        private const string StopTag = "IdemStop";
        private readonly PositionTracker _tracker;
        private readonly IdemConfig _cfg;
        private readonly System.Func<string, Account> _resolve;
        private readonly object _lock = new object();

        public StopExecutor(PositionTracker tracker, IdemConfig cfg, System.Func<string, Account> resolve)
        {
            _tracker = tracker; _cfg = cfg; _resolve = resolve;
        }

        public void ReconcileStops(Instrument instrument)
        {
            lock (_lock)
            {
                var master = _resolve(_cfg.MasterAccount);
                if (master == null) return;

                Order masterStop = WorkingStop(master, instrument, requireTag: false);
                bool hasMasterStop = masterStop != null;
                double masterStopPrice = hasMasterStop ? masterStop.StopPrice : 0;

                foreach (var sc in _cfg.Slaves)
                {
                    var slave = _resolve(sc.Account);
                    if (slave == null) continue;

                    int net = _tracker.Net(sc.Account + "|" + instrument.FullName);
                    Order slaveStop = WorkingStop(slave, instrument, requireTag: true);
                    bool hasSlaveStop = slaveStop != null;
                    double slavePrice = hasSlaveStop ? slaveStop.StopPrice : 0;
                    int slaveQty = hasSlaveStop ? slaveStop.Quantity : 0;

                    var act = StopMirror.Decide(hasMasterStop, masterStopPrice, net,
                        hasSlaveStop, slavePrice, slaveQty);

                    if (act.Kind == StopActionKind.Cancel && slaveStop != null)
                        Cancel(slave, slaveStop);
                    else if (act.Kind == StopActionKind.Place)
                    {
                        if (slaveStop != null) Cancel(slave, slaveStop);
                        Place(slave, instrument, net, act.Price, act.Qty);
                    }
                }
            }
        }

        private static Order WorkingStop(Account acc, Instrument instrument, bool requireTag)
        {
            try
            {
                lock (acc.Orders)
                {
                    return acc.Orders.FirstOrDefault(o =>
                        o.OrderType == OrderType.StopMarket
                        && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted)
                        && o.Instrument == instrument
                        && (!requireTag || o.Name == StopTag));
                }
            }
            catch { return null; }
        }

        private void Place(Account slave, Instrument instrument, int net, double price, int qty)
        {
            double p = instrument.MasterInstrument.RoundToTickSize(price);
            if (p <= 0 || qty <= 0) return;
            // long → stop Sell; short → stop BuyToCover.
            NtOrderAction action = net > 0 ? NtOrderAction.Sell : NtOrderAction.BuyToCover;
            var order = slave.CreateOrder(
                instrument, action, OrderType.StopMarket, OrderEntry.Manual,
                TimeInForce.Day, qty, 0, p, string.Empty, StopTag, System.DateTime.MaxValue, null);
            slave.Submit(new[] { order });
        }

        private void Cancel(Account slave, Order order)
        {
            try { slave.Cancel(new[] { order }); } catch { }
        }
    }
}
