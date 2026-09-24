# Idem — Plan de implementación: MirrorStop + Drawdown real (Fase 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** (A) Activar el RiskGuard cableando el drawdown real de cada slave, y (B) espejar un stop de protección en cada slave — reconcile-style (idempotente, sin huérfanos), no por eventos.

**Architecture:** Igual que Fase 2: cerebro puro testeable + cáscara NT8 (F5/SIM). El drawdown se lee en el UI-thread (donde `Account.Get` funciona) y se cachea en un `DayPnlCache` puro; el guard lo lee de ahí. El stop se reconcilia en el sweep de 1s: cada slave con posición debe tener un stop al precio del master; si no hay stop del master o el slave está flat, se cancela. La decisión es un núcleo puro (`StopMirror.Decide`); la cáscara lee/coloca/cancela.

**Tech Stack:** C# (core net48/net8.0; nt NinjaTrader), xUnit.

**Spec:** `bin\Custom\Idem\docs\2026-09-24-idem-design.md`
**Depende de:** Fase 1 y 2 (todo `Idem.Core.*` + la cáscara NT8: `CopyEngine`, `FillMonitor`, `OrderSubmit`, `Idem.cs`).

## Global Constraints

- `core\` NT8-free (net48/net8.0); `nt\` NinjaTrader (F5+SIM). Namespaces `Idem.Core` / `Idem.Nt`.
- Test project en `C:\dev\idem-tests\`.
- **Tasks 1–2 ejecutables acá con `dotnet test`. Tasks 3–4 requieren F5 + SIM (las hace el usuario).**
- El stop del master lo pone el usuario (bracket manual). Idem lo espeja; nunca lo mueve.
- Modificar un stop = **cancel + place** (evita la API `Change`, no verificada).
- SIM sólo con cuentas de simulación.

---

### Task 1: `DayPnlCache` (puro) — YA EJECUTADA 2026-09-24

**Files:**
- Create: `bin\Custom\Idem\core\DayPnlCache.cs`
- Test: `C:\dev\idem-tests\DayPnlCacheTests.cs`

**Interfaces:**
- Produces: `Idem.Core.DayPnlCache` con:
  - `void Update(string account, double balance)` — actualiza el HWM (`hwm = max(hwm, balance)`) y guarda el último balance. Lo llama el poll del UI-thread.
  - `double Drawdown(string account)` — `max(0, hwm - last)`; `0` si la cuenta es desconocida. Lo lee el guard (cualquier thread).

- [ ] **Step 1: Escribir el test que falla**

Crear `C:\dev\idem-tests\DayPnlCacheTests.cs`:

```csharp
using Idem.Core;
using Xunit;

public class DayPnlCacheTests
{
    [Fact]
    public void Unknown_IsZero()
    {
        var t = new DayPnlCache();
        Assert.Equal(0, t.Drawdown("A"));
    }

    [Fact]
    public void AtPeak_DrawdownZero()
    {
        var t = new DayPnlCache();
        t.Update("A", 50000);
        Assert.Equal(0, t.Drawdown("A"));
    }

    [Fact]
    public void BelowPeak_DrawdownIsGap()
    {
        var t = new DayPnlCache();
        t.Update("A", 50000);   // pico
        t.Update("A", 49100);   // −900
        Assert.Equal(900, t.Drawdown("A"));
    }

    [Fact]
    public void PeakTrailsUp_ThenMeasuresFromNewPeak()
    {
        var t = new DayPnlCache();
        t.Update("A", 50000);
        t.Update("A", 50800);   // nuevo pico
        t.Update("A", 50300);   // −500 desde 50800
        Assert.Equal(500, t.Drawdown("A"));
    }

    [Fact]
    public void NeverNegative()
    {
        var t = new DayPnlCache();
        t.Update("A", 50000);
        t.Update("A", 51000);
        Assert.Equal(0, t.Drawdown("A"));
    }
}
```

- [ ] **Step 2: Correr para verlo fallar**

Run: `cd /c/dev/idem-tests && dotnet test --filter DayPnlCacheTests`
Expected: FAIL — `DayPnlCache` no existe.

- [ ] **Step 3: Implementar**

Crear `bin\Custom\Idem\core\DayPnlCache.cs`:

```csharp
using System.Collections.Generic;

namespace Idem.Core
{
    // Drawdown por cuenta = cuánto está por debajo de su pico de balance (net-liq).
    // Update lo alimenta el poll del UI-thread (donde Account.Get funciona); Drawdown
    // lo lee el guard desde cualquier thread. Conservador: nunca subestima el DD.
    public sealed class DayPnlCache
    {
        private struct Row { public double Hwm; public double Last; public bool Seen; }
        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        private readonly object _lock = new object();

        public void Update(string account, double balance)
        {
            lock (_lock)
            {
                _rows.TryGetValue(account, out var r);
                if (!r.Seen || balance > r.Hwm) r.Hwm = balance;
                r.Last = balance;
                r.Seen = true;
                _rows[account] = r;
            }
        }

        public double Drawdown(string account)
        {
            lock (_lock)
            {
                if (!_rows.TryGetValue(account, out var r) || !r.Seen) return 0;
                double dd = r.Hwm - r.Last;
                return dd > 0 ? dd : 0;
            }
        }
    }
}
```

- [ ] **Step 4: Correr para verlo pasar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add core/DayPnlCache.cs && git commit -m "feat(core): DayPnlCache (DD por cuenta = HWM - balance actual)"
```

---

### Task 2: `StopMirror.Decide` (puro — el reconciler del stop)

**Files:**
- Create: `bin\Custom\Idem\core\StopMirror.cs`
- Test: `C:\dev\idem-tests\StopMirrorTests.cs`

**Interfaces:**
- Produces:
  - `Idem.Core.StopActionKind` — enum `{ None, Place, Cancel }`.
  - `Idem.Core.StopAction` — struct `{ StopActionKind Kind; double Price; int Qty; }`.
  - `Idem.Core.StopMirror.Decide(bool hasMasterStop, double masterStopPrice, int slaveNet, bool hasSlaveStop, double slaveStopPrice, int slaveStopQty) : StopAction` — reconcilia el stop del slave al del master. Flat o master sin stop → `Cancel` (si el slave tiene) o `None`. Con posición y stop del master: si el slave ya tiene el stop correcto (precio+qty) → `None`; si no → `Place` (el executor cancela el viejo si hay). `Place.Qty = |slaveNet|`.

- [ ] **Step 1: Escribir el test que falla**

Crear `C:\dev\idem-tests\StopMirrorTests.cs`:

```csharp
using Idem.Core;
using Xunit;

public class StopMirrorTests
{
    [Fact]
    public void Position_NoSlaveStop_PlacesAtMasterPrice()
    {
        var a = StopMirror.Decide(true, 20000.0, 2, false, 0, 0);
        Assert.Equal(StopActionKind.Place, a.Kind);
        Assert.Equal(20000.0, a.Price);
        Assert.Equal(2, a.Qty);
    }

    [Fact]
    public void SlaveStopMatches_IsNoOp()
    {
        var a = StopMirror.Decide(true, 20000.0, 2, true, 20000.0, 2);
        Assert.Equal(StopActionKind.None, a.Kind);
    }

    [Fact]
    public void MasterStopMoved_ReplacesAtNewPrice()
    {
        var a = StopMirror.Decide(true, 20010.0, 2, true, 20000.0, 2);
        Assert.Equal(StopActionKind.Place, a.Kind);
        Assert.Equal(20010.0, a.Price);
        Assert.Equal(2, a.Qty);
    }

    [Fact]
    public void SlaveScaled_ReplacesWithNewQty()
    {
        // slave bajó a 1 contrato; el stop debe ir a qty 1.
        var a = StopMirror.Decide(true, 20000.0, 1, true, 20000.0, 2);
        Assert.Equal(StopActionKind.Place, a.Kind);
        Assert.Equal(1, a.Qty);
    }

    [Fact]
    public void Flat_CancelsExistingStop()
    {
        var a = StopMirror.Decide(true, 20000.0, 0, true, 20000.0, 2);
        Assert.Equal(StopActionKind.Cancel, a.Kind);
    }

    [Fact]
    public void MasterHasNoStop_CancelsSlaveStop()
    {
        var a = StopMirror.Decide(false, 0, 2, true, 20000.0, 2);
        Assert.Equal(StopActionKind.Cancel, a.Kind);
    }

    [Fact]
    public void Flat_NoStop_IsNoOp()
    {
        var a = StopMirror.Decide(false, 0, 0, false, 0, 0);
        Assert.Equal(StopActionKind.None, a.Kind);
    }

    [Fact]
    public void ShortPosition_UsesAbsQty()
    {
        var a = StopMirror.Decide(true, 21000.0, -2, false, 0, 0);
        Assert.Equal(StopActionKind.Place, a.Kind);
        Assert.Equal(2, a.Qty);
    }
}
```

- [ ] **Step 2: Correr para verlo fallar**

Run: `cd /c/dev/idem-tests && dotnet test --filter StopMirrorTests`
Expected: FAIL — `StopMirror` no existe.

- [ ] **Step 3: Implementar**

Crear `bin\Custom\Idem\core\StopMirror.cs`:

```csharp
namespace Idem.Core
{
    public enum StopActionKind { None, Place, Cancel }

    public struct StopAction
    {
        public StopActionKind Kind;
        public double Price;
        public int Qty;
    }

    // Reconciler del stop de protección: dado el stop del master y el estado del slave,
    // decide colocar / cancelar / nada. Idempotente y por barrido → sin huérfanos ni OCO.
    // Modificar = Place (el executor cancela el viejo antes de colocar). |slaveNet| = qty.
    public static class StopMirror
    {
        public static StopAction Decide(bool hasMasterStop, double masterStopPrice,
            int slaveNet, bool hasSlaveStop, double slaveStopPrice, int slaveStopQty)
        {
            int absNet = slaveNet < 0 ? -slaveNet : slaveNet;

            if (absNet == 0 || !hasMasterStop)
                return hasSlaveStop
                    ? new StopAction { Kind = StopActionKind.Cancel }
                    : new StopAction { Kind = StopActionKind.None };

            if (hasSlaveStop && slaveStopPrice == masterStopPrice && slaveStopQty == absNet)
                return new StopAction { Kind = StopActionKind.None };

            return new StopAction { Kind = StopActionKind.Place, Price = masterStopPrice, Qty = absNet };
        }
    }
}
```

- [ ] **Step 4: Correr para verlo pasar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add core/StopMirror.cs && git commit -m "feat(core): StopMirror.Decide (reconciler del stop de protección, idempotente)"
```

---

### Task 3: P&L del día real — poll UI-thread + wire del guard · F5 + SIM

**Files:**
- Create: `bin\Custom\Idem\nt\DayPnlPoll.cs`
- Modify: `bin\Custom\Idem\Idem.cs` (wire del callback `drawdown` + arranque del poll)

**Interfaces:**
- Consumes: `Idem.Core.DayPnlCache`.
- Produces: `Idem.Nt.DayPnlPoll` con constructor `(DayPnlCache tracker, System.Collections.Generic.List<NinjaTrader.Cbi.Account> accounts)` y `Start()` / `Stop()`. Corre un `DispatcherTimer` en el UI-thread que lee el net-liq de cada cuenta y llama `tracker.Update`.

> **Sin unit test (código NT8).** Verificación: F5 + SIM.

- [ ] **Step 1: Implementar `DayPnlPoll`**

Crear `bin\Custom\Idem\nt\DayPnlPoll.cs`:

```csharp
using System.Collections.Generic;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using Idem.Core;

namespace Idem.Nt
{
    // Lee el net-liquidation de cada cuenta en el UI-thread (donde Account.Get funciona;
    // off-thread devuelve 0 — el bug de PropCommand) y alimenta el DayPnlCache.
    public sealed class DayPnlPoll
    {
        private readonly DayPnlCache _cache;
        private readonly List<Account> _accounts;
        private DispatcherTimer _timer;

        public DayPnlPoll(DayPnlCache cache, List<Account> accounts)
        {
            _cache = cache;
            _accounts = accounts;
        }

        public void Start()
        {
            var disp = System.Windows.Application.Current != null
                ? System.Windows.Application.Current.Dispatcher : null;
            if (disp == null) return;
            disp.InvokeAsync(() =>
            {
                _timer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(1000) };
                _timer.Tick += (s, e) => Poll();
                _timer.Start();
            });
        }

        public void Stop()
        {
            try { _timer?.Stop(); } catch { }
            _timer = null;
        }

        private void Poll()
        {
            foreach (var acc in _accounts)
            {
                try
                {
                    double realized = acc.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                    double unrealized = acc.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                    _cache.Update(acc.Name, realized + unrealized);
                }
                catch { }
            }
        }
    }
}
```

- [ ] **Step 2: Wire en `Idem.cs`**

En `bin\Custom\Idem\Idem.cs`, agregar el `DayPnlCache` + `DayPnlPoll` y cablear el callback `drawdown`. Reemplazar el bloque del `drawdown` placeholder:

Buscar en `Boot()`:
```csharp
            // Fase 2: DD real todavía no cableado → 0 (el guard no bloquea).
            // Se conecta el DD real en Fase 3/4; el guard ya está probado en aislamiento.
            Func<Account, double> drawdown = acc => 0.0;
```
Reemplazar por:
```csharp
            _dd = new DayPnlCache();
            Func<Account, double> dayPnl = acc => _dd.Get(acc.Name);
```
Agregar el campo (arriba, con los otros): `private DayPnlCache _dd; private DayPnlPoll _ddPoll;`
Después de resolver los slaves y antes de `_engine.Start();`, arrancar el poll con las cuentas slave resueltas:
```csharp
            var slaveAccounts = new System.Collections.Generic.List<Account>();
            foreach (var sc in cfg.Slaves) { var a = resolve(sc.Account); if (a != null) slaveAccounts.Add(a); }
            _ddPoll = new DayPnlPoll(_dd, slaveAccounts);
            _ddPoll.Start();
```
En `State.Terminated`, agregar `try { _ddPoll?.Stop(); } catch { }`.

- [ ] **Step 3: F5**

F5. Expected: compila limpio. (Referencia de `Application.Current.Dispatcher` + `Account.Get(AccountItem.NetLiquidation,...)`: PropCommand.)

- [ ] **Step 4: SIM — el guard ahora bloquea**

1. En `idem-config.txt`, poné un tope bajo en un slave (ej. `slave=SimAccount1,150`).
2. Operá en el master y hacé que SimAccount1 pierda ~$160 en el día (realized+unrealized).
3. Intentá una entrada nueva desde el master → SimAccount1 **no** debe replicar la entrada (perdió más que 150 hoy). El otro slave (límite normal) sí replica.
4. Confirmá que una **salida** en ese slave nunca se bloquea (cerrás en el master → SimAccount1 cierra igual).

- [ ] **Step 5: Commit** (tras F5 + SIM)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add nt/DayPnlPoll.cs Idem.cs && git commit -m "feat(nt): drawdown real (poll UI-thread de net-liq) → guard activo"
```

---

### Task 4: MirrorStop — executor + integración al sweep · F5 + SIM

**Files:**
- Create: `bin\Custom\Idem\nt\StopExecutor.cs`
- Modify: `bin\Custom\Idem\nt\CopyEngine.cs` (hook `onSweepTick`)
- Modify: `bin\Custom\Idem\Idem.cs` (cablear el StopExecutor al hook)

**Interfaces:**
- Consumes: `Idem.Core.PositionTracker`, `Idem.Core.StopMirror`, `Idem.Core.IdemConfig`.
- Produces: `Idem.Nt.StopExecutor` con `ReconcileStops(NinjaTrader.Cbi.Instrument instrument)` — por cada slave: lee el stop del master, lee el Idem-stop del slave, `StopMirror.Decide`, y ejecuta (Place = cancel viejo + CreateOrder StopMarket; Cancel = cancel).

> **Sin unit test (código NT8).** Verificación: F5 + SIM.

- [ ] **Step 1: Agregar el hook de sweep a `CopyEngine`**

En `bin\Custom\Idem\nt\CopyEngine.cs`:
- Agregar campo y parámetro opcional: en el constructor, sumar `System.Action<Instrument> onSweepTick = null` y guardarlo en `private readonly System.Action<Instrument> _onSweepTick;`.
- Al final de `SweepTick()`, después de `Reconcile(masterNet, _lastInstrument);`, agregar:
```csharp
                try { _onSweepTick?.Invoke(_lastInstrument); } catch { }
```

- [ ] **Step 2: Implementar `StopExecutor`**

Crear `bin\Custom\Idem\nt\StopExecutor.cs`:

```csharp
using System.Linq;
using NinjaTrader.Cbi;
using Idem.Core;

namespace Idem.Nt
{
    // Espeja el stop de protección: por barrido, cada slave con posición debe tener UN
    // stop Idem al precio del stop del master. Reconcile-style (idempotente): sin huérfanos.
    // El stop del master lo pone el usuario; Idem sólo lo copia. Modificar = cancel+place.
    public sealed class StopExecutor
    {
        private const string StopTag = "IdemStop";
        private readonly PositionTracker _tracker;
        private readonly IdemConfig _cfg;
        private readonly System.Func<string, Account> _resolve;
        private readonly object _lock = new object();

        public StopExecutor(PositionTracker tracker, IdemConfig cfg, System.Func<string, Account> resolve)
        {
            _tracker = tracker; _cfg = cfg; _resolve = resolve;
        }

        public void ReconcileStops(Instrument instrument)
        {
            lock (_lock)
            {
                var master = _resolve(_cfg.MasterAccount);
                if (master == null) return;

                // Stop del master: primer StopMarket working en este instrumento.
                Order masterStop = WorkingStop(master, instrument, requireTag: false);
                bool hasMasterStop = masterStop != null;
                double masterStopPrice = hasMasterStop ? masterStop.StopPrice : 0;

                foreach (var sc in _cfg.Slaves)
                {
                    var slave = _resolve(sc.Account);
                    if (slave == null) continue;

                    int net = _tracker.Net(sc.Account + "|" + instrument.FullName);
                    Order slaveStop = WorkingStop(slave, instrument, requireTag: true);
                    bool hasSlaveStop = slaveStop != null;
                    double slavePrice = hasSlaveStop ? slaveStop.StopPrice : 0;
                    int slaveQty = hasSlaveStop ? slaveStop.Quantity : 0;

                    var act = StopMirror.Decide(hasMasterStop, masterStopPrice, net,
                        hasSlaveStop, slavePrice, slaveQty);

                    if (act.Kind == StopActionKind.Cancel && slaveStop != null)
                        Cancel(slave, slaveStop);
                    else if (act.Kind == StopActionKind.Place)
                    {
                        if (slaveStop != null) Cancel(slave, slaveStop);
                        Place(slave, instrument, net, act.Price, act.Qty);
                    }
                }
            }
        }

        private static Order WorkingStop(Account acc, Instrument instrument, bool requireTag)
        {
            try
            {
                lock (acc.Orders)
                {
                    return acc.Orders.FirstOrDefault(o =>
                        o.OrderType == OrderType.StopMarket
                        && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted)
                        && o.Instrument == instrument
                        && (!requireTag || o.Name == StopTag));
                }
            }
            catch { return null; }
        }

        private void Place(Account slave, Instrument instrument, int net, double price, int qty)
        {
            double p = instrument.MasterInstrument.RoundToTickSize(price);
            if (p <= 0 || qty <= 0) return;
            // long → stop Sell; short → stop BuyToCover.
            OrderAction action = net > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
            var order = slave.CreateOrder(
                instrument, action, OrderType.StopMarket, OrderEntry.Manual,
                TimeInForce.Day, qty, 0, p, string.Empty, StopTag, System.DateTime.MaxValue, null);
            slave.Submit(new[] { order });
        }

        private void Cancel(Account slave, Order order)
        {
            try { slave.Cancel(new[] { order }); } catch { }
        }
    }
}
```

- [ ] **Step 3: Cablear en `Idem.cs`**

En `Boot()`, después de crear `_engine` (o al construirlo), crear el StopExecutor y pasar el hook. Reemplazar la construcción de `_engine`:
```csharp
            _engine = new CopyEngine(_tracker, cfg, resolve, drawdown);
```
por:
```csharp
            var stopExec = new StopExecutor(_tracker, cfg, resolve);
            _engine = new CopyEngine(_tracker, cfg, resolve, drawdown, inst => stopExec.ReconcileStops(inst));
```

- [ ] **Step 4: F5**

F5. Expected: compila limpio. Ajustar si `OrderState`, `o.Instrument`, `RoundToTickSize` difieren (referencia: PropCommand `OrderReplicator.cs:1044` y `FillMonitor.cs:826`).

- [ ] **Step 5: SIM — el stop se espeja**

1. En el master, entrá 2 contratos y poné un **stop-loss** (bracket) a X.
2. En ~1s, cada slave debe tener un StopMarket "IdemStop" a X, qty 2 (verificar en la pestaña Orders de cada slave).
3. Mové el stop del master a Y → en ~1s los stops de los slaves pasan a Y.
4. Reducí el master a 1 contrato → los stops de los slaves pasan a qty 1.
5. Flatten el master → los stops "IdemStop" de los slaves se cancelan (sin huérfanos).
6. Sacá el stop del master (cancelalo) con posición abierta → los stops de los slaves se cancelan.
7. **Que salte:** dejá que el precio toque el stop en el master → los slaves salen por su propio stop (pre-colocado, sin lag de reacción).

- [ ] **Step 6: Commit** (tras F5 + SIM)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add nt/StopExecutor.cs nt/CopyEngine.cs Idem.cs && git commit -m "feat(nt): MirrorStop (stop de protección espejado, reconcile-style en el sweep)"
```

---

## Qué NO cubre este plan

- **Dashboard WPF** → Fase 4 (incluye reemplazar editar `idem-config.txt` a mano por UI).
- **Calendar** → Fase 5.
- El stop espejado es sólo el **stop de protección** (no el target); el target lo maneja el reconciler (decisión de diseño Enfoque 3).

## Self-review

- **Cobertura del spec:** R3 (guard con DD real) → Tasks 1+3; R4 (stop de protección espejado) → Tasks 2+4. El resto (R5 sweep) ya está de Fase 2 y se reusa como hook.
- **Placeholders:** ninguno. El `drawdown` que devolvía 0 (Fase 2) se reemplaza por el real en Task 3.
- **Consistencia de tipos:** `DayPnlCache.Update/Drawdown(string...)` (Task 1) → poll+guard (Task 3); `StopMirror.Decide(...)` (Task 2) → `StopExecutor` (Task 4); `CopyEngine` gana un `Action<Instrument> onSweepTick` opcional (Task 4 Step 1) usado en Idem.cs (Task 4 Step 3). El constructor de `CopyEngine` con el nuevo parámetro opcional no rompe la llamada existente de Fase 2 (default null) hasta que Task 4 la actualiza.
