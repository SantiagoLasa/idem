using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using IdemCore = Idem.Core;

namespace Idem.Nt
{
    // Manda una market a un slave. Serializado por (slave|instrumento): el evento de
    // fill y el safety sweep pueden reconciliar el mismo par a la vez; el lock envuelve
    // el submit para que nunca se pisen (mata la ventana de reversión cross-thread).
    public static class OrderSubmit
    {
        private static readonly Dictionary<string, object> _locks = new Dictionary<string, object>();

        private static object LockFor(string key)
        {
            lock (_locks)
            {
                if (!_locks.TryGetValue(key, out var l)) { l = new object(); _locks[key] = l; }
                return l;
            }
        }

        public static void Market(Account slave, Instrument instrument, IdemCore.OrderAction action, int qty)
        {
            if (slave == null || instrument == null || qty <= 0 || action == IdemCore.OrderAction.None) return;

            OrderAction ntAction = action == IdemCore.OrderAction.Buy ? OrderAction.Buy : OrderAction.Sell;
            string key = slave.Name + "|" + instrument.FullName;

            lock (LockFor(key))
            {
                var order = slave.CreateOrder(
                    instrument, ntAction, OrderType.Market, OrderEntry.Manual,
                    TimeInForce.Day, qty, 0, 0, string.Empty,
                    "Idem", DateTime.MaxValue, null);
                slave.Submit(new[] { order });
            }
        }
    }
}
