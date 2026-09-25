using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using Idem.Core;
using Idem.Nt;
using Idem.Ui;

namespace NinjaTrader.NinjaScript.AddOns
{
    // AddOn entry de Idem. Arranca el motor de copy en la primera ventana creada,
    // agrega el menú "Idem" al Control Center, y expone Reconfigure para editar la
    // config en vivo desde el dashboard (re-watchea cuentas + reescribe el .txt).
    public class Idem : AddOnBase
    {
        private static readonly object _initLock = new object();
        private static bool _booted;
        private NTMenuItem _menu;

        private IdemConfig _cfg;
        private Func<string, Account> _resolve;
        private string _configPath;

        private PositionTracker _tracker;
        private DayPnlCache _dayCache;
        private DayPnlPoll _dayPoll;
        private FillMonitor _fills;
        private CopyEngine _engine;

        private CalendarStore _calendar;
        private CalendarRecorder _calRecorder;
        private string _calendarPath;

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
                try { _dayPoll?.Stop(); } catch { }
                try { _calRecorder?.Stop(); } catch { }
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            bool doBoot = false;
            lock (_initLock) { if (!_booted) { _booted = true; doBoot = true; } }
            if (doBoot)
            {
                try { Boot(); }
                catch (Exception ex) { Log("boot error: " + ex.Message); }
            }

            try { AddMenu(window); }
            catch (Exception ex) { Log("menu error: " + ex.Message); }
        }

        private void AddMenu(Window window)
        {
            var cc = window as ControlCenter;
            if (cc == null) return;
            var newMenu = cc.FindFirst("ControlCenterMenuItemNew") as ItemsControl;
            if (newMenu == null) return;

            foreach (var it in newMenu.Items.OfType<NTMenuItem>().Where(m => m.Header?.ToString() == "Idem").ToList())
                newMenu.Items.Remove(it);

            var root = new NTMenuItem { Header = "Idem", Style = Application.Current.TryFindResource("MainMenuItem") as Style };
            var open = new NTMenuItem { Header = "Dashboard", Style = Application.Current.TryFindResource("SubMenuItem") as Style };
            open.Click += (s, e) => IdemWindow.ShowOrActivate();
            root.Items.Add(open);
            newMenu.Items.Add(root);
            _menu = root;
        }

        private void Boot()
        {
            _configPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir,
                "bin", "Custom", "Idem", "idem-config.txt");
            if (!File.Exists(_configPath)) { Log("no idem-config.txt en " + _configPath); return; }

            _cfg = IdemConfig.Parse(File.ReadAllText(_configPath));

            try { lock (Account.All) Log("cuentas disponibles: " + string.Join(", ", Account.All.Select(a => a.Name))); } catch { }

            _resolve = name =>
            {
                lock (Account.All) return Account.All.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            };

            _dayCache = new DayPnlCache();
            Func<Account, double> dayPnl = acc => _dayCache.Get(acc.Name);

            _tracker = new PositionTracker();

            _calendarPath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir,
                "bin", "Custom", "Idem", "idem-calendar.txt");
            _calendar = new CalendarStore();
            try
            {
                if (File.Exists(_calendarPath))
                    foreach (var snap in CalendarSerializer.Parse(File.ReadAllText(_calendarPath)))
                        _calendar.Record(snap.Account, snap.Date, snap.NetLiq);
            }
            catch (Exception ex) { Log("calendario load error: " + ex.Message); }

            IdemRuntime.Instance = new IdemRuntime
            {
                Tracker = _tracker,
                DayCache = _dayCache,
                Calendar = _calendar,
                Config = _cfg,
                Resolve = _resolve,
                Reconfigure = Reconfigure
            };

            var stopExec = new StopExecutor(_tracker, _cfg, _resolve);
            _engine = new CopyEngine(_tracker, _cfg, _resolve, dayPnl,
                inst => stopExec.ReconcileStops(inst));

            WireWatches();
            _engine.Start();
            Log("motor arrancado (master " + _cfg.MasterAccount + ", " + _cfg.Slaves.Count + " slaves, enabled=" + _cfg.Enabled + ")");
        }

        // Arma (o rearma) la suscripción a fills y el poll de P&L para el master/slaves
        // actuales de _cfg. Reusado por Boot y Reconfigure.
        private void WireWatches()
        {
            try { _fills?.StopAll(); } catch { }
            try { _dayPoll?.Stop(); } catch { }
            try { _calRecorder?.Stop(); } catch { }

            _fills = new FillMonitor(_tracker, (m, net, inst) =>
            {
                if (IdemRuntime.Instance != null) IdemRuntime.Instance.LastInstrument = inst;
                _engine.OnMasterFill(m, net, inst);
            });

            var master = _resolve(_cfg.MasterAccount);
            if (master != null) _fills.Watch(master, true);
            else Log("master no encontrado: " + _cfg.MasterAccount);

            var slaveAccounts = new List<Account>();
            foreach (var sc in _cfg.Slaves)
            {
                var acc = _resolve(sc.Account);
                if (acc != null) { _fills.Watch(acc, false); slaveAccounts.Add(acc); }
                else Log("slave no encontrado: " + sc.Account);
            }

            var allAccounts = new List<Account>();
            if (master != null) allAccounts.Add(master);
            allAccounts.AddRange(slaveAccounts);

            // Poll de P&L del día sobre master+slaves: el guard sólo mira slaves, pero el
            // master también hace falta para el total del día en el calendario (celda "hoy").
            _dayPoll = new DayPnlPoll(_dayCache, allAccounts);
            _dayPoll.Start();

            _calRecorder = new CalendarRecorder(_calendar, allAccounts, _calendarPath);
            _calRecorder.Start();
        }

        // Aplica una config nueva en vivo: muta la instancia que el motor ya referencia,
        // rearma watches/poll, y persiste al .txt. Sin reiniciar NT8.
        private void Reconfigure(IdemConfig newCfg)
        {
            if (newCfg == null || _cfg == null) return;
            _cfg.MasterAccount = newCfg.MasterAccount;
            _cfg.Enabled = newCfg.Enabled;
            _cfg.Slaves.Clear();
            _cfg.Slaves.AddRange(newCfg.Slaves);

            WireWatches();

            try { File.WriteAllText(_configPath, IdemConfigWriter.ToText(_cfg)); }
            catch (Exception ex) { Log("persist error: " + ex.Message); }
            Log("reconfigurado (master " + _cfg.MasterAccount + ", " + _cfg.Slaves.Count + " slaves)");
        }

        private static void Log(string msg)
        {
            try { NinjaTrader.Code.Output.Process("Idem: " + msg, PrintTo.OutputTab1); } catch { }
        }
    }
}
