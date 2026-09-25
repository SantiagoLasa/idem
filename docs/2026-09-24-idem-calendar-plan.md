# Idem — Fase 5: Calendario local (plan)

> Última fase. Calendario de P&L diario **dentro del dashboard** (no nube), con corte
> de día de trading a las **5pm ET** (como las prop firms) y P&L calculado como
> **delta día-a-día de net-liquidation** persistido localmente.
> Diseño aprobado por el usuario: **A + 1** (calendario en el dashboard WPF que ya existe
> + fuente = snapshot de net-liq a fin de día, P&L = delta día-a-día).

## Por qué así (lecciones de PropCommand)

- **Net-liquidation, no CashValue.** El skew de ~$33 en PropCommand era CashValue vs
  NetLiquidation. El calendario mide net-liq real.
- **Día de trading = corte 5pm ET.** El P&L de la sesión de Asia de anoche cae en el día
  de HOY, como en las prop firms. En PropCommand el bug era UTC vs 5pm ET.
- **P&L = delta día-a-día del snapshot de cierre.** No acumular trade por trade (se
  desincroniza). El cierre de cada día menos el cierre del anterior = P&L del día. Es lo
  que terminó siendo correcto en PropCommand (`ending_balance` day-over-day).
- **Guard de reset/funding.** Si el delta supera un umbral (p. ej. depósito/retiro/nueva
  cuenta), no es P&L: se ignora ese día para no inflar el total.
- **Leer net-liq en el UI-thread.** `Account.Get` off-thread devuelve 0 (bug de PropCommand).
  Igual que `DayPnlPoll`, todo se lee desde el dispatcher.

## Arquitectura

- **Días pasados:** de los snapshots de cierre persistidos → delta día-a-día.
- **Día en curso (hoy):** del `DayPnlCache` que ya existe (realized+unrealized de la
  sesión) — es exactamente el P&L del día en curso, ya validado. El calendario lo pinta
  como celda "viva".
- **Persistencia:** `idem-calendar.txt` (gitignored, específico de la máquina), formato
  línea-por-línea como el `idem-config.txt`.

## Tasks

### Task 1 — Núcleos puros (TDD, `dotnet test`)
Todo NT8-free, testeado en `C:\dev\idem-tests` (auto-incluye `core/**`).

1. **`core\TradingDay.cs`** — `EtDate(DateTime utc) : DateTime` (date-only).
   Convierte UTC→ET vía `TimeZoneInfo` ("Eastern Standard Time", maneja EDT/EST solo);
   si la hora ET >= 17 → día siguiente, si no → mismo día. RED: 16:59 ET, 17:00 ET
   (borde), medianoche ET, un día de verano (EDT) y uno de invierno (EST).
2. **`core\NetLiqSnapshot.cs`** — `struct NetLiqSnapshot { string Account; DateTime Date; double NetLiq; }`.
3. **`core\CalendarStore.cs`** — guarda snapshots de cierre por (cuenta, fecha).
   - `Record(account, date, netLiq)`.
   - `DailyPnl(date, fundingGuard) : double` — suma sobre cuentas de
     `(cierre[date] − cierre[fechaPrevia registrada])`; si `|delta| > fundingGuard`
     ese aporte se ignora (funding/reset). Sin fecha previa para una cuenta → 0.
   - `Totals(fundingGuard) : Dictionary<DateTime,double>` — P&L por fecha.
   RED: dos días consecutivos (delta simple), hueco entre días (usa el último cierre
   registrado), multi-cuenta (suma), guard de funding (delta gigante → ignora).
4. **`core\CalendarSerializer.cs`** — `Parse(text) : List<NetLiqSnapshot>` +
   `ToText(IEnumerable<NetLiqSnapshot>) : string`. Formato `snap=<cuenta>|<yyyy-MM-dd>|<netLiq>`.
   RED: round-trip, líneas vacías/comentario ignoradas, línea mal formada ignorada.
5. **`core\Heatmap.cs`** — `Bucket(double pnl) : HeatLevel {Loss,Flat,Gain}` +
   `Intensity(double pnl, double maxAbs) : double` (0..1 para el degradé). RED: signo,
   cero → Flat, maxAbs=0 → 0 sin dividir por cero.

**Gate:** `cd /c/dev/idem-tests && dotnet test` en verde (rojo primero).

### Task 2 — Cáscara NT8: grabador + persistencia (F5)
1. **`nt\CalendarRecorder.cs`** — DispatcherTimer (UI-thread, 1s o piggyback en el poll).
   Lee `AccountItem.NetLiquidation` por cuenta; trackea el día de trading actual
   (`TradingDay.EtDate(DateTime.UtcNow)`); al detectar rollover, escribe el snapshot de
   **cierre** del día que terminó en el `CalendarStore` y persiste `idem-calendar.txt`.
   Carga el archivo al arrancar (historial previo).
2. **`nt\IdemRuntime.cs`** — exponer `CalendarStore Calendar` (para que la ventana lo lea).
3. **`Idem.cs`** — instanciar `CalendarStore`, cargar `idem-calendar.txt`, arrancar
   `CalendarRecorder`, publicarlo en `IdemRuntime`.

**Gate:** F5 compila limpio + el usuario confirma que el archivo se crea/persiste.

### Task 3 — UI: grilla del calendario (F5)
1. **`ui\IdemWindow.cs`** — sección/pestaña "Calendario": grilla mensual (7 columnas),
   cada celda = día con su P&L total, coloreada por `Heatmap` (verde ganancia / rojo
   pérdida, intensidad por `maxAbs` del mes). Hoy = celda viva desde `DayPnlCache`.
   Navegación de mes (◀ ▶) y total del mes. Timer de UI reusa el patrón de 300ms.

**Gate:** F5 compila + el usuario ve el calendario con días coloreados.

## Fuera de alcance (esta vuelta)
- Sync a la nube / Vercel (Idem es local a propósito).
- Desglose por trade dentro del día (sólo total diario).
- Export CSV (se puede agregar después si hace falta).
