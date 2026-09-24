using System.Collections.Generic;

namespace Idem.Core
{
    // Guard de orden en vuelo por (cuenta|instrumento). Tras mandar una orden al slave
    // apuntando a `expectedNet`, ese par queda "en vuelo" hasta que su net real llegue
    // al target (el fill confirmó) o venza el timeout (orden colgada/rechazada). Mientras
    // esté en vuelo NO se manda otra → evita el apilamiento de órdenes duplicadas cuando
    // el reconcile (fill del master o sweep) corre antes de que el fill anterior confirme.
    public sealed class PendingIntents
    {
        private struct Intent { public int Expected; public long ExpiresAt; }
        private readonly Dictionary<string, Intent> _m = new Dictionary<string, Intent>();
        private readonly object _lock = new object();

        public void Register(string key, int expectedNet, long nowMs, long timeoutMs)
        {
            lock (_lock) _m[key] = new Intent { Expected = expectedNet, ExpiresAt = nowMs + timeoutMs };
        }

        public bool ShouldSkip(string key, int currentNet, long nowMs)
        {
            lock (_lock)
            {
                if (!_m.TryGetValue(key, out var it)) return false;
                if (nowMs >= it.ExpiresAt) { _m.Remove(key); return false; }   // venció → retomar
                if (currentNet == it.Expected) { _m.Remove(key); return false; } // llegó el fill
                return true; // en vuelo → no mandar otra
            }
        }
    }
}
