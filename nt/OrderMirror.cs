using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using Idem.Core;
using NtOrderAction = NinjaTrader.Cbi.OrderAction;

namespace Idem.Nt
{
    // Copy a nivel de ORDEN: espeja la orden del master cuando la manda (OrderUpdate), no
    // cuando se llena. Market → todas al mismo instante; límite/TP → al mismo precio; bracket →
    // OCO espejado por slave. Modificar = cancel+place (patrón de StopExecutor). El guard
    // saltea entradas de un slave pasado de tope. El SweepTick del CopyEngine queda de red de
    // seguridad para el neto residual. Suscribe sólo tras Connected (poll, como el FillMonitor).
    public sealed class OrderMirror
    {
        private readonly IdemConfig _cfg;
        private readonly Func<string, Account> _resolve;
        private readonly Func<Account, double> _dayPnl;
        private readonly Action<Instrument> _onInstrument;
        private readonly object _lock = new object();

        // orderId del master → (slave → orden espejo)
        private readonly Dictionary<string, Dictionary<string, Order>> _map =
            new Dictionary<string, Dictionary<string, Order>>();
        // snapshot de precio para detectar modificaciones
        private readonly Dictionary<string, double> _lastLimit = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _lastStop = new Dictionary<string, double>();

        private Account _master;
        private System.Threading.Timer _connectPoll;

        public OrderMirror(IdemConfig cfg, Func<string, Account> resolve,
            Func<Account, double> dayPnl, Action<Instrument> onInstrument)
        {
            _cfg = cfg; _resolve = resolve; _dayPnl = dayPnl; _onInstrument = onInstrument;
        }

        public void Watch(Account master)
        {
            _master = master;
            if (master == null) return;
            if (IsConnected(master)) Subscribe();
            else if (_connectPoll == null)
                _connectPoll = new System.Threading.Timer(_ => PollConnect(), null, 1000, 1000);
        }

        public void Stop()
        {
            try { _connectPoll?.Dispose(); } catch { }
            _connectPoll = null;
            if (_master != null) { try { _master.OrderUpdate -= OnOrderUpdate; } catch { } }
            lock (_lock) { _map.Clear(); _lastLimit.Clear(); _lastStop.Clear(); }
        }

        private static bool IsConnected(Account a)
        {
            return a != null && a.Connection != null && a.Connection.Status == ConnectionStatus.Connected;
        }

        private void PollConnect()
        {
            if (!IsConnected(_master)) return;
            try { _connectPoll?.Dispose(); } catch { }
            _connectPoll = null;
            Subscribe();
        }

        private void Subscribe()
        {
            if (_master == null) return;
            _master.OrderUpdate -= OnOrderUpdate; // idempotente
            _master.OrderUpdate += OnOrderUpdate;
        }

        private static MirrorSide Side(NtOrderAction a)
        {
            return (a == NtOrderAction.Buy || a == NtOrderAction.BuyToCover) ? MirrorSide.Buy : MirrorSide.Sell;
        }

        private static bool IsLive(OrderState s)
        {
            return s == OrderState.Submitted || s == OrderState.Accepted
                || s == OrderState.Working || s == OrderState.TriggerPending;
        }

        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                var o = e.Order;
                if (o == null || o.Instrument == null) return;
                if (o.Account == null || _master == null || o.Account.Name != _master.Name) return;

                _onInstrument?.Invoke(o.Instrument);
                if (IdemRuntime.Instance != null) IdemRuntime.Instance.LastInstrument = o.Instrument;

                string id = o.OrderId;
                var state = o.OrderState;

                lock (_lock)
                {
                    bool mapped = _map.ContainsKey(id);

                    if (state == OrderState.Filled)
                    {
                        // La espejo llena sola (mismo precio) / el OCO cancela la otra pata.
                        Forget(id);
                    }
                    else if (state == OrderState.Cancelled || state == OrderState.Rejected)
                    {
                        if (mapped) CancelMirrors(id);
                        Forget(id);
                    }
                    else if (IsLive(state))
                    {
                        if (!mapped)
                            MirrorNew(o, id);
                        else if (PriceChanged(o, id))
                        {
                            CancelMirrors(id);
                            Forget(id);
                            MirrorNew(o, id);
                        }
                    }
                }
            }
            catch { /* nunca tirar desde el handler de NT8 */ }
        }

        private bool PriceChanged(Order o, string id)
        {
            _lastLimit.TryGetValue(id, out double ll);
            _lastStop.TryGetValue(id, out double ls);
            return o.LimitPrice != ll || o.StopPrice != ls;
        }

        private void MirrorNew(Order o, string id)
        {
            int qty = o.Quantity - o.Filled;
            if (qty <= 0) return;

            var side = Side(o.OrderAction);
            var slaves = new Dictionary<string, Order>();

            foreach (var sc in _cfg.Slaves)
            {
                var slave = _resolve(sc.Account);
                if (slave == null) continue;

                int slaveNet = RealNet(slave, o.Instrument);
                double pnl = _dayPnl != null ? _dayPnl(slave) : 0;
                var d = OrderMirrorDecision.Decide(side, qty, slaveNet, pnl, sc.DailyLossLimit, 1);
                if (!d.Place || d.Qty <= 0) continue;

                string oco = OrderMirrorDecision.SlaveOco(o.Oco, slave.Name);
                var mirror = OrderSubmit.Submit(slave, o.Instrument, o.OrderType, o.OrderAction,
                    d.Qty, o.LimitPrice, o.StopPrice, oco);
                if (mirror != null) slaves[slave.Name] = mirror;
            }

            if (slaves.Count > 0)
            {
                _map[id] = slaves;
                _lastLimit[id] = o.LimitPrice;
                _lastStop[id] = o.StopPrice;
                IdemRuntime.Instance?.AddFeed("orden " + o.OrderType + " " + o.OrderAction + " x" + qty
                    + " → " + slaves.Count + " slaves");
            }
        }

        private void CancelMirrors(string id)
        {
            if (!_map.TryGetValue(id, out var slaves)) return;
            foreach (var kv in slaves)
            {
                var slave = _resolve(kv.Key);
                var order = kv.Value;
                if (slave == null || order == null) continue;
                if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled) continue;
                try { slave.Cancel(new[] { order }); } catch { }
            }
        }

        private void Forget(string id)
        {
            _map.Remove(id);
            _lastLimit.Remove(id);
            _lastStop.Remove(id);
        }

        private static int RealNet(Account acc, Instrument instrument)
        {
            try
            {
                lock (acc.Positions)
                {
                    foreach (Position pos in acc.Positions)
                    {
                        if (pos.Instrument == instrument)
                            return pos.MarketPosition == MarketPosition.Long ? pos.Quantity
                                 : pos.MarketPosition == MarketPosition.Short ? -pos.Quantity : 0;
                    }
                }
            }
            catch { }
            return 0;
        }
    }
}
