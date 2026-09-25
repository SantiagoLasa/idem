using System;

namespace Idem.Core
{
    // Día de trading con corte a las 5pm ET (como las prop firms): la sesión de Asia de
    // anoche cae en el día de HOY. Convierte a ET (maneja EDT/EST solo) y si la hora >= 17
    // el día de trading es el siguiente. El bug de PropCommand era usar UTC en vez de esto.
    public static class TradingDay
    {
        public static DateTime EtDate(DateTime utc)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var et = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
            var day = et.Date;
            if (et.Hour >= 17) day = day.AddDays(1);
            return day;
        }
    }
}
