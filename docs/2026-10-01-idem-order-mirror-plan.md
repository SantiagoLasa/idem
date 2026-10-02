# Idem — Fase 6: Copy a nivel de orden (plan)

Spec: `2026-10-01-idem-order-mirror-spec.md`. Objetivo: replicar la **orden** del master
(al mandarla, no al llenarse) → instantáneo e igual en todas las cuentas. El reconciler/sweep
queda como red de seguridad; el guard sigue aplicando.

## Task 1 — Núcleo puro (TDD, `dotnet test`)
Todo NT8-free, en `core/`, testeado en `C:\dev\idem-tests`.

1. **`core/OrderMirror.cs`** (lógica pura; nombre `OrderMirrorDecision` para no chocar con la
   clase NT8 `OrderMirror`):
   - `enum MirrorSide { Buy, Sell }` (la cáscara mapea NtOrderAction Buy/BuyToCover→Buy,
     Sell/SellShort→Sell).
   - `bool IsEntryOrder(MirrorSide side, int slaveNet)` — entrada = aumenta exposición en su
     dirección: Buy con net ≥ 0 → entrada; Buy con net < 0 → salida (cover). Sell simétrico.
   - `struct MirrorDecision { bool Place; int Qty; }`
   - `MirrorDecision Decide(MirrorSide side, int masterQty, int slaveNet, double slaveDayPnl, double slaveDailyLossLimit, double ratio)`
     — bloqueado = `slaveDayPnl <= -slaveDailyLossLimit`; si es entrada y está bloqueado → `Place=false`;
     si no → `Place=true, Qty = (int)Math.Round(masterQty*ratio)`.
   - `string SlaveOco(string masterOco, string slaveName)` — vacío/null → ""; si no → `masterOco + "|" + slaveName`.

   **RED:** IsEntryOrder (Buy flat/long/short, Sell flat/short/long); Decide (entrada+bloqueado→no;
   entrada+ok→sí con qty; salida+bloqueado→sí; ratio escala la qty); SlaveOco (vacío→vacío,
   determinístico, distinto por slave).

**Gate:** `cd /c/dev/idem-tests && dotnet test` verde (rojo primero).

## Task 2 — Cáscara NT8 (F5)
1. **`nt/OrderSubmit.cs`** — agregar `Submit(Account slave, Instrument, OrderType, NtOrderAction, int qty, double limitPrice, double stopPrice, string oco)` genérico (además del `Market` que usa el sweep), con el mismo lock por `slave|instrumento`. Devuelve el `Order` creado.
2. **`nt/OrderMirror.cs`** (clase NT8) — suscribe a `master.OrderUpdate` (poll de Connected como el FillMonitor). Mantiene `masterOrderId → {slaveName → Order}` y el último precio visto por orden.
   - **Nueva orden (Working/Accepted, no mapeada):** por slave, mapear NtOrderAction→MirrorSide, leer el neto real del slave, `OrderMirrorDecision.Decide(...)`; si Place, crear la orden equivalente (mismo `OrderType`/`LimitPrice`/`StopPrice`, `oco = SlaveOco(master.Oco, slave)`), submit, guardar mapeo.
   - **Cambio de precio/qty (ChangeSubmitted):** `Account.Change` las espejo vivas.
   - **Cancelled/Rejected:** cancelar las espejo vivas; limpiar mapeo.
   - **Filled:** limpiar mapeo (la espejo llena/cancela sola por OCO).
   - Setear `IdemRuntime.LastInstrument` y feed (`AddFeed`) en cada acción.
3. **`Idem.cs`** — en `WireWatches`: instanciar `OrderMirror`, `Watch(master)`. **Neutralizar** el market-reconcile fill-driven: `CopyEngine.OnMasterFill` sólo setea instrumento/feed (sin submit). **Retirar** `StopExecutor`/`StopMirror` del wiring (el stop ahora lo espeja el order-mirror). El `SweepTick` (1s) se mantiene como red de seguridad.

**Gate:** F5 compila limpio.

## Task 3 — Validación en SIM (F5 + usuario)
Probar y confirmar, uno por uno:
- Market del master → 5 cuentas casi al mismo precio (gap mucho menor que antes).
- Límite/TP → slaves al mismo precio del límite.
- Bracket (TP+SL) → OCO espejado; al llenar uno, el otro se cancela.
- Modificar/cancelar en el master → se replica.
- Guard: slave pasado de tope no toma entradas nuevas; el sweep corrige residual.
- Comparar P&L master vs slaves en una sesión → spread chico (sólo latencia residual).

**Gate:** confirmación del usuario en SIM; si queda divergencia, ajustar.

## Riesgos / mitigaciones
- **Fill parcial / orden que no llena en un slave:** el sweep (1s) reconcilia el neto real → se
  corrige en ≤1s (a mercado, slippage sólo en lo faltante).
- **OCO/brackets:** se replica el `Oco` por slave; NT8 linkea las patas. Doble-seguro con el
  cancel del master replicado.
- **Doble acción (order-mirror + fill reconcile):** se neutraliza `OnMasterFill`; sólo queda el
  order-mirror + sweep.
- **Órdenes pre-existentes** al arrancar Idem: no se espejan (no-goal); nota para el usuario.
