using System.Collections.Generic;
using NinjaTrader.Cbi;
using Idem.Core;

namespace Idem.Nt
{
    // Estado vivo del motor, publicado para que la ventana lo lea. Singleton estático
    // (app personal). El motor lo puebla en Boot; el panel lo lee con un timer de UI.
    public sealed class IdemRuntime
    {
        public static IdemRuntime Instance;

        public PositionTracker Tracker;
        public DayPnlCache DayCache;
        public IdemConfig Config;
        public System.Func<string, Account> Resolve;
        public System.Action<IdemConfig> Reconfigure;
        public Instrument LastInstrument;

        private readonly LinkedList<string> _feed = new LinkedList<string>();
        private readonly object _feedLock = new object();

        public void AddFeed(string line)
        {
            lock (_feedLock)
            {
                _feed.AddFirst(System.DateTime.Now.ToString("HH:mm:ss") + "  " + line);
                while (_feed.Count > 50) _feed.RemoveLast();
            }
        }

        public List<string> RecentFeed()
        {
            lock (_feedLock) return new List<string>(_feed);
        }
    }
}
