# Idem — Plan de implementación: Integración del copy (Fase 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cablear el motor de copy sobre NT8 — el master fillea, cada slave reconcilia a su net vía market — con el cerebro (decisión) puro y testeable, y la mano (NT8) validada en SIM.

**Architecture:** Patrón "núcleo funcional, cáscara imperativa". El **cerebro** (`PositionTracker`, `CopyDecision`) es C# NT8-free en `core\` y se verifica con `dotnet test`. La **cáscara** (`FillMonitor`, `OrderSubmit`, `CopyEngine`, `Idem` entry) vive en `nt\`, usa la API de NinjaTrader, y se valida con F5 (compila) + un escenario en SIM. El cerebro decide *qué* órdenes mandar; la cáscara sólo las ejecuta.

**Tech Stack:** C# (core: vanilla net48/net8.0; nt: NinjaTrader 8 API, net48), xUnit para el core.

**Spec:** `bin\Custom\Idem\docs\2026-09-24-idem-design.md`
**Depende de:** Fase 1 (`Idem.Core.Sizing`, `RiskGuard`, `Reconciler`, `OrderAction`, `ReconcileOrder`) — ya implementada.

## Global Constraints

- `core\` es **NT8-free** (sólo `System.*`); `nt\` usa NinjaTrader y **sólo se verifica con F5 + SIM** (no hay unit test de NT8).
- Namespace: `Idem.Core` (cerebro) / `Idem.Nt` (cáscara).
- Vanilla C# en `core\` (compila net48 y net8.0).
- El proyecto de test sigue en `C:\dev\idem-tests\` (fuera de `bin\Custom`).
- Las salidas nunca se bloquean (regla dura); el reconciler y el guard ya lo garantizan desde Fase 1.
- **Tasks 1–3 son ejecutables e ilustrables acá con `dotnet test`. Tasks 4–6 requieren NinjaTrader abierto (F5 + SIM) y las hace el usuario.**
- Nunca operar cuentas reales para verificar: el SIM usa Sim101 / cuentas de simulación.

---

### Task 1: `PositionTracker` (puro)

**Files:**
- Create: `bin\Custom\Idem\core\PositionTracker.cs`
- Test: `C:\dev\idem-tests\PositionTrackerTests.cs`

**Interfaces:**
- Produces:
  - `Idem.Core.PositionTracker` — clase con estado. Métodos:
    - `void Seed(string key, int net)` — fija el net conocido de un `key` (`"cuenta|instrumento"`).
    - `void ApplyFill(string key, int signedQty)` — aplica un fill: `+N` = compró N, `-N` = vendió N. `net += signedQty`.
    - `int Net(string key)` — net actual (`0` si desconocido).

- [ ] **Step 1: Escribir el test que falla**

Crear `C:\dev\idem-tests\PositionTrackerTests.cs`:

```csharp
using Idem.Core;
using Xunit;

public class PositionTrackerTests
{
    [Fact]
    public void UnknownKey_IsZero()
    {
        var t = new PositionTracker();
        Assert.Equal(0, t.Net("APEX-1|NQ"));
    }

    [Fact]
    public void ApplyFill_AccumulatesSigned()
    {
        var t = new PositionTracker();
        t.ApplyFill("APEX-1|NQ", 2);   // compró 2
        t.ApplyFill("APEX-1|NQ", 1);   // compró 1 más → +3
        t.ApplyFill("APEX-1|NQ", -3);  // vendió 3 → 0
        Assert.Equal(0, t.Net("APEX-1|NQ"));
    }

    [Fact]
    public void Seed_SetsAbsoluteNet_ThenFillsAdjust()
    {
        var t = new PositionTracker();
        t.Seed("APEX-1|NQ", 2);        // arranca en +2 (posición arrastrada)
        t.ApplyFill("APEX-1|NQ", -1);  // vendió 1 → +1
        Assert.Equal(1, t.Net("APEX-1|NQ"));
    }

    [Fact]
    public void KeysAreIndependent()
    {
        var t = new PositionTracker();
        t.ApplyFill("A|NQ", 2);
        t.ApplyFill("B|NQ", -1);
        Assert.Equal(2, t.Net("A|NQ"));
        Assert.Equal(-1, t.Net("B|NQ"));
    }
}
```

- [ ] **Step 2: Correr para verlo fallar**

Run: `cd /c/dev/idem-tests && dotnet test --filter PositionTrackerTests`
Expected: FAIL de compilación — `PositionTracker` no existe.

- [ ] **Step 3: Implementar**

Crear `bin\Custom\Idem\core\PositionTracker.cs`:

```csharp
using System.Collections.Generic;

namespace Idem.Core
{
    // Net real por key ("cuenta|instrumento"), alimentado por TODO fill de forma
    // incondicional (separado del gating de réplica). Fuente de verdad de posiciones.
    // Puro: la cáscara NT8 llama ApplyFill/Seed desde el evento de ejecución.
    public sealed class PositionTracker
    {
        private readonly Dictionary<string, int> _net = new Dictionary<string, int>();
        private readonly object _lock = new object();

        public void Seed(string key, int net)
        {
            lock (_lock) _net[key] = net;
        }

        public void ApplyFill(string key, int signedQty)
        {
            lock (_lock)
            {
                _net.TryGetValue(key, out int cur);
                _net[key] = cur + signedQty;
            }
        }

        public int Net(string key)
        {
            lock (_lock)
            {
                _net.TryGetValue(key, out int cur);
                return cur;
            }
        }
    }
}
```

- [ ] **Step 4: Correr para verlo pasar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: PASS (todos).

- [ ] **Step 5: Commit**

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add core/PositionTracker.cs && git commit -m "feat(core): PositionTracker (net por cuenta|instrumento, feed incondicional)"
```

---

### Task 2: `CopyDecision` (puro — el cerebro de la orquestación)

**Files:**
- Create: `bin\Custom\Idem\core\CopyDecision.cs`
- Test: `C:\dev\idem-tests\CopyDecisionTests.cs`

**Interfaces:**
- Consumes: `Reconciler.Compute`, `Sizing.SlaveTarget`, `RiskGuard.ShouldBlock`, `OrderAction`.
- Produces:
  - `Idem.Core.SlaveState` — struct `{ string Id; int Net; double Drawdown; double DdLimit; }`.
  - `Idem.Core.SlaveDecision` — struct `{ string Id; OrderAction Action; int Qty; bool Blocked; }`.
  - `Idem.Core.CopyDecision.ForMasterNet(int masterNet, System.Collections.Generic.IReadOnlyList<SlaveState> slaves, double cushion, int ratio = 1) : System.Collections.Generic.List<SlaveDecision>` — por cada slave: reconcilia a `masterNet`; si el delta es 0 → decisión `None`; si aumenta exposición y el RiskGuard bloquea → `Blocked=true` (sin orden); si no → la orden del reconciler.

- [ ] **Step 1: Escribir el test que falla**

Crear `C:\dev\idem-tests\CopyDecisionTests.cs`:

```csharp
using System.Collections.Generic;
using Idem.Core;
using Xunit;

public class CopyDecisionTests
{
    private static SlaveState S(string id, int net, double dd, double lim) =>
        new SlaveState { Id = id, Net = net, Drawdown = dd, DdLimit = lim };

    [Fact]
    public void FlatSlaves_FollowMasterEntry()
    {
        var d = CopyDecision.ForMasterNet(2, new List<SlaveState> { S("A", 0, 500, 2500) }, cushion: 400);
        Assert.Single(d);
        Assert.Equal(OrderAction.Buy, d[0].Action);
        Assert.Equal(2, d[0].Qty);
        Assert.False(d[0].Blocked);
    }

    [Fact]
    public void AlreadyAtTarget_IsNoOp()
    {
        var d = CopyDecision.ForMasterNet(2, new List<SlaveState> { S("A", 2, 500, 2500) }, cushion: 400);
        Assert.Equal(OrderAction.None, d[0].Action);
        Assert.Equal(0, d[0].Qty);
        Assert.False(d[0].Blocked);
    }

    [Fact]
    public void EntryBlocked_WhenNearDrawdownLimit()
    {
        // 0 -> 2 (entrada), DD 2100 + 400 >= 2500 → bloquea, sin orden.
        var d = CopyDecision.ForMasterNet(2, new List<SlaveState> { S("A", 0, 2100, 2500) }, cushion: 400);
        Assert.True(d[0].Blocked);
        Assert.Equal(OrderAction.None, d[0].Action);
    }

    [Fact]
    public void ExitNeverBlocked_EvenNearLimit()
    {
        // master flat, slave +2 → salida. Aunque DD esté al límite, cierra.
        var d = CopyDecision.ForMasterNet(0, new List<SlaveState> { S("A", 2, 2400, 2500) }, cushion: 400);
        Assert.False(d[0].Blocked);
        Assert.Equal(OrderAction.Sell, d[0].Action);
        Assert.Equal(2, d[0].Qty);
    }

    [Fact]
    public void MixedFleet_DecidesPerSlave()
    {
        var d = CopyDecision.ForMasterNet(1, new List<SlaveState>
        {
            S("A", 0, 500, 2500),   // entra
            S("B", 1, 500, 2500),   // ya en target
            S("C", 0, 2200, 2500),  // bloqueada
        }, cushion: 400);
        Assert.Equal(OrderAction.Buy, d[0].Action);
        Assert.Equal(OrderAction.None, d[1].Action);
        Assert.True(d[2].Blocked);
    }
}
```

- [ ] **Step 2: Correr para verlo fallar**

Run: `cd /c/dev/idem-tests && dotnet test --filter CopyDecisionTests`
Expected: FAIL de compilación — `CopyDecision`, `SlaveState`, `SlaveDecision` no existen.

- [ ] **Step 3: Implementar**

Crear `bin\Custom\Idem\core\CopyDecision.cs`:

```csharp
using System.Collections.Generic;

namespace Idem.Core
{
    public struct SlaveState
    {
        public string Id;
        public int Net;
        public double Drawdown;
        public double DdLimit;
    }

    public struct SlaveDecision
    {
        public string Id;
        public OrderAction Action;
        public int Qty;
        public bool Blocked;
    }

    // El cerebro de la orquestación: dado el net del master y el estado de cada slave,
    // decide qué orden mandar a cada uno. Compone Reconciler (qué orden) + RiskGuard
    // (si una entrada se permite). Puro y determinista → 100% testeable sin NT8.
    public static class CopyDecision
    {
        public static List<SlaveDecision> ForMasterNet(
            int masterNet, IReadOnlyList<SlaveState> slaves, double cushion, int ratio = 1)
        {
            var result = new List<SlaveDecision>(slaves.Count);
            int target = Sizing.SlaveTarget(masterNet, ratio);

            foreach (var s in slaves)
            {
                var order = Reconciler.Compute(masterNet, s.Net, ratio);

                if (order.Action == OrderAction.None)
                {
                    result.Add(new SlaveDecision { Id = s.Id, Action = OrderAction.None, Qty = 0, Blocked = false });
                    continue;
                }

                bool block = RiskGuard.ShouldBlock(s.Net, target, s.Drawdown, s.DdLimit, cushion);
                if (block)
                    result.Add(new SlaveDecision { Id = s.Id, Action = OrderAction.None, Qty = 0, Blocked = true });
                else
                    result.Add(new SlaveDecision { Id = s.Id, Action = order.Action, Qty = order.Qty, Blocked = false });
            }
            return result;
        }
    }
}
```

- [ ] **Step 4: Correr para verlo pasar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: PASS (todos).

- [ ] **Step 5: Commit**

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add core/CopyDecision.cs && git commit -m "feat(core): CopyDecision (cerebro de orquestación: reconciler + guard por slave)"
```

---

### Task 3: `IdemConfig` (modelo + parse, puro) + carga desde archivo

**Files:**
- Create: `bin\Custom\Idem\core\IdemConfig.cs`
- Test: `C:\dev\idem-tests\IdemConfigTests.cs`

**Interfaces:**
- Produces:
  - `Idem.Core.SlaveConfig` — `{ string Account; double DdLimit; }`.
  - `Idem.Core.IdemConfig` — `{ string MasterAccount; List<SlaveConfig> Slaves; double Cushion; bool Enabled; }`.
  - `Idem.Core.IdemConfig.Parse(string json) : IdemConfig` — parsea el JSON de config (formato abajo). Parse manual mínimo, sin dependencias externas (para compilar en NT8 net48 sin paquetes).

Formato del archivo `bin\Custom\Idem\idem-config.json` (lo edita el usuario a mano en Fase 2; el dashboard lo hará en Fase 4):
```json
{ "master": "Sim101", "cushion": 400, "enabled": true,
  "slaves": [ { "account": "SimAccount1", "ddLimit": 2500 },
              { "account": "SimAccount2", "ddLimit": 2500 } ] }
```

- [ ] **Step 1: Escribir el test que falla**

Crear `C:\dev\idem-tests\IdemConfigTests.cs`:

```csharp
using Idem.Core;
using Xunit;

public class IdemConfigTests
{
    private const string Json =
        "{ \"master\": \"Sim101\", \"cushion\": 400, \"enabled\": true, " +
        "\"slaves\": [ { \"account\": \"SimAccount1\", \"ddLimit\": 2500 }, " +
        "{ \"account\": \"SimAccount2\", \"ddLimit\": 3000 } ] }";

    [Fact]
    public void Parse_ReadsMasterCushionEnabled()
    {
        var c = IdemConfig.Parse(Json);
        Assert.Equal("Sim101", c.MasterAccount);
        Assert.Equal(400, c.Cushion);
        Assert.True(c.Enabled);
    }

    [Fact]
    public void Parse_ReadsSlaves()
    {
        var c = IdemConfig.Parse(Json);
        Assert.Equal(2, c.Slaves.Count);
        Assert.Equal("SimAccount1", c.Slaves[0].Account);
        Assert.Equal(2500, c.Slaves[0].DdLimit);
        Assert.Equal("SimAccount2", c.Slaves[1].Account);
        Assert.Equal(3000, c.Slaves[1].DdLimit);
    }
}
```

- [ ] **Step 2: Correr para verlo fallar**

Run: `cd /c/dev/idem-tests && dotnet test --filter IdemConfigTests`
Expected: FAIL de compilación — `IdemConfig` no existe.

- [ ] **Step 3: Implementar**

Crear `bin\Custom\Idem\core\IdemConfig.cs`. Usa `System.Text.Json` (disponible en net8.0 test; en NT8 net48, si no estuviera, se reemplaza por un parse manual — verificar en F5). Implementación con `System.Text.Json`:

```csharp
using System.Collections.Generic;
using System.Text.Json;

namespace Idem.Core
{
    public struct SlaveConfig
    {
        public string Account;
        public double DdLimit;
    }

    // Config del copy: master, slaves (con su límite de DD), colchón del guard, on/off.
    // Parse sin estado; la cáscara NT8 lee el archivo y llama Parse.
    public sealed class IdemConfig
    {
        public string MasterAccount;
        public List<SlaveConfig> Slaves = new List<SlaveConfig>();
        public double Cushion;
        public bool Enabled;

        public static IdemConfig Parse(string json)
        {
            var cfg = new IdemConfig();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            cfg.MasterAccount = root.GetProperty("master").GetString();
            cfg.Cushion = root.GetProperty("cushion").GetDouble();
            cfg.Enabled = root.GetProperty("enabled").GetBoolean();
            foreach (var s in root.GetProperty("slaves").EnumerateArray())
                cfg.Slaves.Add(new SlaveConfig
                {
                    Account = s.GetProperty("account").GetString(),
                    DdLimit = s.GetProperty("ddLimit").GetDouble()
                });
            return cfg;
        }
    }
}
```

- [ ] **Step 4: Correr para verlo pasar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: PASS (todos).

- [ ] **Step 5: Commit**

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add core/IdemConfig.cs && git commit -m "feat(core): IdemConfig (modelo + parse JSON)"
```

> **NOTA para F5 (Task 6):** si `System.Text.Json` no está disponible en tu NT8 net48, reemplazar `Parse` por un parser manual (el JSON es fijo y simple). Se decide al primer F5.

---

### Task 4: `FillMonitor` (NT8 — suscripción tras `Connected`, feed del tracker) · F5 + SIM

**Files:**
- Create: `bin\Custom\Idem\nt\FillMonitor.cs`

**Interfaces:**
- Consumes: `Idem.Core.PositionTracker`.
- Produces: `Idem.Nt.FillMonitor` con:
  - constructor `FillMonitor(PositionTracker tracker, System.Action<string,int,NinjaTrader.Cbi.Instrument> onMasterFill)` — callback `(masterAccountName, masterNetAfter, instrument)` cuando el master fillea.
  - `void Watch(NinjaTrader.Cbi.Account account, bool isMaster)` — suscribe a `ExecutionUpdate` **sólo si `account.Connection?.Status == ConnectionStatus.Connected`**; si no, engancha `account.ConnectionStatusUpdate` y suscribe cuando pase a `Connected`.
  - `void Unwatch(NinjaTrader.Cbi.Account account)` — desengancha.

> **Sin unit test (código NT8).** Verificación: **F5 limpio** + **SIM** (abajo).

- [ ] **Step 1: Implementar `FillMonitor`**

Crear `bin\Custom\Idem\nt\FillMonitor.cs`. Patrón clave (lo que mató la carrera de PropCommand): suscribir SÓLO tras `Connected`, y re-suscribir en `ConnectionStatusUpdate`.

```csharp
using System;
using NinjaTrader.Cbi;
using Idem.Core;

namespace Idem.Nt
{
    // Suscribe a las ejecuciones y alimenta el PositionTracker con TODO fill.
    // Se suscribe SÓLO cuando la cuenta está Connected, y re-suscribe en
    // ConnectionStatusUpdate — esto mata la carrera de arranque de PropCommand
    // (suscribir antes de conectar dejaba al copy sin recibir fills tras F5).
    public sealed class FillMonitor
    {
        private readonly PositionTracker _tracker;
        private readonly Action<string, int, Instrument> _onMasterFill;
        private readonly System.Collections.Generic.HashSet<string> _masters = new System.Collections.Generic.HashSet<string>();

        public FillMonitor(PositionTracker tracker, Action<string, int, Instrument> onMasterFill)
        {
            _tracker = tracker;
            _onMasterFill = onMasterFill;
        }

        public void Watch(Account account, bool isMaster)
        {
            if (account == null) return;
            if (isMaster) _masters.Add(account.Name);

            if (account.Connection != null && account.Connection.Status == ConnectionStatus.Connected)
                Subscribe(account);
            else
                account.ConnectionStatusUpdate += OnConnectionStatusUpdate;
        }

        public void Unwatch(Account account)
        {
            if (account == null) return;
            try { account.ExecutionUpdate -= OnExecutionUpdate; } catch { }
            try { account.ConnectionStatusUpdate -= OnConnectionStatusUpdate; } catch { }
        }

        private void OnConnectionStatusUpdate(object sender, ConnectionStatusEventArgs e)
        {
            if (e.Status == ConnectionStatus.Connected && sender is Account acc)
                Subscribe(acc);
        }

        private void Subscribe(Account account)
        {
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

                // signo: Buy/BuyToCover suman; Sell/SellShort restan.
                int signed = SignedQty(exec.Order.OrderAction, exec.Quantity);
                _tracker.ApplyFill(key, signed);

                if (_masters.Contains(accName))
                    _onMasterFill(accName, _tracker.Net(key), exec.Instrument);
            }
            catch { /* nunca tirar desde el handler de NT8 */ }
        }

        private static int SignedQty(OrderAction action, int qty)
        {
            switch (action)
            {
                case OrderAction.Buy:
                case OrderAction.BuyToCover: return qty;
                case OrderAction.Sell:
                case OrderAction.SellShort: return -qty;
                default: return 0;
            }
        }
    }
}
```

- [ ] **Step 2: Verificar en NinjaTrader — F5**

En NT8: F5 (Compile). Expected: **compila limpio**. Si tira error de firma (`ExecutionEventArgs`, `ConnectionStatusEventArgs`, `OrderAction`), ajustar al nombre exacto de tu versión de NT8 y volver a F5. (Referencia: PropCommand `FillMonitor.cs` usa `ExecutionEventArgs e` con `e.Execution`.)

- [ ] **Step 3: Verificar en SIM (necesita el CopyEngine de Task 6 para el callback, o un log temporal)**

Este módulo se valida end-to-end en Task 6. Aislado, se puede loguear en `OnExecutionUpdate` y confirmar en SIM que **un fill en el master imprime el net correcto** — incluido el caso crítico: **arrancar el AddOn ANTES de conectar el broker**, conectar, y verificar que igual recibe el primer fill (la carrera de arranque, muerta).

- [ ] **Step 4: Commit** (tras F5 limpio)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add nt/FillMonitor.cs && git commit -m "feat(nt): FillMonitor (suscribe tras Connected + feed del tracker)"
```

---

### Task 5: `OrderSubmit` (NT8 — market a un slave, con lock por slave|instrumento) · F5 + SIM

**Files:**
- Create: `bin\Custom\Idem\nt\OrderSubmit.cs`

**Interfaces:**
- Produces: `Idem.Nt.OrderSubmit` con:
  - `static void Market(NinjaTrader.Cbi.Account slave, NinjaTrader.Cbi.Instrument instrument, Idem.Core.OrderAction action, int qty)` — construye y manda una market, serializada por un **lock por `slaveName|instrumento`** (evita que el evento de fill y el sweep manden órdenes pisadas → mata la reversión cross-thread).

> **Sin unit test (código NT8).** Verificación: F5 + SIM.

- [ ] **Step 1: Implementar `OrderSubmit`**

Crear `bin\Custom\Idem\nt\OrderSubmit.cs`. Patrón `CreateOrder`/`Submit` idéntico al probado en PropCommand (`OrderReplicator.cs:654`).

```csharp
using System.Collections.Generic;
using NinjaTrader.Cbi;
using IdemCore = Idem.Core;

namespace Idem.Nt
{
    // Manda una market a un slave. Serializado por (slave|instrumento): el evento de
    // fill y el safety sweep pueden reconciliar el mismo par a la vez; el lock envuelve
    // el submit para que nunca se pisen (mata la ventana de reversión cross-thread).
    public static class OrderSubmit
    {
        private static readonly Dictionary<string, object> _locks = new Dictionary<string, object>();

        private static object LockFor(string key)
        {
            lock (_locks)
            {
                if (!_locks.TryGetValue(key, out var l)) { l = new object(); _locks[key] = l; }
                return l;
            }
        }

        public static void Market(Account slave, Instrument instrument, IdemCore.OrderAction action, int qty)
        {
            if (slave == null || instrument == null || qty <= 0 || action == IdemCore.OrderAction.None) return;

            OrderAction ntAction = action == IdemCore.OrderAction.Buy ? OrderAction.Buy : OrderAction.Sell;
            string key = slave.Name + "|" + instrument.FullName;

            lock (LockFor(key))
            {
                var order = slave.CreateOrder(
                    instrument, ntAction, OrderType.Market, OrderEntry.Manual,
                    TimeInForce.Day, qty, 0, 0, string.Empty,
                    "Idem", System.DateTime.MaxValue, null);
                slave.Submit(new[] { order });
            }
        }
    }
}
```

- [ ] **Step 2: Verificar en NinjaTrader — F5**

F5. Expected: compila limpio. Si `CreateOrder`/`OrderEntry`/`TimeInForce` difieren, ajustar a tu versión (referencia: PropCommand `OrderReplicator.cs:654`).

- [ ] **Step 3: Nota sobre Buy/Sell vs SellShort**

En SIM (Task 6) verificar el caso **flat → short**: si NT8 rechaza `Sell` desde flat y exige `SellShort`, mapear el signo del target (no la acción del delta) para elegir `SellShort` cuando el net resultante es negativo. Se resuelve midiendo en SIM; anotarlo si aparece.

- [ ] **Step 4: Commit** (tras F5 limpio)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add nt/OrderSubmit.cs && git commit -m "feat(nt): OrderSubmit (market a slave con lock por slave|instrumento)"
```

---

### Task 6: `CopyEngine` + `Idem` (AddOn entry) + safety sweep · F5 + SIM full

**Files:**
- Create: `bin\Custom\Idem\nt\CopyEngine.cs`
- Create: `bin\Custom\Idem\Idem.cs`

**Interfaces:**
- Consumes: `PositionTracker`, `CopyDecision`, `IdemConfig`, `FillMonitor`, `OrderSubmit`.
- Produces:
  - `Idem.Nt.CopyEngine` — orquesta: recibe el callback de master fill del `FillMonitor` → arma `List<SlaveState>` (net del tracker por slave + drawdown/límite) → `CopyDecision.ForMasterNet` → por cada decisión no bloqueada y no-None, `OrderSubmit.Market`. Más un **safety sweep** (`System.Threading.Timer`, ~1000ms) que recontrola.
  - `Idem` (AddOn de NT8, hereda `NinjaTrader.NinjaScript.AddOnBase`) — lee `idem-config.json`, resuelve los `Account` por nombre, arma el `CopyEngine`, arranca el `FillMonitor.Watch` sobre master + slaves. Menú en Control Center para abrir (placeholder; el panel viene en Fase 4).

> **Sin unit test (código NT8).** Verificación: F5 + escenario SIM completo.

- [ ] **Step 1: Implementar `CopyEngine`**

Crear `bin\Custom\Idem\nt\CopyEngine.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using NinjaTrader.Cbi;
using Idem.Core;

namespace Idem.Nt
{
    // Orquestador de la cáscara: master fillea → decide por slave → manda. Más un
    // safety sweep que relee el net real y reconcilia (respaldo por si un fill se perdió).
    public sealed class CopyEngine
    {
        private readonly PositionTracker _tracker;
        private readonly IdemConfig _cfg;
        private readonly Func<string, Account> _resolve;   // nombre → Account
        private readonly Func<Account, double> _drawdown;  // Account → DD actual (UI-thread safe)
        private Timer _sweep;
        private Instrument _lastInstrument;                // el instrumento que opera el master

        public CopyEngine(PositionTracker tracker, IdemConfig cfg,
            Func<string, Account> resolve, Func<Account, double> drawdown)
        {
            _tracker = tracker; _cfg = cfg; _resolve = resolve; _drawdown = drawdown;
        }

        public void OnMasterFill(string masterName, int masterNet, Instrument instrument)
        {
            _lastInstrument = instrument;
            Reconcile(masterNet, instrument);
        }

        public void Start()
        {
            _sweep = new Timer(_ => SweepTick(), null, 1000, 1000);
        }

        public void Stop()
        {
            try { _sweep?.Dispose(); } catch { }
            _sweep = null;
        }

        private void SweepTick()
        {
            try
            {
                if (!_cfg.Enabled || _lastInstrument == null) return;
                var master = _resolve(_cfg.MasterAccount);
                if (master == null) return;
                int masterNet = _tracker.Net(master.Name + "|" + _lastInstrument.FullName);
                Reconcile(masterNet, _lastInstrument);
            }
            catch { }
        }

        private void Reconcile(int masterNet, Instrument instrument)
        {
            if (!_cfg.Enabled) return;

            var states = new List<SlaveState>(_cfg.Slaves.Count);
            var byId = new Dictionary<string, Account>();
            foreach (var sc in _cfg.Slaves)
            {
                var acc = _resolve(sc.Account);
                if (acc == null) continue;
                byId[sc.Account] = acc;
                states.Add(new SlaveState
                {
                    Id = sc.Account,
                    Net = _tracker.Net(sc.Account + "|" + instrument.FullName),
                    Drawdown = _drawdown(acc),
                    DdLimit = sc.DdLimit
                });
            }

            foreach (var d in CopyDecision.ForMasterNet(masterNet, states, _cfg.Cushion))
            {
                if (d.Blocked || d.Action == OrderAction.None) continue;
                if (byId.TryGetValue(d.Id, out var acc))
                    OrderSubmit.Market(acc, instrument, d.Action, d.Qty);
            }
        }
    }
}
```

- [ ] **Step 2: Implementar el AddOn `Idem`**

Crear `bin\Custom\Idem\Idem.cs`. Lee la config, resuelve cuentas, arma todo. (El menú/panel del Control Center llega en Fase 4; acá alcanza con arrancar el motor.)

```csharp
using System;
using System.IO;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using Idem.Core;
using Idem.Nt;

namespace NinjaTrader.NinjaScript.AddOns
{
    public class Idem : AddOnBase
    {
        private PositionTracker _tracker;
        private FillMonitor _fills;
        private CopyEngine _engine;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Idem";
            }
            else if (State == State.Configure)
            {
                try { Boot(); } catch (Exception ex) { NinjaTrader.Code.Output.Process("Idem boot error: " + ex.Message, PrintTo.OutputTab1); }
            }
            else if (State == State.Terminated)
            {
                _engine?.Stop();
            }
        }

        private void Boot()
        {
            string path = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "bin", "Custom", "Idem", "idem-config.json");
            if (!File.Exists(path)) { NinjaTrader.Code.Output.Process("Idem: no idem-config.json", PrintTo.OutputTab1); return; }
            var cfg = IdemConfig.Parse(File.ReadAllText(path));

            Func<string, Account> resolve = name => Account.All.FirstOrDefault(a => a.Name == name);
            Func<Account, double> drawdown = acc =>
            {
                // DD actual = HWM del día − balance actual, o el cálculo que uses. Placeholder simple:
                // se refina en la fase de riesgo. Aquí: 0 (no bloquea) hasta cablear el DD real.
                return 0.0;
            };

            _tracker = new PositionTracker();
            _engine = new CopyEngine(_tracker, cfg, resolve, drawdown);
            _fills = new FillMonitor(_tracker, (m, net, inst) => _engine.OnMasterFill(m, net, inst));

            var master = resolve(cfg.MasterAccount);
            if (master != null) _fills.Watch(master, true);
            foreach (var sc in cfg.Slaves)
            {
                var acc = resolve(sc.Account);
                if (acc != null) _fills.Watch(acc, false);
            }
            _engine.Start();
            NinjaTrader.Code.Output.Process("Idem: motor arrancado (master " + cfg.MasterAccount + ")", PrintTo.OutputTab1);
        }
    }
}
```

- [ ] **Step 3: Verificar en NinjaTrader — F5**

F5. Expected: compila limpio. Ajustar firmas de NT8 si hace falta (`AddOnBase`, `Account.All`, `Globals.UserDataDir`). Referencia: cualquier AddOn de PropCommand.

- [ ] **Step 4: Verificar en SIM — escenario completo**

1. Crear `bin\Custom\Idem\idem-config.json` con master = Sim101 y 2 slaves de simulación.
2. Reiniciar NT8 (o re-F5), conectar el broker de simulación.
3. **Carrera de arranque:** confirmar en Output "Idem: motor arrancado".
4. **Entrada:** comprar 1 contrato en Sim101 → los 2 slaves compran 1 (verificar en sus posiciones).
5. **Agregar:** comprar 1 más en el master → slaves a +2.
6. **Salida parcial:** vender 1 en el master → slaves a +1.
7. **Cierre:** flatten en el master → slaves a 0.
8. **Sweep:** con el master en +1, cerrar un slave a mano → en ~1s el sweep lo vuelve a +1.
9. Cero reversiones, cero posiciones trabadas, cero huérfanos.

- [ ] **Step 5: Commit** (tras F5 limpio + SIM ok)

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add nt/CopyEngine.cs Idem.cs && git commit -m "feat(nt): CopyEngine + AddOn Idem + safety sweep (copy end-to-end en SIM)"
```

---

## Qué NO cubre este plan

- **MirrorStop** (stop de protección espejado) → Fase 3.
- **Dashboard WPF** (flota, feed, controles, config UI) → Fase 4. En Fase 2 la config se edita a mano en `idem-config.json`.
- **DD real** en el `drawdown` callback → se cablea en Fase 3/4 (por ahora devuelve 0 = el guard no bloquea; el guard se valida con sus unit tests de Fase 1).
- **Calendar** → Fase 5.

## Self-review

- **Cobertura del spec:** R1 (dispatch master→slaves) → Task 6; R5 (evento + sweep) → Task 6; R8 (núcleos testeables) → Tasks 1-3; el mapa de bugs (carrera de suscripción → Task 4; reversión cross-thread → Task 5 lock; contar fills → Tasks 1-2 reconciler) queda cubierto.
- **Placeholders:** el `drawdown` callback devuelve 0 a propósito en Fase 2 (declarado arriba); no es un placeholder olvidado sino un límite de fase explícito (el guard ya está testeado en aislamiento).
- **Consistencia de tipos:** `PositionTracker.ApplyFill(string,int)` (Task 1) usado por `FillMonitor` (Task 4); `CopyDecision.ForMasterNet` (Task 2) usado por `CopyEngine` (Task 6); `OrderSubmit.Market(Account,Instrument,OrderAction,int)` (Task 5) usado por `CopyEngine` (Task 6); `IdemConfig.Parse` (Task 3) usado por el AddOn (Task 6). Todas las firmas coinciden.
