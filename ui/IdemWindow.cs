using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using Idem.Core;
using Idem.Nt;

namespace Idem.Ui
{
    // Ventana del dashboard: flota en vivo + feed de réplicas + controles (Pausa,
    // Flatten All). Lee IdemRuntime con un timer de UI (~300ms). Config UI → Task 6.
    public class IdemWindow : Window
    {
        private static IdemWindow _instance;

        private readonly StackPanel _fleet = new StackPanel();
        private readonly StackPanel _feed = new StackPanel();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBox _cfgBox = new TextBox();
        private readonly TextBlock _cfgMsg = new TextBlock();
        private readonly Button _pauseBtn = new Button();
        private DispatcherTimer _timer;

        // Guard de funding: un delta de net-liq mayor a esto es depósito/retiro, no P&L.
        private const double FundingGuard = 1000000;
        private readonly UniformGrid _calGrid = new UniformGrid { Columns = 7 };
        private readonly TextBlock _calLabel = new TextBlock();
        private readonly TextBlock _calTotal = new TextBlock();
        private DateTime _calMonth;
        private static readonly CultureInfo Es = new CultureInfo("es-ES");

        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x94, 0xa3, 0xb8));

        public static void ShowOrActivate()
        {
            if (_instance == null)
            {
                _instance = new IdemWindow();
                _instance.Closed += (s, e) => _instance = null;
                _instance.Show();
            }
            else _instance.Activate();
        }

        public IdemWindow()
        {
            Title = "Idem";
            Width = 720; Height = 520;
            Background = new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0f));

            _status.Foreground = Brushes.White;
            _status.FontSize = 14;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(0, 0, 16, 0);

            _pauseBtn.Content = "Pausa";
            _pauseBtn.Padding = new Thickness(12, 3, 12, 3);
            _pauseBtn.Margin = new Thickness(0, 0, 8, 0);
            _pauseBtn.Click += (s, e) => TogglePause();

            var flattenBtn = new Button { Content = "FLATTEN ALL", Padding = new Thickness(12, 3, 12, 3) };
            flattenBtn.Foreground = Red;
            flattenBtn.Click += (s, e) => FlattenAll();

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            bar.Children.Add(_status);
            bar.Children.Add(_pauseBtn);
            bar.Children.Add(flattenBtn);

            var feedTitle = new TextBlock { Text = "Réplicas", Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 14, 0, 4) };

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(bar);
            root.Children.Add(_fleet);
            root.Children.Add(feedTitle);
            root.Children.Add(_feed);

            var t0 = TradingDay.EtDate(DateTime.UtcNow);
            _calMonth = new DateTime(t0.Year, t0.Month, 1);

            var calTitle = new TextBlock { Text = "Calendario (P&L diario, corte 5pm ET)", Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 18, 0, 4) };
            var prevBtn = new Button { Content = "◀", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
            var nextBtn = new Button { Content = "▶", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            prevBtn.Click += (s, e) => { _calMonth = _calMonth.AddMonths(-1); RefreshCalendar(); };
            nextBtn.Click += (s, e) => { _calMonth = _calMonth.AddMonths(1); RefreshCalendar(); };
            _calLabel.Foreground = Brushes.White; _calLabel.FontSize = 13; _calLabel.VerticalAlignment = VerticalAlignment.Center;
            _calLabel.MinWidth = 150; _calLabel.TextAlignment = TextAlignment.Center;
            _calTotal.Foreground = Dim; _calTotal.FontSize = 12; _calTotal.VerticalAlignment = VerticalAlignment.Center; _calTotal.Margin = new Thickness(16, 0, 0, 0);
            var calBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            calBar.Children.Add(prevBtn); calBar.Children.Add(_calLabel); calBar.Children.Add(nextBtn); calBar.Children.Add(_calTotal);

            root.Children.Add(calTitle);
            root.Children.Add(calBar);
            root.Children.Add(_calGrid);

            var cfgTitle = new TextBlock { Text = "Config (editar y Guardar — sin reiniciar)", Foreground = Dim, FontSize = 12, Margin = new Thickness(0, 18, 0, 4) };
            _cfgBox.AcceptsReturn = true;
            _cfgBox.MinLines = 5;
            _cfgBox.FontFamily = new FontFamily("Consolas");
            _cfgBox.Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x18));
            _cfgBox.Foreground = Brushes.White;
            _cfgBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e));
            _cfgBox.Padding = new Thickness(6);
            var rt0 = IdemRuntime.Instance;
            if (rt0 != null && rt0.Config != null) _cfgBox.Text = IdemConfigWriter.ToText(rt0.Config);
            var saveBtn = new Button { Content = "Guardar config", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            saveBtn.Click += (s, e) => SaveConfig();
            _cfgMsg.Foreground = Dim; _cfgMsg.FontSize = 11; _cfgMsg.Margin = new Thickness(0, 4, 0, 0);

            root.Children.Add(cfgTitle);
            root.Children.Add(_cfgBox);
            root.Children.Add(saveBtn);
            root.Children.Add(_cfgMsg);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Closed += (s, e) => { try { _timer.Stop(); } catch { } };
        }

        private void TogglePause()
        {
            var rt = IdemRuntime.Instance;
            if (rt == null || rt.Config == null) return;
            rt.Config.Enabled = !rt.Config.Enabled;
            rt.AddFeed(rt.Config.Enabled ? "REANUDADO" : "PAUSADO");
        }

        private void FlattenAll()
        {
            var rt = IdemRuntime.Instance;
            if (rt == null || rt.Config == null || rt.Resolve == null) return;

            var accs = new List<Account>();
            var m = rt.Resolve(rt.Config.MasterAccount); if (m != null) accs.Add(m);
            foreach (var sc in rt.Config.Slaves) { var a = rt.Resolve(sc.Account); if (a != null) accs.Add(a); }

            if (rt.LastInstrument != null)
            {
                foreach (var a in accs)
                {
                    try { a.Flatten(new[] { rt.LastInstrument }); } catch { }
                }
            }
            else
            {
                try { Account.FlattenEverything(); } catch { }
            }
            rt.AddFeed("FLATTEN ALL");
        }

        private void Refresh()
        {
            var rt = IdemRuntime.Instance;
            _fleet.Children.Clear();

            if (rt == null || rt.Config == null)
            {
                _status.Text = "motor no arrancado";
                return;
            }

            bool on = rt.Config.Enabled;
            _status.Text = "Replicación: " + (on ? "ON" : "OFF");
            _status.Foreground = on ? Green : Warn;
            _pauseBtn.Content = on ? "Pausa" : "Reanudar";

            var inst = rt.LastInstrument;
            string instName = inst != null ? inst.FullName : "";
            var inputs = new List<FleetInput>();

            var master = rt.Resolve != null ? rt.Resolve(rt.Config.MasterAccount) : null;
            inputs.Add(new FleetInput
            {
                Account = rt.Config.MasterAccount, IsMaster = true,
                Net = inst != null ? rt.Tracker.Net(rt.Config.MasterAccount + "|" + instName) : 0,
                DayPnl = master != null ? rt.DayCache.Get(master.Name) : 0, DailyLossLimit = 0
            });

            foreach (var sc in rt.Config.Slaves)
                inputs.Add(new FleetInput
                {
                    Account = sc.Account, IsMaster = false,
                    Net = inst != null ? rt.Tracker.Net(sc.Account + "|" + instName) : 0,
                    DayPnl = rt.DayCache.Get(sc.Account), DailyLossLimit = sc.DailyLossLimit
                });

            foreach (var row in FleetView.Build(inputs))
                _fleet.Children.Add(RowUi(row));

            _feed.Children.Clear();
            foreach (var line in rt.RecentFeed())
                _feed.Children.Add(new TextBlock { Text = line, Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 1, 0, 1) });

            RefreshCalendar();
        }

        private void RefreshCalendar()
        {
            var rt = IdemRuntime.Instance;
            _calGrid.Children.Clear();

            string[] wd = { "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom" };
            foreach (var w in wd)
                _calGrid.Children.Add(new TextBlock { Text = w, Foreground = Dim, FontSize = 11, TextAlignment = TextAlignment.Center, Margin = new Thickness(2) });

            _calLabel.Text = _calMonth.ToString("MMMM yyyy", Es);

            if (rt == null || rt.Calendar == null) { _calTotal.Text = ""; return; }

            var totals = rt.Calendar.Totals(FundingGuard);
            // Hoy en vivo: P&L de sesión (realized+unrealized) sumado sobre master+slaves.
            // Anda desde el día uno (no necesita cierre previo, a diferencia del delta de net-liq).
            var today = TradingDay.EtDate(DateTime.UtcNow);
            totals[today] = LiveTodayPnl(rt);

            int days = DateTime.DaysInMonth(_calMonth.Year, _calMonth.Month);
            int lead = ((int)_calMonth.DayOfWeek + 6) % 7; // lunes = 0
            for (int i = 0; i < lead; i++) _calGrid.Children.Add(new Border());

            double maxAbs = 0;
            for (int d = 1; d <= days; d++)
            {
                var date = new DateTime(_calMonth.Year, _calMonth.Month, d);
                if (totals.TryGetValue(date, out double v)) maxAbs = Math.Max(maxAbs, Math.Abs(v));
            }

            double monthSum = 0;
            for (int d = 1; d <= days; d++)
            {
                var date = new DateTime(_calMonth.Year, _calMonth.Month, d);
                bool has = totals.TryGetValue(date, out double v);
                if (has) monthSum += v;
                _calGrid.Children.Add(DayCell(d, has ? (double?)v : null, maxAbs, date == today));
            }

            _calTotal.Text = "Mes: " + monthSum.ToString("+0;-0;0");
            _calTotal.Foreground = monthSum > 0 ? Green : monthSum < 0 ? Red : Dim;
        }

        private static double LiveTodayPnl(IdemRuntime rt)
        {
            if (rt.Config == null || rt.DayCache == null) return 0;
            double sum = 0;
            if (rt.Resolve != null)
            {
                var m = rt.Resolve(rt.Config.MasterAccount);
                if (m != null) sum += rt.DayCache.Get(m.Name);
            }
            foreach (var sc in rt.Config.Slaves) sum += rt.DayCache.Get(sc.Account);
            return sum;
        }

        private UIElement DayCell(int day, double? pnl, double maxAbs, bool isToday)
        {
            var border = new Border
            {
                Margin = new Thickness(2), Padding = new Thickness(4), MinHeight = 44,
                CornerRadius = new CornerRadius(3), Background = CellBrush(pnl, maxAbs)
            };
            if (isToday) { border.BorderBrush = Brushes.White; border.BorderThickness = new Thickness(1); }

            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = day.ToString(), Foreground = Dim, FontSize = 10 });
            if (pnl.HasValue)
                sp.Children.Add(new TextBlock { Text = pnl.Value.ToString("+0;-0;0"), Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold });
            border.Child = sp;
            return border;
        }

        private static Brush CellBrush(double? pnl, double maxAbs)
        {
            if (!pnl.HasValue || pnl.Value == 0) return new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x18));
            byte a = (byte)(40 + 150 * Heatmap.Intensity(pnl.Value, maxAbs));
            return Heatmap.Bucket(pnl.Value) == HeatLevel.Gain
                ? new SolidColorBrush(Color.FromArgb(a, 0x22, 0xc5, 0x5e))
                : new SolidColorBrush(Color.FromArgb(a, 0xef, 0x44, 0x44));
        }

        private void SaveConfig()
        {
            var rt = IdemRuntime.Instance;
            if (rt == null || rt.Reconfigure == null) { _cfgMsg.Text = "motor no arrancado"; return; }
            try
            {
                var cfg = IdemConfig.Parse(_cfgBox.Text);
                if (string.IsNullOrWhiteSpace(cfg.MasterAccount)) { _cfgMsg.Text = "falta master="; return; }
                rt.Reconfigure(cfg);
                _cfgMsg.Text = "guardado y aplicado (" + cfg.Slaves.Count + " slaves)";
            }
            catch (Exception ex) { _cfgMsg.Text = "error: " + ex.Message; }
        }

        private UIElement RowUi(FleetRow r)
        {
            var tb = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 4), FontSize = 13, Foreground = Brushes.White,
                Text = (r.IsMaster ? "★ " : "    ") + r.Account
                     + "    net " + r.Net
                     + "    día " + r.DayPnl.ToString("+0;-0;0")
                     + (r.GuardBlocking ? "    ⚠ BLOQUEADO" : "")
            };
            if (r.GuardBlocking) tb.Foreground = Warn;
            else if (r.DayPnl > 0) tb.Foreground = Green;
            else if (r.DayPnl < 0) tb.Foreground = Red;
            return tb;
        }
    }
}
