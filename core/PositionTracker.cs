using System.Collections.Generic;

namespace Idem.Core
{
    // Net real por key ("cuenta|instrumento"), alimentado por TODO fill de forma
    // incondicional (separado del gating de réplica). Fuente de verdad de posiciones.
    // Puro: la cáscara NT8 llama ApplyFill/Seed desde el evento de ejecución.
    public sealed class PositionTracker
    {
        private readonly Dictionary<string, int> _net = new Dictionary<string, int>();
        private readonly object _lock = new object();

        public void Seed(string key, int net)
        {
            lock (_lock) _net[key] = net;
        }

        public void ApplyFill(string key, int signedQty)
        {
            lock (_lock)
            {
                _net.TryGetValue(key, out int cur);
                _net[key] = cur + signedQty;
            }
        }

        public int Net(string key)
        {
            lock (_lock)
            {
                _net.TryGetValue(key, out int cur);
                return cur;
            }
        }
    }
}
