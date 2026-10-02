using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using Idem.Core;
using NtOrderAction = NinjaTrader.Cbi.OrderAction;

namespace Idem.Nt
{
    // Copy a nivel de ORDEN: espeja la orden del master cuando la manda (OrderUpdate), no
    // cuando se llena. Market → todas al mismo instante; límite/TP → al mismo precio.
    // Brackets (TP+SL): NT8 NO deja reusar un OCO id entre submits separados (las 2 patas
    // comparten el OCO del master), así que NO usamos OCO de NT8 en los slaves — en cambio,
    // cuando una pata del grupo se llena, cancelamos las otras patas del grupo en los slaves
    // (comportamiento OCO manejado por nosotros). El guard saltea entradas de slaves pasados
    // de tope. El SweepTick del CopyEngine queda de red de seguridad del neto.
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
        // orderId del master → su OCO (del master) para agrupar las patas de un bracket
        private readonly Dictionary<string, string> _oco = new Dictionary<string, string>();
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
            lock (_lock) { _map.Clear(); _oco.Clear(); _lastLimit.Clear(); _lastStop.Clear(); }
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
                        // La espejo de ESTA pata se llena sola (mismo precio) → no la cancelo.
                        // Pero si era parte de un bracket, cancelo las OTRAS patas del grupo.
                        string group = OcoOf(id);
                        Forget(id);
                        if (!string.IsNullOrEmpty(group)) CancelGroupSiblings(group);
                    }
                    else if (state == OrderState.Cancelled || state == OrderState.Rejected)
                    {
                        if (mapped) CancelSlavesOf(id);
                        Forget(id);
                    }
                    else if (IsLive(state))
                    {
                        if (!mapped)
                            MirrorNew(o, id);
                        else if (PriceChanged(o, id))
                        {
                            // Modificar IN-PLACE (Change), no cancel+replace: evita el hueco donde
                            // el stop viejo se llena (fills no deseados en trailing — 2026-10-02).
                            ChangeMirrors(o, id);
                            _lastLimit[id] = o.LimitPrice;
                            _lastStop[id] = o.StopPrice;
                        }
                    }
                }
            }
            catch { /* nunca tirar desde el handler de NT8 */ }
        }

        private string OcoOf(string id)
        {
            _oco.TryGetValue(id, out string v);
            return v;
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

                // Acción normalizada a Buy/Sell (no BuyToCover/SellShort del master): NT8 con
                // OrderEntry.Manual abre o cierra según la posición REAL del slave. Copiar
                // "Buy to cover" tal cual abriría un long desde flat (el bug del 2026-10-01).
                NtOrderAction ntAction = side == MirrorSide.Buy ? NtOrderAction.Buy : NtOrderAction.Sell;

                // Sin OCO de NT8 en el slave (ver nota de clase): el bracket se maneja cancelando
                // las patas hermanas cuando una del grupo se llena.
                var mirror = OrderSubmit.Submit(slave, o.Instrument, o.OrderType, ntAction,
                    d.Qty, o.LimitPrice, o.StopPrice, string.Empty);
                if (mirror != null) slaves[slave.Name] = mirror;
            }

            if (slaves.Count > 0)
            {
                _map[id] = slaves;
                _oco[id] = o.Oco;
                _lastLimit[id] = o.LimitPrice;
                _lastStop[id] = o.StopPrice;
                IdemRuntime.Instance?.AddFeed("orden " + o.OrderType + " " + o.OrderAction + " x" + qty
                    + " → " + slaves.Count + " slaves");
            }
        }

        // Cancela las patas hermanas de un bracket (mismo OCO del master), menos las ya
        // olvidadas. Replica el OCO del master del lado del slave sin usar un OCO id de NT8.
        private void CancelGroupSiblings(string group)
        {
            var ids = new List<string>();
            foreach (var kv in _oco)
                if (kv.Value == group) ids.Add(kv.Key);

            foreach (var sid in ids)
            {
                CancelSlavesOf(sid);
                Forget(sid);
            }
        }

        private void ChangeMirrors(Order o, string id)
        {
            if (!_map.TryGetValue(id, out var slaves)) return;
            int qty = o.Quantity - o.Filled;
            foreach (var kv in slaves)
            {
                var slave = _resolve(kv.Key);
                var order = kv.Value;
                if (slave == null || order == null) continue;
                if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled) continue;
                OrderSubmit.Change(slave, order, o.LimitPrice, o.StopPrice, qty > 0 ? qty : order.Quantity);
            }
        }

        private void CancelSlavesOf(string id)
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
            _oco.Remove(id);
            _lastLimit.Remove(id);
            _lastStop.Remove(id);
        }

        // Cancela las órdenes IdemMirror que quedaron apoyadas de una sesión anterior. Idem no
        // puede retomarlas (no están en su mapa nuevo); si no se cancelan, pueden llenarse solas
        // al reiniciar y abrir posiciones (incidente 2026-10-01). Se corre una vez al Boot.
        public static void CancelTagged(Account acc)
        {
            if (acc == null) return;
            try
            {
                var toCancel = new List<Order>();
                lock (acc.Orders)
                {
                    foreach (Order o in acc.Orders)
                        if (o.Name == "IdemMirror"
                            && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
                                || o.OrderState == OrderState.TriggerPending))
                            toCancel.Add(o);
                }
                foreach (var o in toCancel) { try { acc.Cancel(new[] { o }); } catch { } }
            }
            catch { }
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
