# Idem — Diseño

**Fecha:** 2026-09-24
**Estado:** spec para revisión
**Autor:** Santiago + Claude

## Qué es

Idem es un AddOn de NinjaTrader 8 para **uso personal**, versión limpia y simplificada
de PropCommand. Hace **dos cosas**: un **copy trader** (un master → N slaves) y un
**dashboard local** dentro de NT8. Está optimizado para lo que más se opera:
**scalping de NQ y MNQ**, donde la velocidad de entradas y salidas importa.

Se construye desde cero **conociendo todos los bugs de PropCommand** (documentados en la
sección "Mapa de bugs"), para evitarlos por diseño en vez de parcharlos después.

**No hay nube.** Todo corre in-process dentro de NT8: el copy y el dashboard comparten
memoria, sin Supabase, sin Vercel, sin bridge token, sin sincronización de red. Esta
decisión elimina el ~90% de la complejidad y de los modos de falla de PropCommand.

## No-objetivos (lo que Idem deliberadamente NO hace)

- Sin nube, sin dashboard web, sin acceso remoto (sólo la máquina de trading).
- Sin multi-master (un solo master; multi-master fue un infierno de reversiones cross-thread).
- Sin licencias, sin base de firmas (firm-rules), sin lifecycle de fases, sin Telegram, sin Stripe.
- Sin bracket completo espejado en v1 (sólo el stop de protección; el target lo maneja el reconciler).
- Sin proyección de lanzamiento público.

## Requisitos

| # | Requisito |
|---|---|
| R1 | Topología **un master → N slaves**, elegidos por el usuario. |
| R2 | Sizing **1:1**: 1 contrato en el master → 1 en cada slave. Sin multiplicadores, sin estado por-cuenta. |
| R3 | **RiskGuard liviano**: no replica una entrada a una cuenta cuyo `DD_actual + colchón ≥ límite`. Un solo umbral en dólares, **sin** heurístico de stop por instrumento. |
| R4 | **Salidas por reconciler-a-target** (Enfoque 1) **+ stop de protección espejado** en cada slave (Enfoque 3). |
| R5 | Réplica **por evento** (submit inmediato en el fill del master) + **safety sweep** de respaldo cada ~1s. |
| R6 | **Dashboard WPF** en NT8: estado en vivo de la flota + feed de réplicas + controles (Flatten All, Pausa, enable/disable por cuenta). |
| R7 | **Calendario de P&L diario local**: net liquidation real leído en el UI-thread, día cortado a **5pm ET**, persistido a archivo local. |
| R8 | La lógica central es **NT8-free** y testeable con `dotnet test` sin abrir NinjaTrader. |

## Arquitectura y módulos

Un solo AddOn (`Idem`), dos mitades in-process (motor de copy + panel WPF). Módulos chicos,
cada uno con una responsabilidad y testeable solo:

| Módulo | Responsabilidad | Tipo |
|---|---|---|
| **PositionTracker** | Net real por (cuenta, instrumento), alimentado por **todo** fill de forma **incondicional** (separado del gating de réplica). Fuente de verdad de posiciones. | núcleo puro + feed NT8 |
| **Reconciler** | `target = masterNet` (1:1); `delta = target − slaveNet`; devuelve la orden a mandar. Idempotente: `delta=0` → no hace nada. Cero "contar fills". | **puro, unit-test** |
| **RiskGuard** | `¿DD_actual + colchón ≥ límite?` → bloquea la entrada de esa cuenta. Una resta y una comparación. | **puro, unit-test** |
| **Sizing** | 1:1 (trivial hoy; aislado por si algún día cambia). | **puro, unit-test** |
| **MirrorStop** | Coloca/actualiza/cancela un stop de protección en cada slave, espejando el precio de stop del master. Cancela **exactamente** cuando el slave queda flat. | núcleo puro + submit NT8 |
| **FillMonitor** | Suscripción a ejecuciones **sólo cuando la cuenta está `Connected`**, y re-suscribe en `ConnectionStatusUpdate`. Confirma fills por la vía primaria (sin timer fijo de fallback). | integración NT8 |
| **CopyEngine** | Orquestador: fill del master → RiskGuard → Reconciler → submit → MirrorStop. Más el safety sweep. | integración |
| **Calendar** | Agrega P&L diario desde snapshots de net liquidation; corte a 5pm ET. | **puro, unit-test** |
| **Dashboard (WPF)** | Ventana flotante en NT8; lee el estado en vivo; controles. | UI |

**Config local:** master + slaves + colchón del RiskGuard + toggle del MirrorStop, elegidos
en el panel y guardados en un archivo (XML/JSON) al lado del AddOn. Sin cuentas, sin token.

## Flujo de datos

**Entrada (abrir/agregar en el master):**
```
Fill del master (evento NT8, inmediato)
  → PositionTracker actualiza masterNet          [incondicional]
  → CopyEngine, por cada slave:
       RiskGuard: ¿DD + colchón ≥ límite? → si sí, BLOQUEA (log, no manda)
       Reconciler: delta = masterNet − slaveNet   → market por el delta
       submit inmediato + registra pending para (slave, instrumento)
  → al confirmar el fill del slave: PositionTracker actualiza slaveNet
  → MirrorStop coloca/actualiza el stop de protección al precio de stop del master
```

**Salida (cerrar/reducir en el master, o salta stop/target):**
```
Fill de salida del master → masterNet baja (o a 0)
  → Reconciler: delta negativo → market que reduce/cierra el slave
  → slave flat → MirrorStop cancela su stop (sin huérfanos)
```
Entrada y salida son **el mismo código**: cerrar es reconciliar a un target más chico. No hay
lógica separada de cierre (donde PropCommand acumuló bugs).

**Safety sweep (~1s, respaldo):** relee el net **real** de cada (slave, instrumento) y reconcilia
si difiere del target. Idempotente. Atrapa cualquier fill que el evento se haya perdido.

**Concurrencia:** un **lock por `slave|instrumento`** envuelve *leer-net → RiskGuard →
construir-orden → submit* de forma atómica. El evento de fill y el sweep nunca mandan órdenes
pisadas → la ventana de reversión cross-thread no existe por construcción.

## Mapa de bugs (evitados desde el diseño)

| Bug de PropCommand | Cómo lo mata Idem |
|---|---|
| Carrera de suscripción al arrancar (no replicaba tras F5) | FillMonitor se suscribe sólo cuando la cuenta está `Connected` + re-suscribe en `ConnectionStatusUpdate`. |
| Reads off-thread dan 0 | Nada crítico se lee del thread de fondo; el tracker se alimenta del evento de ejecución. |
| Contar fills / qty=0 trabado / fantasmas | Reconciler-a-target: apunta a un net conocido; `delta=0` → nada. |
| Reversión cross-thread | Lock por `slave|instrumento` sobre todo el camino de submit. |
| Stops huérfanos | MirrorStop cancela exactamente al quedar flat, gateado por el tracker. |
| Latencia fantasma de 1.5s | Sin timer de fallback fijo; submit inmediato + confirmación por la vía primaria; se mide la latencia **real**. |
| P&L del calendario que no cerraba | Net liquidation real leído en el UI-thread + corte a 5pm ET, local. Sin reconstrucción. |

## Dashboard

Ventana WPF flotante en NT8, en el **UI-thread** (ahí `Account.Get()` es confiable). Lee balance,
P&L y drawdown **directo de NT8 en vivo** — sin sync, sin balance-delta. Muestra lo que NT8 muestra.

Vista compacta (sin las 4 pestañas de PropCommand):
- **Barra superior:** estado de conexión, Replicación ON/OFF, botones `PAUSA` y `FLATTEN ALL`.
- **Flota:** una fila por cuenta (master destacado) con rol, posición viva, P&L de hoy, y una barra
  de DD contra su límite (⚠ cuando el RiskGuard la tiene cerca).
- **Feed de réplicas en vivo:** las últimas N con la **latencia real** y el motivo si algo se bloqueó.
- **Controles:** `FLATTEN ALL` (cierra todo), `PAUSA` (frena la réplica sin cerrar), enable/disable por cuenta.
- **Config (flyout):** master, slaves, colchón del RiskGuard, toggle del MirrorStop. Se guarda local.

Actualización cada ~300ms desde los módulos en memoria. Cero red.

**Calendario de P&L diario (R7):** snapshot del net liquidation real por cuenta, día cortado a
5pm ET, persistido a archivo local. Agregación en el módulo `Calendar` (puro, testeable). Vista de
heatmap mensual simple.

## Testing

- **Núcleos puros (`core\`) → `dotnet test`, sin NT8.** Reconciler, RiskGuard, Sizing, Calendar,
  TradingDay, escritos **sin tipos de NinjaTrader**. TDD (test rojo primero). Cada bug de la tabla
  tiene su test de regresión (reconciler `delta=0` no hace nada, el lock evita la reversión, el
  MirrorStop cancela al flat, subscribe-tras-Connected, etc.).
- **Integración NT8 (`nt\`, `ui\`) → SIM.** FillMonitor, submit de órdenes, panel. Validación en
  Sim101 / grupo sim con F5 + revisión humana. No automatizable fuera de NT8.

## Estructura del proyecto

```
Documents\NinjaTrader 8\bin\Custom\Idem\     ← donde NT8 compila (F5)
  core\        Reconciler.cs, RiskGuard.cs, Sizing.cs, Calendar.cs, TradingDay.cs   (NT8-free)
  nt\          FillMonitor.cs, CopyEngine.cs, MirrorStop.cs, OrderSubmit.cs          (integración)
  ui\          Dashboard.cs, ConfigFlyout.cs
  Idem.cs      entry point del AddOn
  tests\       Idem.Tests.csproj  → referencia core\ y corre con dotnet test
  docs\        spec y decisiones
  .git         repo propio (git-init acá, whitelist de los archivos de Idem)
```

`tests\` referencia los `.cs` de `core\` directo (son NT8-free): `dotnet test` para la lógica pura,
F5 para la integración. `bin/` y `obj/` del test project van al `.gitignore`.

## Fases sugeridas (para el plan de implementación)

1. **Núcleos puros + tests** (Reconciler, RiskGuard, Sizing) — sin NT8, TDD.
2. **Integración del copy** (FillMonitor tras-Connected, CopyEngine, submit) — SIM.
3. **MirrorStop** — SIM.
4. **Dashboard en vivo** (flota + feed + controles).
5. **Calendar** (TradingDay + agregación + persistencia + vista) — núcleo puro + UI.

Cada fase: núcleo puro testeado primero, después la capa NT8, después validación SIM.

## Preguntas abiertas

- Ninguna bloqueante. El detalle de submit de órdenes (Market vs límite con protección de slippage
  para scalping) se decide en la fase de integración, midiendo la latencia real.
