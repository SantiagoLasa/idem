# MEMO — Idem

> Memoria viva del proyecto. Leer al arrancar sesión, actualizar al cerrar.
> Idem = versión limpia de PropCommand (copy + dashboard local, uso personal, sin nube).

## Status

**Última sesión:** 2026-09-25 — ✅ **PROYECTO COMPLETO. Fases 1-5 hechas y validadas.** Idem = copy trader + dashboard local con calendario, todo andando en SIM. Última fase (5, Calendario) cerrada: grilla mensual de P&L con corte 5pm ET, net-liq real, persistencia local y heatmap. **63 tests puros en verde** + F5 limpio + calendario visible. Repo limpio.

**Fase 5 — Calendar (2026-09-25):** calendario de P&L diario DENTRO del dashboard (no nube). Diseño A+1: grilla en la ventana WPF + fuente = snapshot de net-liq a fin de día, P&L = delta día-a-día. Núcleos puros (TDD): `TradingDay.EtDate` (corte 5pm ET, EDT/EST vía TimeZoneInfo "Eastern Standard Time"), `CalendarStore` (P&L = delta día-a-día del cierre + guard de funding), `CalendarSerializer` (persist `idem-calendar.txt`, cultura invariante), `Heatmap` (nivel por signo + intensidad). Cáscara NT8: `CalendarRecorder` (lee **NetLiquidation** en UI-thread, sobrescribe el día en curso = último es el cierre aun si NT8 cierra a mitad de sesión, persiste cada ~60s y al rollover). UI: grilla mensual coloreada, hoy en vivo desde `DayPnlCache` (anda día uno, sin necesitar cierre previo), navegación de mes + total. Extra: el master ahora entra al `DayPnlPoll` (antes su P&L del día quedaba en 0).

---
**Sesión previa:** 2026-09-24 — ✅ **Fases 1, 2 y 3 COMPLETAS Y VALIDADAS EN SIM.** El copy de Idem está entero: réplica (reconciler-a-target + safety sweep) + **guard de pérdida diaria** + **stop de protección espejado**. Validado por el usuario: el guard bloquea entradas cuando el slave perdió su tope del día (nunca las salidas), y el mirror-stop actualiza precio y cantidad al instante siguiendo al master, se cancela al quedar flat. 35 tests puros en verde + F5 limpio + SIM ok.

**Guard = stop de pérdida diaria** (no proximidad al drawdown): bloquea entradas nuevas cuando el P&L del día (realized+unrealized) llega a `-dailyLossLimit`. Config: `slave=cuenta,perdidaDiaria` (sin cushion). Para testear el stop hay que subir el tope alto (si no, el guard bloquea).

### Bugs encontrados y resueltos (prueba extrema 2026-09-24, muchos contratos + TPs rápidos)
- **RUNAWAY por órdenes duplicadas** (un slave llegó a −$1.8M): faltaba el guard de orden en vuelo. Se reconciliaba en cada fill + cada sweep sin nada que impidiera mandar una segunda orden antes de que el fill de la primera confirmara. Fix: `PendingIntents` (core, testeado) cableado en `CopyEngine` — un par no manda otra orden hasta que su net real llega al target o vence el timeout (3s). Era el `PendingIntentLedger` de PropCommand, simplificado de más.
- **POSICIONES TRABADAS por desync del tracker**: el reconciler trabajaba sobre el tracker interno, que driftea bajo carga (NT8 dropea/batchea ExecutionUpdate), así que creía que los slaves matcheaban al master y no cerraba las colgadas. Fix: el safety sweep lee el net REAL del broker (`RealNet` desde `account.Positions`), re-seedea el tracker y reconcilia contra la verdad cada 1s → auto-destraba. Es el "broker-resync heal" de PropCommand. **Lección: el sweep SIEMPRE reconcilia contra el broker, nunca contra el tracker.**
- **Casing de nombres de cuenta**: el resolve era case-sensitive (sim102 no matcheaba). Ahora es `OrdinalIgnoreCase` + loguea `cuentas disponibles` al arrancar.
- **Gotcha operativo:** editar el `idem-config.txt` requiere **reiniciar NT8** (no alcanza F5) para que Idem lo relea — el Boot corre en OnWindowCreated.

### Done (cont.)
- [x] **Fase 4 — Dashboard WPF COMPLETA Y VALIDADA.** Menú "Idem → Dashboard" en el Control Center; ventana con: flota en vivo (net, P&L del día, ⚠ guard bloqueando), feed de réplicas, controles **Pausa/Reanudar** (togglea `Config.Enabled`) y **FLATTEN ALL** (flatea master+slaves), y **editor de config en vivo** (TextBox + Guardar → `Reconfigure` re-watchea cuentas + reescribe el `.txt`, sin reiniciar). Núcleos puros: `FleetView`, `IdemConfigWriter`. Estado vivo publicado por `IdemRuntime`. Gotchas de F5: `OrderAction` ambiguo (alias), `FlattenEverything()` es estático (`Account.FlattenEverything()`), `Flatten(Instrument[])` es de instancia. 46 tests verdes.

### Next up
- **Proyecto completo — no quedan fases.** Idem es un copy trader + dashboard local con calendario, entero y validado en SIM.
- **Antes de plata real:** bajar los topes de pérdida diaria a valores reales (están en 5000 para tests, se editan desde el dashboard) y probar más en SIM.
- **Limitación conocida del calendario:** si NT8 no corre al rollover de 5pm ET, se pierde el snapshot de cierre de ese día y el delta abarca varios días (el guard de funding acota lo grueso). Aceptable para uso personal; hoy siempre anda vía DayPnlCache.
- **Idea futura opcional:** export CSV / desglose por trade dentro del día (fuera de alcance esta vuelta).

### Done
- [x] **Fase 1 — núcleos puros** (`core\`, testeados con `dotnet test`): `Sizing` (1:1), `RiskGuard` (bloquea entradas cerca del DD, nunca salidas), `Reconciler` (delta con signo → Buy/Sell/None), `Types` (OrderAction/ReconcileOrder).
- [x] **Fase 2 — integración del copy**:
  - Puro (testeado): `PositionTracker` (net por cuenta|instrumento, feed incondicional), `CopyDecision` (cerebro: reconciler+guard por slave), `IdemConfig` (parse línea-por-línea).
  - NT8 (F5 + SIM): `FillMonitor` (suscribe tras Connected vía poll), `OrderSubmit` (market + lock por slave|instrumento), `CopyEngine` (orquesta + safety sweep 1s), `Idem.cs` (AddOn, arranca en OnWindowCreated, lee idem-config.txt).

### Next up
- **Fase 3 — MirrorStop**: stop de protección espejado en cada slave + cablear el DD real en el `drawdown` callback (hoy devuelve 0 = guard inactivo).
- **Fase 4 — Dashboard WPF** (flota, feed de réplicas, controles Flatten/Pausa, config UI).
- **Fase 5 — Calendar** local (net liq real UI-thread + corte 5pm ET + persistencia + heatmap).

## Decisions / Knowledge

- **Estructura:** todo en `bin\Custom\Idem\` (repo git propio). `core\` NT8-free (net48/net8.0), `nt\` integración NT8, `Idem.cs` entry. Docs en `docs\` (spec + planes por fase).
- **Test harness:** proyecto xUnit en `C:\dev\idem-tests\` (FUERA de bin\Custom — NT8 compila todo lo que hay ahí y xUnit rompería el F5). Linkea los `.cs` de `core\` vía `<Compile Include>`. Correr: `cd /c/dev/idem-tests && dotnet test`. Mismos archivos que compila NT8, cero staleness.
- **Config:** `idem-config.txt` (línea-por-línea `clave=valor`, gitignored, específico de la máquina). Formato: `master=`, `cushion=`, `enabled=`, `slave=cuenta,ddLimit`.
- **Gotchas de NT8 pegados en F5 (para no repetir):**
  1. NT8 net48 **NO trae `System.Text.Json`** (CS0234) → parser manual.
  2. `OrderAction` es **ambiguo** entre `Idem.Core` y `NinjaTrader.Cbi` → alias (`NtOrderAction`) o calificar.
  3. PropCommand nunca escuchó el evento de conexión (por eso su carrera de arranque) → Idem usa **poll de `Connection.Status`** hasta Connected, sin depender de un evento no verificado.
  4. AddOn arranca en `OnWindowCreated` (no `State.Configure`), guardado con un flag estático una vez.
- **Pendiente de verificar en SIM si aparece:** `Sell` vs `SellShort` desde flat (el mapeo Idem→NT8 usa Buy/Sell; si NT8 exige SellShort para abrir short, ajustar por el signo del target).
- **Diseño de fondo:** "núcleo funcional, cáscara imperativa" — el cerebro (decisión) es puro y testeable; la cáscara NT8 sólo ejecuta. ~80% de la lógica verificable sin abrir NinjaTrader.

## Session Log

### 2026-09-24 — Arranque de Idem: brainstorm → spec → planes → Fases 1-2
- Brainstorm completo (arquitectura, módulos, flujo, mapa de bugs, dashboard, testing). Spec + 2 planes escritos y commiteados.
- Fase 1 ejecutada inline (Sizing/RiskGuard/Reconciler, TDD, 12 tests).
- Fase 2: cerebro puro ejecutado inline (PositionTracker/CopyDecision/IdemConfig, +12 tests = 24). Cáscara NT8 escrita, F5 iteró 2 veces (System.Text.Json, OrderAction ambiguo), SIM validado por el usuario.
- Repo: `bin\Custom\Idem\` con su git. Último commit `1e52e9d`.
