using System.Collections.Generic;

namespace Idem.Core
{
    public struct SlaveState
    {
        public string Id;
        public int Net;
        public double Drawdown;
        public double DdLimit;
    }

    public struct SlaveDecision
    {
        public string Id;
        public OrderAction Action;
        public int Qty;
        public bool Blocked;
    }

    // El cerebro de la orquestación: dado el net del master y el estado de cada slave,
    // decide qué orden mandar a cada uno. Compone Reconciler (qué orden) + RiskGuard
    // (si una entrada se permite). Puro y determinista → 100% testeable sin NT8.
    public static class CopyDecision
    {
        public static List<SlaveDecision> ForMasterNet(
            int masterNet, IReadOnlyList<SlaveState> slaves, double cushion, int ratio = 1)
        {
            var result = new List<SlaveDecision>(slaves.Count);
            int target = Sizing.SlaveTarget(masterNet, ratio);

            foreach (var s in slaves)
            {
                var order = Reconciler.Compute(masterNet, s.Net, ratio);

                if (order.Action == OrderAction.None)
                {
                    result.Add(new SlaveDecision { Id = s.Id, Action = OrderAction.None, Qty = 0, Blocked = false });
                    continue;
                }

                bool block = RiskGuard.ShouldBlock(s.Net, target, s.Drawdown, s.DdLimit, cushion);
                if (block)
                    result.Add(new SlaveDecision { Id = s.Id, Action = OrderAction.None, Qty = 0, Blocked = true });
                else
                    result.Add(new SlaveDecision { Id = s.Id, Action = order.Action, Qty = order.Qty, Blocked = false });
            }
            return result;
        }
    }
}
