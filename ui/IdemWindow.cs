using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Idem.Core;
using Idem.Nt;

namespace Idem.Ui
{
    // Ventana del dashboard. Lee el estado vivo de IdemRuntime con un timer de UI
    // (~300ms) y muestra la flota (master + slaves) con net, P&L del día y si el guard
    // los está frenando. Controles y config vienen en las Tasks 5-6.
    public class IdemWindow : Window
    {
        private static IdemWindow _instance;

        private readonly StackPanel _fleet = new StackPanel();
        private readonly TextBlock _status = new TextBlock();
        private DispatcherTimer _timer;

        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e));
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
        private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xea, 0xb3, 0x08));

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
            Width = 720; Height = 480;
            Background = new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0f));

            _status.Foreground = Brushes.White;
            _status.FontSize = 13;
            _status.Margin = new Thickness(0, 0, 0, 10);

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(_status);
            root.Children.Add(_fleet);
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Closed += (s, e) => { try { _timer.Stop(); } catch { } };
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

            _status.Text = "Replicación: " + (rt.Config.Enabled ? "ON" : "OFF");

            var inst = rt.LastInstrument;
            string instName = inst != null ? inst.FullName : "";
            var inputs = new List<FleetInput>();

            var master = rt.Resolve != null ? rt.Resolve(rt.Config.MasterAccount) : null;
            inputs.Add(new FleetInput
            {
                Account = rt.Config.MasterAccount,
                IsMaster = true,
                Net = inst != null ? rt.Tracker.Net(rt.Config.MasterAccount + "|" + instName) : 0,
                DayPnl = master != null ? rt.DayCache.Get(master.Name) : 0,
                DailyLossLimit = 0
            });

            foreach (var sc in rt.Config.Slaves)
                inputs.Add(new FleetInput
                {
                    Account = sc.Account,
                    IsMaster = false,
                    Net = inst != null ? rt.Tracker.Net(sc.Account + "|" + instName) : 0,
                    DayPnl = rt.DayCache.Get(sc.Account),
                    DailyLossLimit = sc.DailyLossLimit
                });

            foreach (var row in FleetView.Build(inputs))
                _fleet.Children.Add(RowUi(row));
        }

        private UIElement RowUi(FleetRow r)
        {
            var tb = new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 4),
                FontSize = 13,
                Foreground = Brushes.White,
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
