using System;
using System.Collections.Generic;
using System.Threading;
using NinjaTrader.Cbi;
using Idem.Core;

namespace Idem.Nt
{
    // Orquestador de la cáscara: master fillea → decide por slave → manda. Más un
    // safety sweep que relee el net real y reconcilia (respaldo por si un fill se perdió).
    public sealed class CopyEngine
    {
        private readonly PositionTracker _tracker;
        private readonly IdemConfig _cfg;
        private readonly Func<string, Account> _resolve;
        private readonly Func<Account, double> _dayPnl;   // P&L del día por cuenta (realized+unrealized)
        private readonly Action<Instrument> _onSweepTick;
        private readonly Dictionary<string, int> _divergeCount = new Dictionary<string, int>();
        private const int DebounceTicks = 2; // divergencia persistente ≥2s antes de que el sweep actúe
        private Timer _sweep;
        private Instrument _lastInstrument;

        public CopyEngine(PositionTracker tracker, IdemConfig cfg,
            Func<string, Account> resolve, Func<Account, double> dayPnl,
            Action<Instrument> onSweepTick = null)
        {
            _tracker = tracker; _cfg = cfg; _resolve = resolve; _dayPnl = dayPnl; _onSweepTick = onSweepTick;
        }

        public void OnMasterFill(string masterName, int masterNet, Instrument instrument)
        {
            // La réplica la hace el OrderMirror (copy a nivel de orden). Acá sólo registramos
            // el instrumento para que el SweepTick (red de seguridad) sepa qué reconciliar.
            _lastInstrument = instrument;
        }

        // El OrderMirror avisa el instrumento al apoyar una orden, así el SweepTick (red de
        // seguridad) puede reconciliar el neto aunque todavía no haya habido ningún fill.
        public void NoteInstrument(Instrument instrument)
        {
            if (instrument != null) _lastInstrument = instrument;
        }

        public void Start()
        {
            _sweep = new Timer(_ => SweepTick(), null, 1000, 1000);
        }

        public void Stop()
        {
            try { _sweep?.Dispose(); } catch { }
            _sweep = null;
        }

        private void SweepTick()
        {
            try
            {
                if (!_cfg.Enabled || _lastInstrument == null) return;
                var inst = _lastInstrument;
                var master = _resolve(_cfg.MasterAccount);
                if (master == null) return;

                // Red de seguridad REDUCE-ONLY: lee el net real del broker y SÓLO cierra la
                // exposición de más del slave (lo que el master ya no tiene), nunca abre. Las
                // entradas las hace exclusivamente el order-mirror. Esto mata la doble-entrada
                // que hacía sobre-operar a los slaves ~3,5x (incidente 2026-10-02). Sigue
                // destrabando una posición colgada (master salió, slave quedó adentro).
                int masterReal = RealNet(master, inst);
                _tracker.Seed(master.Name + "|" + inst.FullName, masterReal);

                foreach (var sc in _cfg.Slaves)
                {
                    var acc = _resolve(sc.Account);
                    if (acc == null) continue;

                    int slaveNet = RealNet(acc, inst);
                    _tracker.Seed(sc.Account + "|" + inst.FullName, slaveNet);

                    int target = SweepReduce.Target(masterReal, slaveNet);
                    int delta = target - slaveNet;

                    // El sweep NO actúa si el mirror está trabajando una orden para este slave
                    // (deja que el mirror resuelva; si no, doble-salida → posición contraria).
                    // Y sólo tras DebounceTicks de divergencia persistente, para cubrir órdenes
                    // en vuelo. Así sólo cierra una posición GENUINAMENTE colgada.
                    if (delta == 0 || HasWorkingMirror(acc, inst))
                    {
                        _divergeCount[sc.Account] = 0;
                        continue;
                    }

                    _divergeCount.TryGetValue(sc.Account, out int n);
                    n++;
                    _divergeCount[sc.Account] = n;
                    if (n < DebounceTicks) continue;
                    _divergeCount[sc.Account] = 0;

                    var action = delta > 0 ? Idem.Core.OrderAction.Buy : Idem.Core.OrderAction.Sell;
                    OrderSubmit.Market(acc, inst, action, Math.Abs(delta));
                    IdemRuntime.Instance?.AddFeed("sweep cierra colgada " + sc.Account + " " + action + " " + Math.Abs(delta));
                }

                try { _onSweepTick?.Invoke(inst); } catch { }
            }
            catch { }
        }

        // ¿El slave tiene alguna orden IdemMirror viva para este instrumento? Si sí, el mirror
        // está resolviendo la posición → el sweep no se mete (evita doble-acción).
        private static bool HasWorkingMirror(Account acc, Instrument instrument)
        {
            try
            {
                lock (acc.Orders)
                {
                    foreach (Order o in acc.Orders)
                        if (o.Name == "IdemMirror" && o.Instrument == instrument
                            && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
                                || o.OrderState == OrderState.Submitted || o.OrderState == OrderState.TriggerPending
                                || o.OrderState == OrderState.ChangeSubmitted))
                            return true;
                }
            }
            catch { }
            return false;
        }

        // Net real de una cuenta para un instrumento, leído del broker (no del tracker).
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
