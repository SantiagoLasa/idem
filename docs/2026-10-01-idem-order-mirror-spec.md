# Idem — Fase 6: Copy a nivel de ORDEN (spec)

## Necesidad (observada, no supuesta)
Operando SIM con 5 cuentas APEX, los slaves quedan **sistemáticamente ~$220 peor** que el
master en el día (clúster apretado entre slaves, lejos del master). Causa raíz confirmada en
`FillMonitor.cs:85-99`: Idem copia **a nivel de fill** — espera a que la orden del master se
LLENE (`ExecutionUpdate`), y recién ahí manda **market** a los slaves. Por eso el slave va un
ciclo de fill atrás y **cruza el spread en cada trade** (aunque el master use límite). En
scalping de NQ ese peaje de 1-2 ticks/pata domina el P&L.

## Objetivo
Replicar **instantáneo e igual en todas las cuentas**: cuando el master **manda** una orden
(no cuando se llena), Idem manda la **misma orden** (tipo, precio, OCO) a cada slave. Market →
todas al mismo instante; límite/TP → todas al mismo precio; bracket → OCO espejado.

## Diseño
- **Driver nuevo:** suscribir a `Account.OrderUpdate` del master (no `ExecutionUpdate`).
- **Al ver una orden del master Working/Accepted por primera vez:** crear y mandar en cada slave
  una orden equivalente (mismo `OrderType`, `OrderAction`, `Quantity`×ratio(=1), `LimitPrice`,
  `StopPrice`), mapeando `masterOrderId → {slave → slaveOrder}`.
- **OCO (brackets TP+SL):** si la orden del master trae `Oco`, generar un OCO por slave
  determinístico (`masterOco|slaveName`) → las patas del slave quedan linkeadas (cuando llena una,
  NT8 cancela la otra sola).
- **Cambio de precio/cantidad:** `Account.Change` sobre las órdenes espejo.
- **Cancel/Reject del master:** cancelar las espejo que sigan vivas.
- **Guard:** no espejar una orden de **entrada** (que aumenta exposición) a un slave pasado de su
  tope diario; las de **salida/reducción** siempre se espejan. `IsEntryOrder(action, slaveNet)`.
- **Red de seguridad (se mantiene):** el `SweepTick` (1s) sigue leyendo el **neto real del broker**
  y reconciliando el residual a mercado (vía `CopyDecision`, que ya respeta el guard). Si una orden
  espejo se rechaza o un fill se desincroniza, el sweep lo corrige en ≤1s.
- **Se desactiva** el path viejo fill-driven (`OnMasterFill` → market reconcile) para no actuar
  doble. `StopExecutor`/`StopMirror` se retiran: el stop ahora es una orden más que el mirror espeja.

## Núcleo puro (testeable, `dotnet test`)
- `IsEntryOrder(OrderAction, slaveNet) : bool` — entrada = aumenta |exposición| en su dirección.
- `OrderMirrorDecision.Decide(masterOrder, slaveState) : MirrorAction` — Place / Skip(blocked).
- `SlaveOco(masterOco, slaveName) : string` — mapeo determinístico, vacío→vacío.
- Reusar `Sizing` (ratio 1:1) para la cantidad.

## Criterios de aceptación
- AC1: `IsEntryOrder` — Buy desde flat o long = entrada; Buy en short = salida (cover). Sell simétrico.
- AC2: `Decide` saltea una orden de **entrada** si el slave está bloqueado (dayPnl ≤ −tope); una de
  **salida** nunca se saltea.
- AC3: `Decide` manda Place con la cantidad del master × ratio y el precio del master.
- AC4: `SlaveOco` mapea un OCO no vacío a `masterOco|slave` determinístico; OCO vacío → vacío.
- AC5 (SIM): market del master → las 5 cuentas llenan casi al mismo precio (gap << fill-level).
- AC6 (SIM): límite/TP del master → slaves llenan al **mismo** precio del límite.
- AC7 (SIM): bracket del master → slaves replican TP+SL como OCO; al llenar uno, el otro se cancela.
- AC8 (SIM): cancelar/modificar en el master → se replica en los slaves.
- AC9 (SIM): el guard sigue bloqueando entradas de un slave pasado de tope; el sweep corrige residual.

## No-goals (esta vuelta)
- Órdenes ya vivas antes de que Idem arranque (se espejan sólo las nuevas desde la suscripción).
- Ratios distintos de 1:1 por cuenta.
- Reemplazar la red de seguridad del sweep (se mantiene).

## Reversibilidad
Cambio de motor acotado a `nt/` + un núcleo nuevo. El reconciler/sweep queda intacto como respaldo;
si el order-mirror diera problemas, se puede volver al fill-level reactivando `OnMasterFill`.
