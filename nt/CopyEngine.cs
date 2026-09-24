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
        private readonly PendingIntents _pending = new PendingIntents();
        private static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private const long PendingTimeoutMs = 3000;
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
            _lastInstrument = instrument;
            Reconcile(masterNet, instrument);
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

                // HEAL: re-seedear el tracker con el net REAL del broker. El event-path
                // puede driftear bajo carga (NT8 dropea/batchea ExecutionUpdate); el sweep
                // corrige a la verdad del broker cada 1s → destraba posiciones colgadas.
                int masterReal = RealNet(master, inst);
                _tracker.Seed(master.Name + "|" + inst.FullName, masterReal);
                foreach (var sc in _cfg.Slaves)
                {
                    var acc = _resolve(sc.Account);
                    if (acc != null) _tracker.Seed(sc.Account + "|" + inst.FullName, RealNet(acc, inst));
                }

                Reconcile(masterReal, inst);
                try { _onSweepTick?.Invoke(inst); } catch { }
            }
            catch { }
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

        private void Reconcile(int masterNet, Instrument instrument)
        {
            if (!_cfg.Enabled) return;

            var states = new List<SlaveState>(_cfg.Slaves.Count);
            var byId = new Dictionary<string, Account>();
            foreach (var sc in _cfg.Slaves)
            {
                var acc = _resolve(sc.Account);
                if (acc == null) continue;
                byId[sc.Account] = acc;
                states.Add(new SlaveState
                {
                    Id = sc.Account,
                    Net = _tracker.Net(sc.Account + "|" + instrument.FullName),
                    DayPnl = _dayPnl(acc),
                    DailyLossLimit = sc.DailyLossLimit
                });
            }

            foreach (var d in CopyDecision.ForMasterNet(masterNet, states))
            {
                if (d.Blocked)
                {
                    IdemRuntime.Instance?.AddFeed(d.Id + " BLOQUEADO (guard)");
                    continue;
                }
                if (d.Action == Idem.Core.OrderAction.None) continue;
                if (byId.TryGetValue(d.Id, out var acc))
                {
                    string key = d.Id + "|" + instrument.FullName;
                    long now = _clock.ElapsedMilliseconds;
                    int curNet = _tracker.Net(key);
                    // Guard de orden en vuelo: si ya hay una orden para este par sin
                    // confirmar, no mandar otra (evita apilar duplicados en scalping rápido).
                    if (_pending.ShouldSkip(key, curNet, now)) continue;

                    OrderSubmit.Market(acc, instrument, d.Action, d.Qty);
                    _pending.Register(key, masterNet, now, PendingTimeoutMs);
                    IdemRuntime.Instance?.AddFeed(d.Id + " " + d.Action + " " + d.Qty + " " + instrument.FullName);
                }
            }
        }
    }
}
