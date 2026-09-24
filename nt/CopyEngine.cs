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
        private readonly Func<Account, double> _drawdown;
        private Timer _sweep;
        private Instrument _lastInstrument;

        public CopyEngine(PositionTracker tracker, IdemConfig cfg,
            Func<string, Account> resolve, Func<Account, double> drawdown)
        {
            _tracker = tracker; _cfg = cfg; _resolve = resolve; _drawdown = drawdown;
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
                var master = _resolve(_cfg.MasterAccount);
                if (master == null) return;
                int masterNet = _tracker.Net(master.Name + "|" + _lastInstrument.FullName);
                Reconcile(masterNet, _lastInstrument);
            }
            catch { }
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
                    Drawdown = _drawdown(acc),
                    DdLimit = sc.DdLimit
                });
            }

            foreach (var d in CopyDecision.ForMasterNet(masterNet, states, _cfg.Cushion))
            {
                if (d.Blocked || d.Action == Idem.Core.OrderAction.None) continue;
                if (byId.TryGetValue(d.Id, out var acc))
                    OrderSubmit.Market(acc, instrument, d.Action, d.Qty);
            }
        }
    }
}
