# MEMO — Idem

> Memoria viva del proyecto. Leer al arrancar sesión, actualizar al cerrar.
> Idem = versión limpia de PropCommand (copy + dashboard local, uso personal, sin nube).

## Status

**Última sesión:** 2026-09-24 — ✅ **Fases 1, 2 y 3 COMPLETAS Y VALIDADAS EN SIM.** El copy de Idem está entero: réplica (reconciler-a-target + safety sweep) + **guard de pérdida diaria** + **stop de protección espejado**. Validado por el usuario: el guard bloquea entradas cuando el slave perdió su tope del día (nunca las salidas), y el mirror-stop actualiza precio y cantidad al instante siguiendo al master, se cancela al quedar flat. 35 tests puros en verde + F5 limpio + SIM ok.

**Guard = stop de pérdida diaria** (no proximidad al drawdown): bloquea entradas nuevas cuando el P&L del día (realized+unrealized) llega a `-dailyLossLimit`. Config: `slave=cuenta,perdidaDiaria` (sin cushion). Para testear el stop hay que subir el tope alto (si no, el guard bloquea).

### Next up
- **Fase 4 — Dashboard WPF**: el panel que el usuario ya está pidiendo — setear master/slaves/topes + on/off desde UI (hoy sólo el `idem-config.txt`, se lee 1 vez al arrancar), flota en vivo, feed de réplicas, Flatten/Pausa.
- **Fase 5 — Calendar** local.

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
