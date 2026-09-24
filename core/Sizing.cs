namespace Idem.Core
{
    // Tamaño del slave respecto del master. 1:1 por defecto; el ratio queda
    // aislado por si algún día cambia, pero el resto del sistema asume 1:1.
    public static class Sizing
    {
        public static int SlaveTarget(int masterNet, int ratio = 1) => masterNet * ratio;
    }
}
