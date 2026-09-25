using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        private readonly Border _statusPill = new Border();
        private readonly TextBox _cfgBox = new TextBox();
        private readonly TextBlock _cfgMsg = new TextBlock();
        private readonly TextBlock _pauseText = new TextBlock();
        private DispatcherTimer _timer;

        // Editor del tope de pérdida diaria por slave (barra en vivo + campo editable).
        private const double GuardBarWidth = 150;
        private readonly StackPanel _guardHost = new StackPanel();
        private readonly TextBlock _guardMsg = new TextBlock();
        private readonly List<GuardRow> _guardRows = new List<GuardRow>();
        private string _guardSig;

        private sealed class GuardRow
        {
            public string Account;
            public double LimitValue;
            public TextBox LimitBox;
            public TextBlock DayText;
            public Border BarFill;
        }

        // Selector de cuentas: por cada cuenta disponible, radio (master) + check (slave).
        private const double NewSlaveDefaultLimit = 1000;
        private readonly StackPanel _acctHost = new StackPanel();
        private readonly TextBlock _acctMsg = new TextBlock();
        private readonly List<AccountRow> _acctRows = new List<AccountRow>();
        private string _acctSig;

        private sealed class AccountRow
        {
            public string Account;
            public RadioButton MasterRb;
            public CheckBox SlaveCb;
        }

        // Filtro de cuentas visibles (para ocultar cuentas quemadas del listado de NT8).
        // Se persiste en idem-visible.txt (una cuenta por línea) para no rehacerlo cada sesión.
        private readonly TextBlock _visGlyph = new TextBlock();
        private readonly StackPanel _visPanel = new StackPanel();
        private readonly ScrollViewer _visScroll = new ScrollViewer();
        private HashSet<string> _visibleSet;
        private string _visAvailSig;
        private string _visiblePath;

        // Guard de funding: un delta de net-liq mayor a esto es depósito/retiro, no P&L.
        private const double FundingGuard = 1000000;
        private readonly UniformGrid _calGrid = new UniformGrid { Columns = 7 };
        private readonly TextBlock _calLabel = new TextBlock();
        private readonly TextBlock _calTotal = new TextBlock();
        private readonly Border _calTotalPill = new Border();
        private DateTime _calMonth;
        private static readonly CultureInfo Es = new CultureInfo("es-ES");

        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));
        private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x94, 0xa3, 0xb8));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6));
        private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x18));
        private static readonly Brush CellEmpty = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1f));
        private static readonly Brush BorderCol = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x33));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8b));
        private static readonly Brush NavHover = new SolidColorBrush(Color.FromRgb(0x1f, 0x2a, 0x44));

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
            Width = 760; Height = 640;
            Background = new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0f));

            // --- Barra superior: marca + pill de estado + controles ---
            var brand = new TextBlock { Text = "IDEM", Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            _status.FontSize = 12; _status.FontWeight = FontWeights.SemiBold; _status.VerticalAlignment = VerticalAlignment.Center;
            _statusPill.CornerRadius = new CornerRadius(12); _statusPill.Padding = new Thickness(11, 3, 11, 3);
            _statusPill.Margin = new Thickness(12, 0, 0, 0); _statusPill.VerticalAlignment = VerticalAlignment.Center; _statusPill.Child = _status;

            var headerLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerLeft.Children.Add(brand);
            headerLeft.Children.Add(_statusPill);

            _pauseText.Text = "Pausa"; _pauseText.Foreground = Brushes.White; _pauseText.FontSize = 13; _pauseText.FontWeight = FontWeights.SemiBold;
            var flatText = new TextBlock { Text = "FLATTEN ALL", Foreground = Red, FontSize = 13, FontWeight = FontWeights.Bold };

            var headerRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            headerRight.Children.Add(Btn(_pauseText, () => TogglePause()));
            headerRight.Children.Add(Btn(flatText, () => FlattenAll()));

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(headerRight, Dock.Right);
            header.Children.Add(headerRight);
            header.Children.Add(headerLeft);

            // --- Flota: encabezado de columnas + filas ---
            var fleetHead = FleetGrid();
            fleetHead.Margin = new Thickness(0, 0, 0, 8);
            fleetHead.Children.Add(Col(0, HeaderLabel("CUENTA", HorizontalAlignment.Left)));
            fleetHead.Children.Add(Col(1, HeaderLabel("NET", HorizontalAlignment.Right)));
            fleetHead.Children.Add(Col(2, HeaderLabel("P&L DÍA", HorizontalAlignment.Right)));
            fleetHead.Children.Add(Col(3, HeaderLabel("ESTADO", HorizontalAlignment.Right)));
            var fleetBody = new StackPanel();
            fleetBody.Children.Add(fleetHead);
            fleetBody.Children.Add(_fleet);

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(header);
            root.Children.Add(SectionCard("FLOTA", fleetBody, new Thickness(0, 0, 0, 0)));
            root.Children.Add(SectionCard("RÉPLICAS", _feed, new Thickness(0, 14, 0, 0)));

            var t0 = TradingDay.EtDate(DateTime.UtcNow);
            _calMonth = new DateTime(t0.Year, t0.Month, 1);

            var caption = new TextBlock { Text = "CALENDARIO  ·  P&L DIARIO  ·  CORTE 5PM ET", Foreground = Muted, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };

            // Header: ‹ mes › ................ [pill total del mes]
            _calLabel.Foreground = Brushes.White; _calLabel.FontSize = 17; _calLabel.FontWeight = FontWeights.SemiBold;
            _calLabel.VerticalAlignment = VerticalAlignment.Center; _calLabel.MinWidth = 175; _calLabel.TextAlignment = TextAlignment.Center;

            var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nav.Children.Add(NavBox("‹", () => { _calMonth = _calMonth.AddMonths(-1); RefreshCalendar(); }));
            nav.Children.Add(_calLabel);
            nav.Children.Add(NavBox("›", () => { _calMonth = _calMonth.AddMonths(1); RefreshCalendar(); }));

            _calTotal.FontSize = 14; _calTotal.FontWeight = FontWeights.Bold; _calTotal.Foreground = Brushes.White;
            _calTotalPill.CornerRadius = new CornerRadius(6); _calTotalPill.Padding = new Thickness(12, 5, 12, 5);
            _calTotalPill.VerticalAlignment = VerticalAlignment.Center; _calTotalPill.Child = _calTotal;

            var calHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(_calTotalPill, Dock.Right);
            calHeader.Children.Add(_calTotalPill);
            calHeader.Children.Add(nav);

            // Fila de días de la semana (lunes → domingo; fin de semana en acento).
            var weekHead = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
            string[] wd = { "LUN", "MAR", "MIÉ", "JUE", "VIE", "SÁB", "DOM" };
            for (int i = 0; i < 7; i++)
                weekHead.Children.Add(new TextBlock
                {
                    Text = wd[i], FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Foreground = i >= 5 ? Accent : Muted, TextAlignment = TextAlignment.Center, Margin = new Thickness(3, 0, 3, 0)
                });

            var calInner = new StackPanel();
            calInner.Children.Add(caption);
            calInner.Children.Add(calHeader);
            calInner.Children.Add(weekHead);
            calInner.Children.Add(_calGrid);

            var calCard = new Border
            {
                Background = CardBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(16),
                BorderBrush = BorderCol, BorderThickness = new Thickness(1), Margin = new Thickness(0, 14, 0, 0),
                Child = calInner
            };
            root.Children.Add(calCard);

            // --- Selector de master y slaves ---
            var acctHelp = new TextBlock
            {
                Text = "Elegí el master (uno) y las slaves (las que quieras). Los topes se ajustan abajo.",
                Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap
            };
            _visiblePath = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "bin", "Custom", "Idem", "idem-visible.txt");

            // Desplegable "Cuentas visibles": tildá cuáles mostrar en la tabla de abajo.
            _visGlyph.Text = "▸"; _visGlyph.Foreground = Accent; _visGlyph.FontSize = 12; _visGlyph.Margin = new Thickness(0, 0, 8, 0); _visGlyph.VerticalAlignment = VerticalAlignment.Center;
            var visHeaderInner = new StackPanel { Orientation = Orientation.Horizontal };
            visHeaderInner.Children.Add(_visGlyph);
            visHeaderInner.Children.Add(new TextBlock { Text = "Cuentas visibles (elegí cuáles mostrar)", Foreground = Dim, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var visHeader = new Border
            {
                Background = CellEmpty, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6),
                BorderBrush = BorderCol, BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 6), Child = visHeaderInner
            };
            visHeader.MouseLeftButtonUp += (s, e) => ToggleVisible();

            _visScroll.MaxHeight = 180; _visScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _visScroll.Visibility = Visibility.Collapsed; _visScroll.Margin = new Thickness(0, 0, 0, 10);
            _visScroll.Content = _visPanel;

            var acctApplyText = new TextBlock { Text = "Aplicar cuentas", Foreground = Green, FontSize = 13, FontWeight = FontWeights.SemiBold };
            var acctApplyBtn = Btn(acctApplyText, () => ApplyAccounts());
            acctApplyBtn.Margin = new Thickness(0, 12, 0, 0);
            acctApplyBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _acctMsg.Foreground = Muted; _acctMsg.FontSize = 11; _acctMsg.Margin = new Thickness(0, 8, 0, 0); _acctMsg.TextWrapping = TextWrapping.Wrap;

            var acctBody = new StackPanel();
            acctBody.Children.Add(acctHelp);
            acctBody.Children.Add(visHeader);
            acctBody.Children.Add(_visScroll);
            acctBody.Children.Add(_acctHost);
            acctBody.Children.Add(acctApplyBtn);
            acctBody.Children.Add(_acctMsg);
            root.Children.Add(SectionCard("MASTER Y SLAVES  ·  ELEGIR CUENTAS", acctBody, new Thickness(0, 14, 0, 0)));

            // --- Editor amigable del tope de pérdida diaria ---
            var guardHelp = new TextBlock
            {
                Text = "Frena entradas nuevas cuando el P&L del día (realized+unrealized) toca −tope. Las salidas nunca se bloquean.",
                Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap
            };
            var guardApplyText = new TextBlock { Text = "Aplicar topes", Foreground = Green, FontSize = 13, FontWeight = FontWeights.SemiBold };
            var guardApplyBtn = Btn(guardApplyText, () => ApplyGuards());
            guardApplyBtn.Margin = new Thickness(0, 12, 0, 0);
            guardApplyBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _guardMsg.Foreground = Muted; _guardMsg.FontSize = 11; _guardMsg.Margin = new Thickness(0, 8, 0, 0); _guardMsg.TextWrapping = TextWrapping.Wrap;

            var guardBody = new StackPanel();
            guardBody.Children.Add(guardHelp);
            guardBody.Children.Add(_guardHost);
            guardBody.Children.Add(guardApplyBtn);
            guardBody.Children.Add(_guardMsg);
            root.Children.Add(SectionCard("GUARD  ·  TOPE DE PÉRDIDA DIARIA POR SLAVE", guardBody, new Thickness(0, 14, 0, 0)));

            var cfgHelp = new TextBlock
            {
                Text = "master=Cuenta   ·   slave=Cuenta,PérdidaDiaria   ·   enabled=true/false   —  se aplica sin reiniciar",
                Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            };
            _cfgBox.AcceptsReturn = true;
            _cfgBox.MinLines = 5;
            _cfgBox.FontFamily = new FontFamily("Consolas");
            _cfgBox.FontSize = 13;
            _cfgBox.Background = new SolidColorBrush(Color.FromRgb(0x0d, 0x0d, 0x14));
            _cfgBox.Foreground = Brushes.White;
            _cfgBox.CaretBrush = Accent;
            _cfgBox.BorderBrush = BorderCol;
            _cfgBox.BorderThickness = new Thickness(1);
            _cfgBox.Padding = new Thickness(10);
            var rt0 = IdemRuntime.Instance;
            if (rt0 != null && rt0.Config != null) _cfgBox.Text = IdemConfigWriter.ToText(rt0.Config);

            var saveText = new TextBlock { Text = "Guardar", Foreground = Green, FontSize = 13, FontWeight = FontWeights.SemiBold };
            var saveBtn = Btn(saveText, () => SaveConfig());
            saveBtn.Margin = new Thickness(0, 10, 0, 0);
            saveBtn.HorizontalAlignment = HorizontalAlignment.Left;

            _cfgMsg.Foreground = Muted; _cfgMsg.FontSize = 11; _cfgMsg.Margin = new Thickness(0, 8, 0, 0); _cfgMsg.TextWrapping = TextWrapping.Wrap;

            var cfgBody = new StackPanel();
            cfgBody.Children.Add(cfgHelp);
            cfgBody.Children.Add(_cfgBox);
            cfgBody.Children.Add(saveBtn);
            cfgBody.Children.Add(_cfgMsg);

            root.Children.Add(SectionCard("CONFIGURACIÓN AVANZADA  ·  MASTER · SLAVES · ENABLED", cfgBody, new Thickness(0, 14, 0, 0)));

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
                _status.Text = "○ SIN MOTOR"; _status.Foreground = Muted;
                _statusPill.Background = CellEmpty;
                return;
            }

            bool on = rt.Config.Enabled;
            _status.Text = on ? "● ACTIVO" : "● PAUSADO";
            _status.Foreground = on ? Green : Warn;
            _statusPill.Background = on
                ? new SolidColorBrush(Color.FromArgb(0x33, 0x22, 0xc5, 0x5e))
                : new SolidColorBrush(Color.FromArgb(0x33, 0xea, 0xb3, 0x08));
            _pauseText.Text = on ? "Pausa" : "Reanudar";

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

            RefreshAccounts(rt);
            RefreshGuard(rt);
            RefreshCalendar();
        }

        private static List<string> AvailableAccounts()
        {
            var list = new List<string>();
            try
            {
                lock (Account.All)
                    foreach (var a in Account.All)
                        if (!list.Contains(a.Name)) list.Add(a.Name);
            }
            catch { }
            list.Sort();
            return list;
        }

        private void ToggleVisible()
        {
            bool show = _visScroll.Visibility != Visibility.Visible;
            _visScroll.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _visGlyph.Text = show ? "▾" : "▸";
        }

        private HashSet<string> LoadVisible()
        {
            try
            {
                if (!File.Exists(_visiblePath)) return null;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(_visiblePath))
                {
                    var line = raw.Trim();
                    if (line.Length > 0) set.Add(line);
                }
                return set;
            }
            catch { return null; }
        }

        private void SaveVisible()
        {
            try { File.WriteAllLines(_visiblePath, new List<string>(_visibleSet).ToArray()); }
            catch { }
        }

        // Reconstruye el selector sólo cuando cambian las cuentas disponibles, el filtro de
        // visibles o la elección aplicada (no en cada tick — si no, borraría lo que estás
        // seleccionando). Sólo se muestran las cuentas visibles + las que ya están en la config.
        private void RefreshAccounts(IdemRuntime rt)
        {
            var avail = AvailableAccounts();

            // Primera vez sin archivo: arrancar mostrando todas las disponibles.
            if (_visibleSet == null)
            {
                _visibleSet = LoadVisible();
                if (_visibleSet == null) { _visibleSet = new HashSet<string>(avail, StringComparer.OrdinalIgnoreCase); SaveVisible(); }
            }

            var availSig = string.Join(",", avail.ToArray());
            if (availSig != _visAvailSig) { RebuildVisiblePanel(avail); _visAvailSig = availSig; }

            // Mostrar en la tabla: cuentas visibles + las que ya están en la config (para no
            // perder de vista una slave/master aunque esté oculta o desconectada).
            var all = new List<string>();
            foreach (var name in avail) if (_visibleSet.Contains(name)) all.Add(name);
            if (!string.IsNullOrEmpty(rt.Config.MasterAccount) && !ContainsCi(all, rt.Config.MasterAccount)) all.Add(rt.Config.MasterAccount);
            foreach (var sc in rt.Config.Slaves) if (!ContainsCi(all, sc.Account)) all.Add(sc.Account);
            all.Sort();

            var sig = string.Join(",", all.ToArray()) + "|" + rt.Config.MasterAccount + "|";
            foreach (var sc in rt.Config.Slaves) sig += sc.Account + ";";
            if (sig != _acctSig) { RebuildAccounts(rt, all); _acctSig = sig; }
        }

        private void RebuildVisiblePanel(List<string> avail)
        {
            _visPanel.Children.Clear();
            if (avail.Count == 0)
            {
                _visPanel.Children.Add(new TextBlock { Text = "Sin cuentas disponibles.", Foreground = Muted, FontSize = 12 });
                return;
            }

            foreach (var name in avail)
            {
                var cb = new CheckBox
                {
                    Content = name, Foreground = Brushes.White, FontSize = 12,
                    IsChecked = _visibleSet.Contains(name), Margin = new Thickness(0, 3, 0, 3)
                };
                var captured = name; // handlers después de fijar IsChecked → sin eventos espurios
                cb.Checked += (s, e) => { _visibleSet.Add(captured); SaveVisible(); _acctSig = null; };
                cb.Unchecked += (s, e) => { _visibleSet.Remove(captured); SaveVisible(); _acctSig = null; };
                _visPanel.Children.Add(cb);
            }
        }

        private void RebuildAccounts(IdemRuntime rt, List<string> all)
        {
            _acctHost.Children.Clear();
            _acctRows.Clear();

            if (all.Count == 0)
            {
                _acctHost.Children.Add(new TextBlock { Text = "Sin cuentas disponibles (¿conectado?).", Foreground = Muted, FontSize = 12 });
                return;
            }

            var head = AccountGrid();
            head.Margin = new Thickness(0, 0, 0, 8);
            head.Children.Add(Col(0, HeaderLabel("CUENTA", HorizontalAlignment.Left)));
            head.Children.Add(Col(1, HeaderLabel("MASTER", HorizontalAlignment.Center)));
            head.Children.Add(Col(2, HeaderLabel("SLAVE", HorizontalAlignment.Center)));
            _acctHost.Children.Add(head);

            foreach (var name in all)
            {
                var g = AccountGrid();
                g.Margin = new Thickness(0, 4, 0, 4);
                g.Children.Add(Col(0, new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }));

                var rb = new RadioButton
                {
                    GroupName = "idem_master", Foreground = Brushes.White,
                    IsChecked = string.Equals(name, rt.Config.MasterAccount, StringComparison.OrdinalIgnoreCase),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                g.Children.Add(Col(1, rb));

                var cb = new CheckBox
                {
                    Foreground = Brushes.White,
                    IsChecked = SlaveContains(rt.Config, name),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                g.Children.Add(Col(2, cb));

                _acctHost.Children.Add(g);
                _acctRows.Add(new AccountRow { Account = name, MasterRb = rb, SlaveCb = cb });
            }
        }

        private void ApplyAccounts()
        {
            var rt = IdemRuntime.Instance;
            if (rt == null || rt.Config == null || rt.Reconfigure == null) { _acctMsg.Text = "✗ motor no arrancado"; _acctMsg.Foreground = Red; return; }

            string master = null;
            foreach (var ar in _acctRows)
                if (ar.MasterRb.IsChecked == true) { master = ar.Account; break; }

            if (master == null) { _acctMsg.Text = "✗ elegí un master"; _acctMsg.Foreground = Red; return; }

            var cfg = new IdemConfig { MasterAccount = master, Enabled = rt.Config.Enabled };
            foreach (var ar in _acctRows)
            {
                if (ar.SlaveCb.IsChecked != true) continue;
                if (string.Equals(ar.Account, master, StringComparison.OrdinalIgnoreCase)) continue; // no puede ser master y slave
                cfg.Slaves.Add(new SlaveConfig { Account = ar.Account, DailyLossLimit = ExistingLimit(rt.Config, ar.Account) });
            }

            rt.Reconfigure(cfg);
            _cfgBox.Text = IdemConfigWriter.ToText(cfg);
            _acctMsg.Text = "✓ master " + master + " · " + cfg.Slaves.Count + " slaves (ajustá los topes abajo)";
            _acctMsg.Foreground = Green;
        }

        private static double ExistingLimit(IdemConfig cfg, string account)
        {
            foreach (var sc in cfg.Slaves)
                if (string.Equals(sc.Account, account, StringComparison.OrdinalIgnoreCase)) return sc.DailyLossLimit;
            return NewSlaveDefaultLimit;
        }

        private static bool SlaveContains(IdemConfig cfg, string account)
        {
            foreach (var sc in cfg.Slaves)
                if (string.Equals(sc.Account, account, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ContainsCi(List<string> list, string val)
        {
            foreach (var s in list)
                if (string.Equals(s, val, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static Grid AccountGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            return g;
        }

        // Reconstruye las filas del guard sólo cuando cambia el conjunto de slaves o sus
        // topes aplicados (no en cada tick — si no, borraría lo que estás tipeando). La
        // firma usa rt.Config, que sólo cambia al Reconfigure, nunca al escribir en el campo.
        private void RefreshGuard(IdemRuntime rt)
        {
            var sig = rt.Config.MasterAccount + "|";
            foreach (var sc in rt.Config.Slaves) sig += sc.Account + "=" + sc.DailyLossLimit + ";";
            if (sig != _guardSig) { RebuildGuard(rt); _guardSig = sig; }

            foreach (var gr in _guardRows)
            {
                double dayPnl = rt.DayCache.Get(gr.Account);
                double loss = dayPnl < 0 ? -dayPnl : 0;
                double frac = gr.LimitValue > 0 ? Math.Min(1, loss / gr.LimitValue) : 0;
                bool blocked = gr.LimitValue > 0 && dayPnl <= -gr.LimitValue;

                gr.BarFill.Width = frac * GuardBarWidth;
                gr.BarFill.Background = blocked ? Red : frac >= 0.7 ? Warn : Green;
                gr.DayText.Text = FormatMoney(dayPnl) + "  /  tope " + gr.LimitValue.ToString("$#,0", Es);
                gr.DayText.Foreground = blocked ? Red : frac >= 0.7 ? Warn : Dim;
            }
        }

        private void RebuildGuard(IdemRuntime rt)
        {
            _guardHost.Children.Clear();
            _guardRows.Clear();

            if (rt.Config.Slaves.Count == 0)
            {
                _guardHost.Children.Add(new TextBlock { Text = "Sin slaves configurados.", Foreground = Muted, FontSize = 12 });
                return;
            }

            foreach (var sc in rt.Config.Slaves)
            {
                var g = GuardGrid();
                g.Margin = new Thickness(0, 5, 0, 5);

                g.Children.Add(Col(0, new TextBlock { Text = sc.Account, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }));

                var box = new TextBox
                {
                    Text = sc.DailyLossLimit.ToString("0.##", CultureInfo.InvariantCulture),
                    Width = 80, FontSize = 13, TextAlignment = TextAlignment.Right,
                    Background = new SolidColorBrush(Color.FromRgb(0x0d, 0x0d, 0x14)), Foreground = Brushes.White,
                    CaretBrush = Accent, BorderBrush = BorderCol, BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 3, 6, 3), VerticalAlignment = VerticalAlignment.Center
                };
                var boxWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                boxWrap.Children.Add(new TextBlock { Text = "$", Foreground = Muted, FontSize = 13, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                boxWrap.Children.Add(box);
                g.Children.Add(Col(1, boxWrap));

                var track = new Border { Width = GuardBarWidth, Height = 8, CornerRadius = new CornerRadius(4), Background = CellEmpty, VerticalAlignment = VerticalAlignment.Center };
                var fill = new Border { Width = 0, Height = 8, CornerRadius = new CornerRadius(4), Background = Green, HorizontalAlignment = HorizontalAlignment.Left };
                track.Child = fill;
                var dayText = new TextBlock { Foreground = Dim, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                var prog = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                prog.Children.Add(track);
                prog.Children.Add(dayText);
                g.Children.Add(Col(2, prog));

                _guardHost.Children.Add(g);
                _guardRows.Add(new GuardRow { Account = sc.Account, LimitValue = sc.DailyLossLimit, LimitBox = box, DayText = dayText, BarFill = fill });
            }
        }

        private void ApplyGuards()
        {
            var rt = IdemRuntime.Instance;
            if (rt == null || rt.Config == null || rt.Reconfigure == null) { _guardMsg.Text = "✗ motor no arrancado"; _guardMsg.Foreground = Red; return; }

            var cfg = new IdemConfig { MasterAccount = rt.Config.MasterAccount, Enabled = rt.Config.Enabled };
            foreach (var gr in _guardRows)
            {
                var raw = gr.LimitBox.Text.Trim().Replace("$", "");
                if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double lim) || lim < 0)
                {
                    _guardMsg.Text = "✗ tope inválido en " + gr.Account; _guardMsg.Foreground = Red; return;
                }
                cfg.Slaves.Add(new SlaveConfig { Account = gr.Account, DailyLossLimit = lim });
            }

            rt.Reconfigure(cfg);
            _cfgBox.Text = IdemConfigWriter.ToText(cfg); // mantener la config cruda en sync
            _guardMsg.Text = "✓ topes aplicados (" + cfg.Slaves.Count + " slaves)";
            _guardMsg.Foreground = Green;
        }

        private static Grid GuardGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            return g;
        }

        private void RefreshCalendar()
        {
            var rt = IdemRuntime.Instance;
            _calGrid.Children.Clear();

            var label = _calMonth.ToString("MMMM yyyy", Es);
            _calLabel.Text = char.ToUpper(label[0]) + label.Substring(1);

            if (rt == null || rt.Calendar == null)
            {
                _calTotal.Text = "—"; _calTotalPill.Background = CellEmpty;
                return;
            }

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

            _calTotal.Text = FormatMoney(monthSum);
            _calTotalPill.Background = monthSum > 0
                ? new SolidColorBrush(Color.FromArgb(0x33, 0x22, 0xc5, 0x5e))
                : monthSum < 0
                    ? new SolidColorBrush(Color.FromArgb(0x33, 0xef, 0x44, 0x44))
                    : CellEmpty;
            _calTotal.Foreground = monthSum > 0 ? Green : monthSum < 0 ? Red : Dim;
        }

        private Border NavBox(string glyph, Action onClick)
        {
            var b = new Border
            {
                Background = CellEmpty, CornerRadius = new CornerRadius(6), Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(11, 3, 11, 3), BorderBrush = BorderCol, BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = glyph, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.Bold }
            };
            b.MouseEnter += (s, e) => b.Background = NavHover;
            b.MouseLeave += (s, e) => b.Background = CellEmpty;
            b.MouseLeftButtonUp += (s, e) => onClick();
            return b;
        }

        private static string FormatMoney(double v)
        {
            return v.ToString("+$#,0;-$#,0;$0", Es);
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
                Margin = new Thickness(3), MinHeight = 54, CornerRadius = new CornerRadius(7),
                Background = CellBrush(pnl, maxAbs),
                BorderBrush = isToday ? Accent : BorderCol,
                BorderThickness = new Thickness(isToday ? 2 : 1)
            };

            var grid = new Grid { Margin = new Thickness(7, 5, 7, 5) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var dayNum = new TextBlock
            {
                Text = day.ToString(), FontSize = 11, FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = isToday ? Accent : Muted, HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetRow(dayNum, 0);
            grid.Children.Add(dayNum);

            if (pnl.HasValue)
            {
                var val = new TextBlock
                {
                    Text = FormatMoney(pnl.Value), FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetRow(val, 1);
                grid.Children.Add(val);
            }

            border.Child = grid;
            return border;
        }

        // Fondo de la celda: vacío = casi negro; con P&L, degradé vertical verde/rojo cuya
        // opacidad escala con la intensidad (|P&L| contra el máximo del mes).
        private static Brush CellBrush(double? pnl, double maxAbs)
        {
            if (!pnl.HasValue || pnl.Value == 0) return CellEmpty;

            byte a = (byte)(55 + 175 * Heatmap.Intensity(pnl.Value, maxAbs));
            Color c = Heatmap.Bucket(pnl.Value) == HeatLevel.Gain
                ? Color.FromArgb(a, 0x22, 0xc5, 0x5e)
                : Color.FromArgb(a, 0xef, 0x44, 0x44);
            Color top = Color.FromArgb(a,
                (byte)Math.Min(255, c.R + 22), (byte)Math.Min(255, c.G + 22), (byte)Math.Min(255, c.B + 22));
            return new LinearGradientBrush(top, c, 90);
        }

        private void SaveConfig()
        {
            var rt = IdemRuntime.Instance;
            if (rt == null || rt.Reconfigure == null) { _cfgMsg.Text = "✗ motor no arrancado"; _cfgMsg.Foreground = Red; return; }
            try
            {
                var cfg = IdemConfig.Parse(_cfgBox.Text);
                if (string.IsNullOrWhiteSpace(cfg.MasterAccount)) { _cfgMsg.Text = "✗ falta master="; _cfgMsg.Foreground = Red; return; }
                rt.Reconfigure(cfg);
                _cfgMsg.Text = "✓ guardado y aplicado (" + cfg.Slaves.Count + " slaves)";
                _cfgMsg.Foreground = Green;
            }
            catch (Exception ex) { _cfgMsg.Text = "✗ error: " + ex.Message; _cfgMsg.Foreground = Red; }
        }

        private UIElement RowUi(FleetRow r)
        {
            var g = FleetGrid();

            var acct = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (r.IsMaster)
                acct.Children.Add(new TextBlock { Text = "★", Foreground = Accent, FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            acct.Children.Add(new TextBlock { Text = r.Account, Foreground = Brushes.White, FontSize = 13, FontWeight = r.IsMaster ? FontWeights.Bold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(Col(0, acct));

            g.Children.Add(Col(1, new TextBlock { Text = r.Net.ToString(), Foreground = Dim, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }));

            var pnlBrush = r.DayPnl > 0 ? Green : r.DayPnl < 0 ? Red : Dim;
            g.Children.Add(Col(2, new TextBlock { Text = FormatMoney(r.DayPnl), Foreground = pnlBrush, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }));

            if (r.GuardBlocking)
                g.Children.Add(Col(3, WarnPill("BLOQUEADO")));

            return new Border
            {
                BorderBrush = BorderCol, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 7, 0, 7), Child = g
            };
        }

        // --- Helpers de estilo compartidos ---

        private Border Btn(TextBlock label, Action onClick)
        {
            var b = new Border
            {
                Background = CellEmpty, CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0), BorderBrush = BorderCol, BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, Child = label
            };
            b.MouseEnter += (s, e) => b.Background = NavHover;
            b.MouseLeave += (s, e) => b.Background = CellEmpty;
            b.MouseLeftButtonUp += (s, e) => onClick();
            return b;
        }

        private static Border SectionCard(string caption, UIElement body, Thickness margin)
        {
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = caption, Foreground = Muted, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            inner.Children.Add(body);
            return new Border
            {
                Background = CardBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(16),
                BorderBrush = BorderCol, BorderThickness = new Thickness(1), Margin = margin, Child = inner
            };
        }

        private static Grid FleetGrid()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            return g;
        }

        private static FrameworkElement Col(int c, FrameworkElement el)
        {
            Grid.SetColumn(el, c);
            return el;
        }

        private static TextBlock HeaderLabel(string text, HorizontalAlignment align)
        {
            return new TextBlock { Text = text, Foreground = Muted, FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = align };
        }

        private static Border WarnPill(string text)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xea, 0xb3, 0x08)),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "⚠ " + text, Foreground = Warn, FontSize = 11, FontWeight = FontWeights.SemiBold }
            };
        }
    }
}
