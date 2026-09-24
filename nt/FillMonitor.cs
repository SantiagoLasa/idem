using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using Idem.Core;
using NtOrderAction = NinjaTrader.Cbi.OrderAction;

namespace Idem.Nt
{
    // Suscribe a las ejecuciones y alimenta el PositionTracker con TODO fill.
    // Clave (mata la carrera de arranque de PropCommand): NO suscribe hasta que la
    // cuenta esté Connected. Como el evento de conexión de NT8 no está verificado,
    // se hace por POLL de Connection.Status (API confirmada) cada 1s hasta conectar.
    public sealed class FillMonitor
    {
        private readonly PositionTracker _tracker;
        private readonly Action<string, int, Instrument> _onMasterFill;
        private readonly HashSet<string> _masters = new HashSet<string>();
        private readonly Dictionary<string, Account> _subscribed = new Dictionary<string, Account>();
        private readonly List<Account> _pending = new List<Account>();
        private readonly object _lock = new object();
        private System.Threading.Timer _connectPoll;

        public FillMonitor(PositionTracker tracker, Action<string, int, Instrument> onMasterFill)
        {
            _tracker = tracker;
            _onMasterFill = onMasterFill;
        }

        public void Watch(Account account, bool isMaster)
        {
            if (account == null) return;
            if (isMaster) _masters.Add(account.Name);

            if (IsConnected(account))
                Subscribe(account);
            else
            {
                lock (_lock) { if (!_pending.Contains(account)) _pending.Add(account); }
                EnsurePoll();
            }
        }

        public void StopAll()
        {
            try { _connectPoll?.Dispose(); } catch { }
            _connectPoll = null;
            List<Account> subs;
            lock (_lock) { subs = new List<Account>(_subscribed.Values); _subscribed.Clear(); _pending.Clear(); }
            foreach (var a in subs) { try { a.ExecutionUpdate -= OnExecutionUpdate; } catch { } }
        }

        private static bool IsConnected(Account a)
        {
            return a.Connection != null && a.Connection.Status == ConnectionStatus.Connected;
        }

        private void EnsurePoll()
        {
            if (_connectPoll == null)
                _connectPoll = new System.Threading.Timer(_ => PollConnections(), null, 1000, 1000);
        }

        private void PollConnections()
        {
            var ready = new List<Account>();
            lock (_lock)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                    if (IsConnected(_pending[i])) { ready.Add(_pending[i]); _pending.RemoveAt(i); }
            }
            foreach (var a in ready) Subscribe(a);
        }

        private void Subscribe(Account account)
        {
            lock (_lock)
            {
                if (_subscribed.ContainsKey(account.Name)) return;
                _subscribed[account.Name] = account;
            }
            account.ExecutionUpdate -= OnExecutionUpdate; // idempotente
            account.ExecutionUpdate += OnExecutionUpdate;
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                var exec = e.Execution;
                if (exec == null || exec.Order == null || exec.Instrument == null) return;

                string accName = exec.Order.Account != null ? exec.Order.Account.Name : "";
                string key = accName + "|" + exec.Instrument.FullName;

                int signed = SignedQty(exec.Order.OrderAction, exec.Quantity);
                _tracker.ApplyFill(key, signed);

                if (_masters.Contains(accName))
                    _onMasterFill(accName, _tracker.Net(key), exec.Instrument);
            }
            catch { /* nunca tirar desde el handler de NT8 */ }
        }

        private static int SignedQty(NtOrderAction action, int qty)
        {
            switch (action)
            {
                case NtOrderAction.Buy:
                case NtOrderAction.BuyToCover: return qty;
                case NtOrderAction.Sell:
                case NtOrderAction.SellShort: return -qty;
                default: return 0;
            }
        }
    }
}
