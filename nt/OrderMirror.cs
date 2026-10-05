using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    //
    // CLAVE (bug 2026-10-05, Rithmic): se identifica cada orden del master por el OBJETO Order
    // (NT8 reusa la misma instancia entre eventos), NO por OrderId — en Rithmic el OrderId es
    // temporal al Submitted y real al Accepted (cambia), y espejar por OrderId duplicaba la
    // orden (slaves abrían 2 contratos por cada 1). La referencia del objeto es estable siempre.
    public sealed class OrderMirror
    {
        // Comparador por identidad de objeto (no por OrderId, que cambia).
        private sealed class OrderRef : IEqualityComparer<Order>
        {
            public static readonly OrderRef Instance = new OrderRef();
            public bool Equals(Order a, Order b) { return ReferenceEquals(a, b); }
            public int GetHashCode(Order o) { return RuntimeHelpers.GetHashCode(o); }
        }

        private readonly IdemConfig _cfg;
        private readonly Func<string, Account> _resolve;
        private readonly Func<Account, double> _dayPnl;
        private readonly Action<Instrument> _onInstrument;
        private readonly object _lock = new object();

        // orden del master → (slave → orden espejo)
        private readonly Dictionary<Order, Dictionary<string, Order>> _map =
            new Dictionary<Order, Dictionary<string, Order>>(OrderRef.Instance);
        // orden del master → su OCO (del master) para agrupar las patas de un bracket
        private readonly Dictionary<Order, string> _oco = new Dictionary<Order, string>(OrderRef.Instance);
        // órdenes del master ya espejadas ALGUNA VEZ: una orden se espeja EXACTAMENTE una vez,
        // aunque lleguen eventos fuera de orden o re-entre el handler.
        private readonly HashSet<Order> _seen = new HashSet<Order>(OrderRef.Instance);
        // snapshot de precio para detectar modificaciones
        private readonly Dictionary<Order, double> _lastLimit = new Dictionary<Order, double>(OrderRef.Instance);
        private readonly Dictionary<Order, double> _lastStop = new Dictionary<Order, double>(OrderRef.Instance);

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
            lock (_lock) { _map.Clear(); _oco.Clear(); _lastLimit.Clear(); _lastStop.Clear(); _seen.Clear(); }
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

                var state = o.OrderState;

                lock (_lock)
                {
                    bool mapped = _map.ContainsKey(o);

                    if (state == OrderState.Filled)
                    {
                        // La espejo de ESTA pata se llena sola (mismo precio) → no la cancelo.
                        // Pero si era parte de un bracket, cancelo las OTRAS patas del grupo.
                        string group = OcoOf(o);
                        Forget(o);
                        if (!string.IsNullOrEmpty(group)) CancelGroupSiblings(group);
                    }
                    else if (state == OrderState.Cancelled || state == OrderState.Rejected)
                    {
                        if (mapped) CancelSlavesOf(o);
                        Forget(o);
                    }
                    else if (IsLive(state))
                    {
                        if (mapped)
                        {
                            if (PriceChanged(o))
                            {
                                // Modificar IN-PLACE (Change), no cancel+replace: evita el hueco
                                // donde el stop viejo se llena (fills no deseados en trailing).
                                ChangeMirrors(o);
                                _lastLimit[o] = o.LimitPrice;
                                _lastStop[o] = o.StopPrice;
                            }
                        }
                        else if (!_seen.Contains(o))
                        {
                            // Espejar EXACTAMENTE una vez por orden del master. Marcar _seen ANTES
                            // de colocar, así un evento tardío/re-entrante no duplica la entrada.
                            _seen.Add(o);
                            MirrorNew(o);
                        }
                        // mapped==false && seen: la orden ya se espejó y llenó/canceló → evento
                        // tardío, se ignora (no re-espejar).
                    }
                }
            }
            catch { /* nunca tirar desde el handler de NT8 */ }
        }

        private string OcoOf(Order o)
        {
            _oco.TryGetValue(o, out string v);
            return v;
        }

        private bool PriceChanged(Order o)
        {
            _lastLimit.TryGetValue(o, out double ll);
            _lastStop.TryGetValue(o, out double ls);
            return o.LimitPrice != ll || o.StopPrice != ls;
        }

        private void MirrorNew(Order o)
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
                _map[o] = slaves;
                _oco[o] = o.Oco;
                _lastLimit[o] = o.LimitPrice;
                _lastStop[o] = o.StopPrice;
                IdemRuntime.Instance?.AddFeed("orden " + o.OrderType + " " + o.OrderAction + " x" + qty
                    + " → " + slaves.Count + " slaves");
            }
        }

        // Cancela las patas hermanas de un bracket (mismo OCO del master), menos las ya
        // olvidadas. Replica el OCO del master del lado del slave sin usar un OCO id de NT8.
        private void CancelGroupSiblings(string group)
        {
            var orders = new List<Order>();
            foreach (var kv in _oco)
                if (kv.Value == group) orders.Add(kv.Key);

            foreach (var mo in orders)
            {
                CancelSlavesOf(mo);
                Forget(mo);
            }
        }

        private void ChangeMirrors(Order o)
        {
            if (!_map.TryGetValue(o, out var slaves)) return;
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

        private void CancelSlavesOf(Order o)
        {
            if (!_map.TryGetValue(o, out var slaves)) return;
            foreach (var kv in slaves)
            {
                var slave = _resolve(kv.Key);
                var order = kv.Value;
                if (slave == null || order == null) continue;
                if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled) continue;
                try { slave.Cancel(new[] { order }); } catch { }
            }
        }

        private void Forget(Order o)
        {
            _map.Remove(o);
            _oco.Remove(o);
            _lastLimit.Remove(o);
            _lastStop.Remove(o);
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
