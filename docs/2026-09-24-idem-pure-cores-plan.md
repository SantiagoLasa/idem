# Idem — Plan de implementación: Núcleos puros (Fase 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Construir y testear los núcleos puros del motor de copy de Idem — `Sizing`, `RiskGuard` y `Reconciler` — como C# NT8-free, verificados de punta a punta con `dotnet test` sin abrir NinjaTrader.

**Architecture:** Los núcleos con lógica viven en `bin\Custom\Idem\core\` como C# vanilla (sin tipos de NinjaTrader), namespace `Idem.Core`. Un proyecto de test xUnit vive FUERA de `bin\Custom` (en `C:\dev\idem-tests\`) y compila esos mismos `.cs` vía `<Compile Include>`. Así NT8 compila los núcleos bajo net48 con F5, y `dotnet test` los verifica bajo net8.0 — mismos archivos fuente, cero duplicación.

**Tech Stack:** C# (vanilla, compatible net48/net8.0), xUnit, `dotnet test`.

**Spec:** `bin\Custom\Idem\docs\2026-09-24-idem-design.md`

## Global Constraints

- Los núcleos en `core\` son **NT8-free**: cero `using NinjaTrader.*`, cero tipos de NinjaTrader. Sólo `System.*` básico.
- Sólo C# vanilla que compile bajo **net48 (NT8) y net8.0 (test)**: nada específico de una versión de framework.
- Namespace de los núcleos: **`Idem.Core`**.
- El proyecto de test vive **FUERA de `bin\Custom`** (NT8 compila todo lo que hay bajo `bin\Custom`; xUnit ahí rompería el F5).
- TDD estricto: test rojo primero, verlo fallar, implementación mínima, verde, commit. Commits frecuentes.
- Las **salidas nunca se bloquean**: cualquier orden que reduce exposición pasa siempre (regla dura aprendida de PropCommand).

---

### Task 1: Setup del proyecto de test + `Sizing`

**Files:**
- Create: `C:\dev\idem-tests\Idem.Tests.csproj`
- Create: `bin\Custom\Idem\core\Sizing.cs`
- Test: `C:\dev\idem-tests\SizingTests.cs`

**Interfaces:**
- Produces: `Idem.Core.Sizing.SlaveTarget(int masterNet, int ratio = 1) : int` — el target de posición del slave para un net dado del master. 1:1 por defecto (`ratio=1`).

- [ ] **Step 1: Crear el proyecto de test**

Crear `C:\dev\idem-tests\Idem.Tests.csproj` con exactamente este contenido (el `<Compile Include>` apunta a los núcleos en `bin\Custom\Idem\core`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="C:\Users\santi\OneDrive\Documentos\NinjaTrader 8\bin\Custom\Idem\core\**\*.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Escribir el test que falla**

Crear `C:\dev\idem-tests\SizingTests.cs`:

```csharp
using Idem.Core;
using Xunit;

public class SizingTests
{
    [Fact]
    public void OneToOne_ReturnsMasterNet()
    {
        Assert.Equal(3, Sizing.SlaveTarget(3));
        Assert.Equal(-2, Sizing.SlaveTarget(-2));
        Assert.Equal(0, Sizing.SlaveTarget(0));
    }

    [Fact]
    public void Ratio_ScalesTarget()
    {
        Assert.Equal(6, Sizing.SlaveTarget(3, 2));
        Assert.Equal(-4, Sizing.SlaveTarget(-2, 2));
    }
}
```

- [ ] **Step 3: Correr el test para verlo fallar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: FAIL de compilación — `Sizing` no existe todavía.

- [ ] **Step 4: Implementar el mínimo**

Crear `bin\Custom\Idem\core\Sizing.cs`:

```csharp
namespace Idem.Core
{
    // Tamaño del slave respecto del master. 1:1 por defecto; el ratio queda
    // aislado por si algún día cambia, pero el resto del sistema asume 1:1.
    public static class Sizing
    {
        public static int SlaveTarget(int masterNet, int ratio = 1) => masterNet * ratio;
    }
}
```

- [ ] **Step 5: Correr el test para verlo pasar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add core/Sizing.cs && git commit -m "feat(core): Sizing 1:1 (slave target = master net * ratio)"
```

(El proyecto de test en `C:\dev\idem-tests` es un harness separado; versionarlo es opcional y no es parte del repo de Idem.)

---

### Task 2: `RiskGuard`

**Files:**
- Create: `bin\Custom\Idem\core\RiskGuard.cs`
- Test: `C:\dev\idem-tests\RiskGuardTests.cs`

**Interfaces:**
- Consumes: nada.
- Produces: `Idem.Core.RiskGuard.ShouldBlock(int slaveNetBefore, int slaveNetAfter, double currentDrawdown, double ddLimit, double cushion) : bool` — `true` = bloquear la orden. Bloquea SÓLO órdenes que aumentan exposición absoluta (entradas) cuando `currentDrawdown + cushion >= ddLimit`. Reducciones/salidas nunca se bloquean.

- [ ] **Step 1: Escribir el test que falla**

Crear `C:\dev\idem-tests\RiskGuardTests.cs`:

```csharp
using Idem.Core;
using Xunit;

public class RiskGuardTests
{
    // Entrada (aumenta exposición) con DD cerca del límite → bloquea.
    [Fact]
    public void BlocksEntry_WhenWithinCushionOfLimit()
    {
        // 0 -> 2 contratos (entrada). DD 2100 + colchón 400 = 2500 >= 2500 → bloquea.
        Assert.True(RiskGuard.ShouldBlock(0, 2, 2100, 2500, 400));
    }

    [Fact]
    public void AllowsEntry_WhenFarFromLimit()
    {
        // 0 -> 2 (entrada). DD 600 + 400 = 1000 < 2500 → permite.
        Assert.False(RiskGuard.ShouldBlock(0, 2, 600, 2500, 400));
    }

    // Salidas / reducciones NUNCA se bloquean, aunque el DD esté al límite.
    [Fact]
    public void NeverBlocksExit_EvenAtLimit()
    {
        // 2 -> 0 (cierre total). Aunque DD+colchón supere el límite, pasa.
        Assert.False(RiskGuard.ShouldBlock(2, 0, 2400, 2500, 400));
        // 3 -> 1 (reducción parcial).
        Assert.False(RiskGuard.ShouldBlock(3, 1, 2400, 2500, 400));
    }

    // Un flip (reversa) que aumenta exposición del otro lado cuenta como entrada.
    [Fact]
    public void TreatsReversalIncreasingExposureAsEntry()
    {
        // 1 -> -2: |−2| > |1| → aumenta exposición → sujeto al guard.
        Assert.True(RiskGuard.ShouldBlock(1, -2, 2200, 2500, 400));
    }
}
```

- [ ] **Step 2: Correr el test para verlo fallar**

Run: `cd /c/dev/idem-tests && dotnet test --filter RiskGuardTests`
Expected: FAIL de compilación — `RiskGuard` no existe.

- [ ] **Step 3: Implementar el mínimo**

Crear `bin\Custom\Idem\core\RiskGuard.cs`:

```csharp
using System;

namespace Idem.Core
{
    // Guard de drawdown liviano: una resta y una comparación. Bloquea SÓLO órdenes
    // que aumentan la exposición absoluta (entradas) cuando la cuenta está a menos
    // de `cushion` dólares de su límite de DD. Las reducciones/salidas SIEMPRE pasan:
    // siempre tenés que poder cerrar. Sin heurístico de stop por instrumento.
    public static class RiskGuard
    {
        public static bool ShouldBlock(int slaveNetBefore, int slaveNetAfter,
            double currentDrawdown, double ddLimit, double cushion)
        {
            bool increasesExposure = Math.Abs(slaveNetAfter) > Math.Abs(slaveNetBefore);
            if (!increasesExposure) return false; // salidas/reducciones nunca se bloquean
            return currentDrawdown + cushion >= ddLimit;
        }
    }
}
```

- [ ] **Step 4: Correr el test para verlo pasar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: PASS (todos, incluidos los de Sizing).

- [ ] **Step 5: Commit**

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add core/RiskGuard.cs && git commit -m "feat(core): RiskGuard liviano (bloquea entradas cerca del DD, nunca salidas)"
```

---

### Task 3: `Reconciler` (+ tipos compartidos)

**Files:**
- Create: `bin\Custom\Idem\core\Types.cs`
- Create: `bin\Custom\Idem\core\Reconciler.cs`
- Test: `C:\dev\idem-tests\ReconcilerTests.cs`

**Interfaces:**
- Consumes: `Idem.Core.Sizing.SlaveTarget`.
- Produces:
  - `Idem.Core.OrderAction` — enum `{ None, Buy, Sell }`.
  - `Idem.Core.ReconcileOrder` — struct `{ OrderAction Action; int Qty; }`.
  - `Idem.Core.Reconciler.Compute(int masterNet, int slaveNet, int ratio = 1) : ReconcileOrder` — `target = Sizing.SlaveTarget(masterNet, ratio)`; `delta = target - slaveNet`; `delta==0` → `None/0`; `>0` → `Buy/delta`; `<0` → `Sell/|delta|`.

- [ ] **Step 1: Escribir el test que falla**

Crear `C:\dev\idem-tests\ReconcilerTests.cs`:

```csharp
using Idem.Core;
using Xunit;

public class ReconcilerTests
{
    [Fact]
    public void NoDelta_ReturnsNone()
    {
        var o = Reconciler.Compute(masterNet: 2, slaveNet: 2);
        Assert.Equal(OrderAction.None, o.Action);
        Assert.Equal(0, o.Qty);
    }

    [Fact]
    public void MasterAheadLong_BuysTheDelta()
    {
        // master +4, slave +2 → target 4, delta +2 → Buy 2.
        var o = Reconciler.Compute(4, 2);
        Assert.Equal(OrderAction.Buy, o.Action);
        Assert.Equal(2, o.Qty);
    }

    [Fact]
    public void MasterFlat_ClosesLongSlave()
    {
        // master 0, slave +2 → delta -2 → Sell 2 (cierra el long).
        var o = Reconciler.Compute(0, 2);
        Assert.Equal(OrderAction.Sell, o.Action);
        Assert.Equal(2, o.Qty);
    }

    [Fact]
    public void MasterFlat_CoversShortSlave()
    {
        // master 0, slave -2 → delta +2 → Buy 2 (cubre el short).
        var o = Reconciler.Compute(0, -2);
        Assert.Equal(OrderAction.Buy, o.Action);
        Assert.Equal(2, o.Qty);
    }

    [Fact]
    public void Reversal_LongToShort_SellsCombinedDelta()
    {
        // master -1, slave +2 → target -1, delta -3 → Sell 3 (revierte).
        var o = Reconciler.Compute(-1, 2);
        Assert.Equal(OrderAction.Sell, o.Action);
        Assert.Equal(3, o.Qty);
    }

    [Fact]
    public void Ratio_ScalesTarget()
    {
        // master +2, ratio 2 → target 4, slave 0 → Buy 4.
        var o = Reconciler.Compute(2, 0, ratio: 2);
        Assert.Equal(OrderAction.Buy, o.Action);
        Assert.Equal(4, o.Qty);
    }
}
```

- [ ] **Step 2: Correr el test para verlo fallar**

Run: `cd /c/dev/idem-tests && dotnet test --filter ReconcilerTests`
Expected: FAIL de compilación — `Reconciler`, `OrderAction`, `ReconcileOrder` no existen.

- [ ] **Step 3: Implementar los tipos**

Crear `bin\Custom\Idem\core\Types.cs`:

```csharp
namespace Idem.Core
{
    public enum OrderAction { None, Buy, Sell }

    public struct ReconcileOrder
    {
        public OrderAction Action;
        public int Qty;
    }
}
```

- [ ] **Step 4: Implementar el Reconciler**

Crear `bin\Custom\Idem\core\Reconciler.cs`:

```csharp
namespace Idem.Core
{
    // Corazón del copy: reconciliar el slave a un target conocido, no contar fills.
    // target = tamaño del master (1:1); delta = target - net_real_del_slave.
    // Idempotente: delta 0 → ninguna orden. Entrada y salida son el mismo cálculo:
    // cerrar es reconciliar a un target más chico. El signo del delta decide el lado.
    public static class Reconciler
    {
        public static ReconcileOrder Compute(int masterNet, int slaveNet, int ratio = 1)
        {
            int target = Sizing.SlaveTarget(masterNet, ratio);
            int delta = target - slaveNet;
            if (delta == 0) return new ReconcileOrder { Action = OrderAction.None, Qty = 0 };
            if (delta > 0) return new ReconcileOrder { Action = OrderAction.Buy, Qty = delta };
            return new ReconcileOrder { Action = OrderAction.Sell, Qty = -delta };
        }
    }
}
```

- [ ] **Step 5: Correr los tests para verlos pasar**

Run: `cd /c/dev/idem-tests && dotnet test`
Expected: PASS (todos — Sizing, RiskGuard, Reconciler).

- [ ] **Step 6: Commit**

```bash
cd "/c/Users/santi/OneDrive/Documentos/NinjaTrader 8/bin/Custom/Idem" && git add core/Types.cs core/Reconciler.cs && git commit -m "feat(core): Reconciler-a-target (delta con signo → Buy/Sell/None) + tipos"
```

---

## Qué NO cubre este plan (fases siguientes, plan propio cada una)

- **Integración NT8 del copy** (FillMonitor suscrito tras `Connected`, CopyEngine, submit de órdenes, lock por `slave|instrumento`, safety sweep) — necesita F5/SIM.
- **MirrorStop** (stop de protección espejado) — necesita F5/SIM.
- **Dashboard WPF** (flota, feed, controles).
- **Calendar** (TradingDay 5pm ET + agregación + persistencia local + vista).

Cada una arranca por su propio núcleo puro testeado (si aplica) y después la capa NT8 validada en SIM.

## Self-review

- **Cobertura del spec (para esta fase):** R2 (sizing 1:1) → Task 1; R3 (RiskGuard liviano, salidas nunca bloqueadas) → Task 2; R4 (reconciler-a-target) → Task 3; R8 (núcleos NT8-free testeables sin NT8) → los tres, vía el proyecto de test fuera de `bin\Custom`. Los requisitos de integración (R1 dispatch, R5 evento+sweep, R6 dashboard, R7 calendario) son fases siguientes, declaradas arriba.
- **Placeholders:** ninguno — cada step trae el código real.
- **Consistencia de tipos:** `Sizing.SlaveTarget` (Task 1) se consume en `Reconciler.Compute` (Task 3) con la misma firma; `OrderAction`/`ReconcileOrder` definidos en Task 3 antes de usarse.
