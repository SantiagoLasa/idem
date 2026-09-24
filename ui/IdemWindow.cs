using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Idem.Ui
{
    // Ventana del dashboard de Idem. Singleton: una sola instancia; ShowOrActivate
    // la crea o la trae al frente. Por ahora vacía (Task 3); la flota, controles y
    // config vienen en las tareas siguientes de la Fase 4.
    public class IdemWindow : Window
    {
        private static IdemWindow _instance;

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
            Content = new TextBlock
            {
                Text = "Idem — dashboard",
                Foreground = Brushes.White,
                Margin = new Thickness(16),
                FontSize = 16
            };
        }
    }
}
