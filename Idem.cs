using System;
using System.IO;
using System.Linq;
using System.Windows;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using Idem.Core;
using Idem.Nt;

namespace NinjaTrader.NinjaScript.AddOns
{
    // AddOn entry de Idem. Arranca el motor de copy en la primera ventana creada
    // (patrón probado de PropCommand), leyendo idem-config.txt. El panel WPF viene
    // en Fase 4; por ahora arranca el copy y loguea al Output.
    public class Idem : AddOnBase
    {
        private static readonly object _initLock = new object();
        private static bool _booted;

        private PositionTracker _tracker;
        private FillMonitor _fills;
        private CopyEngine _engine;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Idem";
            }
            else if (State == State.Terminated)
            {
                try { _engine?.Stop(); } catch { }
                try { _fills?.StopAll(); } catch { }
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            lock (_initLock)
            {
                if (_booted) return;
                _booted = true;
            }
            try { Boot(); }
            catch (Exception ex) { Log("boot error: " + ex.Message); }
        }

        private void Boot()
        {
            string path = Path.Combine(NinjaTrader.Core.Globals.UserDataDir,
                "bin", "Custom", "Idem", "idem-config.txt");
            if (!File.Exists(path)) { Log("no idem-config.txt en " + path); return; }

            var cfg = IdemConfig.Parse(File.ReadAllText(path));

            Func<string, Account> resolve = name =>
            {
                lock (Account.All) return Account.All.FirstOrDefault(a => a.Name == name);
            };

            // Fase 2: DD real todavía no cableado → 0 (el guard no bloquea).
            // Se conecta el DD real en Fase 3/4; el guard ya está probado en aislamiento.
            Func<Account, double> drawdown = acc => 0.0;

            _tracker = new PositionTracker();
            _engine = new CopyEngine(_tracker, cfg, resolve, drawdown);
            _fills = new FillMonitor(_tracker, (m, net, inst) => _engine.OnMasterFill(m, net, inst));

            var master = resolve(cfg.MasterAccount);
            if (master != null) _fills.Watch(master, true);
            else Log("master no encontrado: " + cfg.MasterAccount);

            foreach (var sc in cfg.Slaves)
            {
                var acc = resolve(sc.Account);
                if (acc != null) _fills.Watch(acc, false);
                else Log("slave no encontrado: " + sc.Account);
            }

            _engine.Start();
            Log("motor arrancado (master " + cfg.MasterAccount + ", " + cfg.Slaves.Count + " slaves, enabled=" + cfg.Enabled + ")");
        }

        private static void Log(string msg)
        {
            try { NinjaTrader.Code.Output.Process("Idem: " + msg, PrintTo.OutputTab1); } catch { }
        }
    }
}
