# Idem — Plan de implementación: Dashboard WPF (Fase 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Un panel WPF dentro de NT8 para ver la flota en vivo, el feed de réplicas y controlar el copy (Pausa, Flatten All) y su config (master/slaves/topes) — sin editar el `.txt` ni reiniciar.

**Architecture:** El motor publica su estado vivo en un holder estático `IdemRuntime` (tracker, cache de P&L, config mutable, feed de réplicas). La ventana WPF lee de ahí con un timer de UI (~300ms) y arma las filas con un núcleo puro `FleetView.Build`. Pausa togglea `Config.Enabled`; los cambios de config reconfiguran el motor en vivo. Menú "Idem" en el Control Center (patrón probado de PropCommand).

**Tech Stack:** C# (core net48/net8.0; nt NinjaTrader WPF), xUnit.

**Spec:** `bin\Custom\Idem\docs\2026-09-24-idem-design.md`
**Depende de:** Fases 1-3 (todo `Idem.Core.*` + la cáscara NT8 completa).

## Global Constraints

- `core\` NT8-free; `nt\` NinjaTrader (F5+SIM). **Task 1 testeable acá; Tasks 2-6 F5+SIM (las hace el usuario).**
- WPF-en-NT8 es fiddly: todo lo de UI corre en el UI-thread (Dispatcher). Iteraciones de F5 esperables.
- Tema oscuro (fondo `#0a0a0f`, card `#111118`, borde `#1e1e2e`, verde `#22c55e`, rojo `#ef4444`).
- El `idem-config.txt` sigue siendo la fuente persistente; el panel lo lee y lo reescribe.
- SIM sólo con cuentas de simulación.

---

### Task 1: `FleetView.Build` (puro)

**Files:**
- Create: `bin\Custom\Idem\core\FleetView.cs`
- Test: `C:\dev\idem-tests\FleetViewTests.cs`

**Interfaces:**
- Produces:
  - `Idem.Core.FleetRow` — struct `{ string Account; bool IsMaster; int Net; double DayPnl; double DailyLossLimit; bool GuardBlocking; }`.
  - `Idem.Core.FleetInput` — struct `{ string Account; bool IsMaster; int Net; double DayPnl; double DailyLossLimit; }`.
  - `Idem.Core.FleetView.Build(System.Collections.Generic.IReadOnlyList<FleetInput> rows) : System.Collections.Generic.List<FleetRow>` — copia los datos y marca `GuardBlocking = !IsMaster && DayPnl <= -DailyLossLimit` (el master nunca se marca).

- [ ] **Step 1: Escribir el test que falla**

Crear `C:\dev\idem-tests\FleetViewTests.cs`:

```csharp
using System.Collections.Generic;
using Idem.Core;
using Xunit;

public class FleetViewTests
{
    private static FleetInput I(string a, bool m, int net, double pnl, double lim) =>
        new FleetInput { Account = a, IsMaster = m, Net = net, DayPnl = pnl, DailyLossLimit = lim };

    [Fact]
    public void MarksSlaveBlocked_WhenDayLossAtLimit()
    {
        var r = FleetView.Build(new List<FleetInput> { I("S1", false, 0, -250, 250) });
        Assert.True(r[0].GuardBlocking);
    }

    [Fact]
    public void SlaveNotBlocked_WhenUnderLimit()
    {
        var r = FleetView.Build(new List<FleetInput> { I("S1", false, 2, -100, 250) });
        Assert.False(r[0].GuardBlocking);
    }

    [Fact]
    public void MasterNeverBlocked()
    {
        var r = FleetView.Build(new List<FleetInput> { I("M", true, 2, -9999, 250) });
        Assert.False(r[0].GuardBlocking);
    }

    [Fact]
    public void CarriesFieldsThrough()
    {
        var r = FleetView.Build(new List<FleetInput> { I("S1", false, -3, 120, 250) });
        Assert.Equal("S1", r[0].Account);
        Assert.Equal(-3, r[0].Net);
        Assert.Equal(120, r[0].DayPnl);
        Assert.Equal(250, r[0].DailyLossLimit);
    }
}
```

- [ ] **Step 2: Correr para verlo fallar**

Run: `cd /c/dev/idem-tests && dotnet test --filter FleetViewTests`
Expected: FAIL — `FleetView` no existe.

- [ ] **Step 3: Implementar**

Crear `bin\Custom\Idem\core\FleetView.cs`:

```csharp
using System.Collections.Generic;

namespace Idem.Core
{
    public struct FleetInput
    {
        public string Account;
        public bool IsMaster;
        public int Net;
        public double DayPnl;
        public double DailyLossLimit;
    }

    public struct FleetRow
    {
        public string Account;
        public bool IsMaster;
        public int Net;
        public double DayPnl;
        public double DailyLossLimit;
        public bool GuardBlocking;
    }

    // Arma las filas del panel de flota. Marca qué slaves está frenando el guard
    // (perdieron su tope del día). El master nunca se marca. Puro y testeable.
    public static class FleetView
    {
        public static List<FleetRow> Build(IReadOnlyList<FleetInput> rows)
        {
            var result = new List<FleetRow>(rows.Count);
            foreach (var r in rows)
            {
                bool blocking = !r.IsMaster && r.DayPnl <= -r.DailyLossLimit;
                result.Add(new FleetRow
                {
                    Account = r.Account,
                    IsMaster = r.IsMaster,
                    Net = r.Net,
                    DayPnl = r.DayPnl,
                    DailyLossLimit = r.DailyLossLimit,
                    GuardBlocking = blocking
                });
            }
            return result;
        }
    }
}
```

- [ ] **Step 4: Correr para verlo pasar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add core/FleetView.cs && git commit -m "feat(core): FleetView.Build (filas de la flota + flag de guard bloqueando)"
```

---

### Task 2: `IdemRuntime` — holder de estado vivo + feed · F5

**Files:**
- Create: `bin\Custom\Idem\nt\IdemRuntime.cs`
- Modify: `bin\Custom\Idem\Idem.cs` (publicar al runtime)
- Modify: `bin\Custom\Idem\nt\CopyEngine.cs` (feed de réplicas + bloqueos)

**Interfaces:**
- Produces: `Idem.Nt.IdemRuntime` (singleton estático) con: `static IdemRuntime Instance`, campos `PositionTracker Tracker`, `DayPnlCache DayCache`, `IdemConfig Config`, `System.Func<string, NinjaTrader.Cbi.Account> Resolve`, `NinjaTrader.Cbi.Instrument LastInstrument`; feed: `void AddFeed(string line)` (guarda las últimas ~50, thread-safe) y `System.Collections.Generic.List<string> RecentFeed()`.

- [ ] **Step 1: Implementar `IdemRuntime`**

Crear `bin\Custom\Idem\nt\IdemRuntime.cs`:

```csharp
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
```

- [ ] **Step 2: Publicar al runtime en `Idem.cs`**

En `Boot()`, después de crear `_tracker`, `_dayCache`, `cfg`, `resolve`, y ANTES de arrancar, setear el singleton:
```csharp
            IdemRuntime.Instance = new IdemRuntime
            {
                Tracker = _tracker,
                DayCache = _dayCache,
                Config = cfg,
                Resolve = resolve
            };
```
Y cuando llega el primer master fill, guardar el instrumento (en el lambda del FillMonitor):
```csharp
            _fills = new FillMonitor(_tracker, (m, net, inst) =>
            {
                if (IdemRuntime.Instance != null) IdemRuntime.Instance.LastInstrument = inst;
                _engine.OnMasterFill(m, net, inst);
            });
```

- [ ] **Step 3: Feed de réplicas en `CopyEngine`**

En `CopyEngine.Reconcile`, dentro del `foreach (var d in CopyDecision.ForMasterNet(...))`:
- Si `d.Blocked`: `IdemRuntime.Instance?.AddFeed(d.Id + " BLOQUEADO (guard)");`
- Si se manda (`OrderSubmit.Market(...)`): `IdemRuntime.Instance?.AddFeed(d.Id + " " + d.Action + " " + d.Qty + " " + instrument.FullName);`

- [ ] **Step 4: F5**

F5. Expected: compila limpio. El motor sigue funcionando igual; ahora publica estado.

- [ ] **Step 5: Commit** (tras F5)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add nt/IdemRuntime.cs Idem.cs nt/CopyEngine.cs && git commit -m "feat(nt): IdemRuntime (estado vivo + feed de réplicas) publicado por el motor"
```

---

### Task 3: Menú "Idem" + ventana vacía · F5 + SIM

**Files:**
- Create: `bin\Custom\Idem\ui\IdemWindow.cs`
- Modify: `bin\Custom\Idem\Idem.cs` (menú en el Control Center)

**Interfaces:**
- Produces: `Idem.Ui.IdemWindow : System.Windows.Window` con `static void ShowOrActivate()` (crea/activa un singleton).

- [ ] **Step 1: Implementar la ventana vacía**

Crear `bin\Custom\Idem\ui\IdemWindow.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Idem.Ui
{
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
```

- [ ] **Step 2: Menú en `Idem.cs`**

En `OnWindowCreated`, DESPUÉS del `Boot()` guardado, agregar la integración del menú (patrón de PropCommand). Requiere `using NinjaTrader.Gui;`, `using NinjaTrader.Gui.Tools;`, `using System.Windows.Controls;`, `using Idem.Ui;`. Agregar al final de `OnWindowCreated`:

```csharp
            try
            {
                var cc = window as NinjaTrader.Gui.ControlCenter;
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
            }
            catch (Exception ex) { Log("menu error: " + ex.Message); }
```
(Nota: mover el `if (_booted) return;` para que el menú se agregue una sola vez también, o guardarlo con su propio flag; ver PropCommand `_isInitialized`.)

- [ ] **Step 3: F5 + SIM**

F5. En el Control Center → **New** debería aparecer **Idem → Dashboard**. Click → abre la ventana vacía. Ajustar firmas si `ControlCenter`/`NTMenuItem`/`FindFirst` difieren (referencia: `AddOns/PropCommandCore.cs:130-170`).

- [ ] **Step 4: Commit** (tras F5 + abre)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add ui/IdemWindow.cs Idem.cs && git commit -m "feat(ui): menú Idem en el Control Center + ventana del dashboard"
```

---

### Task 4: Flota en vivo dentro de la ventana · F5 + SIM

**Files:**
- Modify: `bin\Custom\Idem\ui\IdemWindow.cs`

**Interfaces:**
- Consumes: `Idem.Nt.IdemRuntime`, `Idem.Core.FleetView`.

- [ ] **Step 1: Grilla de flota + timer**

En `IdemWindow`, reemplazar el `Content` por un layout con: una barra superior (estado) y una lista de filas de flota. Un `DispatcherTimer` (~300ms) lee `IdemRuntime.Instance`, arma `FleetView.Build` y refresca. Código:

```csharp
// campos
private readonly StackPanel _fleet = new StackPanel();
private System.Windows.Threading.DispatcherTimer _timer;

// en el constructor, en vez del TextBlock:
var scroll = new ScrollViewer { Content = _fleet, Margin = new Thickness(12) };
Content = scroll;
_timer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(300) };
_timer.Tick += (s, e) => Refresh();
_timer.Start();
Closed += (s, e) => _timer.Stop();
```

Método `Refresh()`:
```csharp
private void Refresh()
{
    var rt = Idem.Nt.IdemRuntime.Instance;
    _fleet.Children.Clear();
    if (rt == null || rt.Config == null) return;

    var inst = rt.LastInstrument;
    string instName = inst != null ? inst.FullName : "";
    var inputs = new System.Collections.Generic.List<Idem.Core.FleetInput>();

    var master = rt.Resolve(rt.Config.MasterAccount);
    inputs.Add(new Idem.Core.FleetInput {
        Account = rt.Config.MasterAccount, IsMaster = true,
        Net = inst != null ? rt.Tracker.Net(rt.Config.MasterAccount + "|" + instName) : 0,
        DayPnl = master != null ? rt.DayCache.Get(master.Name) : 0, DailyLossLimit = 0 });

    foreach (var sc in rt.Config.Slaves)
        inputs.Add(new Idem.Core.FleetInput {
            Account = sc.Account, IsMaster = false,
            Net = inst != null ? rt.Tracker.Net(sc.Account + "|" + instName) : 0,
            DayPnl = rt.DayCache.Get(sc.Account), DailyLossLimit = sc.DailyLossLimit });

    foreach (var row in Idem.Core.FleetView.Build(inputs))
        _fleet.Children.Add(FleetRowUi(row));
}

private UIElement FleetRowUi(Idem.Core.FleetRow r)
{
    var pnlBrush = r.DayPnl >= 0 ? new SolidColorBrush(Color.FromRgb(0x22,0xc5,0x5e))
                                 : new SolidColorBrush(Color.FromRgb(0xef,0x44,0x44));
    var text = new TextBlock {
        Foreground = Brushes.White, Margin = new Thickness(0,4,0,4), FontSize = 13,
        Text = (r.IsMaster ? "★ " : "   ") + r.Account
             + "   net " + r.Net
             + "   día " + r.DayPnl.ToString("+0;-0;0")
             + (r.GuardBlocking ? "   ⚠ BLOQUEADO" : "")
    };
    if (r.DayPnl != 0) text.Foreground = pnlBrush;
    return text;
}
```

- [ ] **Step 2: F5 + SIM**

F5. Abrí el dashboard, operá en el master → la flota muestra los nets y el P&L del día en vivo, y marca ⚠ BLOQUEADO en un slave que pasó su tope. Ajustar si algún tipo WPF difiere.

- [ ] **Step 3: Commit** (tras F5 + SIM)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add ui/IdemWindow.cs && git commit -m "feat(ui): flota en vivo en el dashboard (net, P&L del día, guard)"
```

---

### Task 5: Controles — Pausa + Flatten All · F5 + SIM

**Files:**
- Modify: `bin\Custom\Idem\ui\IdemWindow.cs`

- [ ] **Step 1: Botones + feed**

Agregar arriba de la flota una barra con: un texto de estado (Replicación ON/OFF), botón **Pausa/Reanudar** (togglea `IdemRuntime.Instance.Config.Enabled` — CopyEngine ya lo lee cada sweep/fill), y botón **FLATTEN ALL**. Debajo de la flota, un panel con el feed (`RecentFeed()`). Código de los handlers:

```csharp
// Pausa
pauseBtn.Click += (s, e) => {
    var rt = Idem.Nt.IdemRuntime.Instance; if (rt?.Config == null) return;
    rt.Config.Enabled = !rt.Config.Enabled;
    rt.AddFeed(rt.Config.Enabled ? "REANUDADO" : "PAUSADO");
};

// Flatten All: cierra la posición de master + slaves en el instrumento actual.
flattenBtn.Click += (s, e) => {
    var rt = Idem.Nt.IdemRuntime.Instance; if (rt?.Config == null) return;
    var accs = new System.Collections.Generic.List<NinjaTrader.Cbi.Account>();
    var m = rt.Resolve(rt.Config.MasterAccount); if (m != null) accs.Add(m);
    foreach (var sc in rt.Config.Slaves) { var a = rt.Resolve(sc.Account); if (a != null) accs.Add(a); }
    foreach (var a in accs)
    {
        try
        {
            if (rt.LastInstrument != null) a.Flatten(new[] { rt.LastInstrument });
            else a.FlattenEverything();
        }
        catch { }
    }
    rt.AddFeed("FLATTEN ALL");
};
```

En `Refresh()`, actualizar el texto de estado con `rt.Config.Enabled` y volcar `RecentFeed()` al panel del feed.

- [ ] **Step 2: F5 + SIM**

F5. Verificá: **Pausa** frena la réplica sin cerrar posiciones (operá en el master, los slaves no siguen); **Reanudar** vuelve (el sweep reconcilia). **FLATTEN ALL** cierra todo en master + slaves. El feed muestra las réplicas y los eventos. (Confirmar la firma de `Account.Flatten(Instrument[])` / `FlattenEverything()` en tu NT8; referencia: PropCommand `KillSwitch.cs`.)

- [ ] **Step 3: Commit** (tras F5 + SIM)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add ui/IdemWindow.cs && git commit -m "feat(ui): controles Pausa + Flatten All + feed de réplicas"
```

---

### Task 6: Editar config en vivo (master/slaves/topes) · F5 + SIM

**Files:**
- Modify: `bin\Custom\Idem\ui\IdemWindow.cs`
- Modify: `bin\Custom\Idem\Idem.cs` (exponer un `Reconfigure` que re-watchea las cuentas)
- Create: `bin\Custom\Idem\core\IdemConfigWriter.cs` (+ test) — serializa `IdemConfig` de vuelta al formato `.txt`.

**Interfaces:**
- Produces: `Idem.Core.IdemConfigWriter.ToText(IdemConfig cfg) : string` — inverso de `IdemConfig.Parse`. **Puro, testeable** (round-trip Parse→ToText→Parse).

- [ ] **Step 1: `IdemConfigWriter` (puro) — test que falla**

Crear `C:\dev\idem-tests\IdemConfigWriterTests.cs`:

```csharp
using Idem.Core;
using Xunit;

public class IdemConfigWriterTests
{
    [Fact]
    public void RoundTrip_PreservesConfig()
    {
        var c = new IdemConfig { MasterAccount = "Sim101", Enabled = true };
        c.Slaves.Add(new SlaveConfig { Account = "A", DailyLossLimit = 250 });
        c.Slaves.Add(new SlaveConfig { Account = "B", DailyLossLimit = 500 });

        var back = IdemConfig.Parse(IdemConfigWriter.ToText(c));
        Assert.Equal("Sim101", back.MasterAccount);
        Assert.True(back.Enabled);
        Assert.Equal(2, back.Slaves.Count);
        Assert.Equal("A", back.Slaves[0].Account);
        Assert.Equal(250, back.Slaves[0].DailyLossLimit);
        Assert.Equal(500, back.Slaves[1].DailyLossLimit);
    }
}
```
Run: `dotnet test --filter IdemConfigWriterTests` → FAIL.

- [ ] **Step 2: Implementar `IdemConfigWriter`**

Crear `bin\Custom\Idem\core\IdemConfigWriter.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace Idem.Core
{
    public static class IdemConfigWriter
    {
        public static string ToText(IdemConfig cfg)
        {
            var sb = new StringBuilder();
            sb.Append("master=").Append(cfg.MasterAccount ?? "").Append('\n');
            sb.Append("enabled=").Append(cfg.Enabled ? "true" : "false").Append('\n');
            foreach (var s in cfg.Slaves)
                sb.Append("slave=").Append(s.Account).Append(',')
                  .Append(s.DailyLossLimit.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }
    }
}
```
Run: `dotnet test` → PASS. Commit `feat(core): IdemConfigWriter (serializa la config al .txt)`.

- [ ] **Step 3: `Reconfigure` en `Idem.cs`**

Agregar un método público estático o via runtime que: para el master/slaves viejos hace `_fills.Unwatch`/`StopAll` y re-arma con la config nueva, actualiza `IdemRuntime.Instance.Config`, reinicia el `DayPnlPoll` con las nuevas cuentas, y reescribe `idem-config.txt` con `IdemConfigWriter.ToText`. Exponerlo en `IdemRuntime` como `System.Action<IdemConfig> Reconfigure` seteado en Boot.

- [ ] **Step 4: UI de edición**

En `IdemWindow`, un botón **Config** que abre un panel/flyout con: TextBox del master, lista editable de slaves (cuenta + tope), y botón **Guardar** que arma un `IdemConfig` nuevo y llama `IdemRuntime.Instance.Reconfigure(nuevo)`. (Los nombres de cuenta se pueden ofrecer desde `Account.All` en un ComboBox para no tipear mal.)

- [ ] **Step 5: F5 + SIM**

F5. Cambiá un tope desde el panel → Guardar → sin reiniciar, el guard usa el tope nuevo (verificalo replicando). Cambiá un slave → se re-watchea. Confirmá que `idem-config.txt` quedó reescrito.

- [ ] **Step 6: Commit** (tras F5 + SIM)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add ui/IdemWindow.cs Idem.cs core/IdemConfigWriter.cs && git commit -m "feat(ui): editar config en vivo (master/slaves/topes) sin reiniciar"
```

---

## Qué NO cubre este plan

- **Fase 5 — Calendar** local (P&L diario, 5pm ET, persistencia).
- Estilos elaborados: el panel es funcional y oscuro, no pulido. Se puede mejorar después.

## Self-review

- **Cobertura del spec (R6):** flota en vivo → Task 4; feed → Tasks 2+5; controles Flatten/Pausa → Task 5; config UI → Task 6. El menú/ventana → Task 3.
- **Placeholders:** ninguno de lógica. Los estilos WPF son intencionalmente mínimos (declarado).
- **Consistencia de tipos:** `FleetView.Build(List<FleetInput>)` (Task 1) → `IdemWindow.Refresh` (Task 4); `IdemRuntime` (Task 2) leído por la ventana (Tasks 4-6); `IdemConfigWriter.ToText` (Task 6) inverso de `IdemConfig.Parse` (Fase 2), verificado por round-trip.
- **Orden de riesgo:** primero lo testeable (Task 1), después incrementos NT8 verificables de a uno (ventana abre → flota → controles → config en vivo). El más complejo (reconfig en vivo) es el último.
