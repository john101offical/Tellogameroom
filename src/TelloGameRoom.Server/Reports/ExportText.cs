using System.Collections.Frozen;

namespace TelloGameRoom.Server.Reports;

/// <summary>Labels used in exported files, in the languages the product ships with.</summary>
public sealed class ExportText
{
    private static readonly FrozenDictionary<string, (string En, string Es)> Texts = new Dictionary<string, (string, string)>
    {
        ["IncomeReport"] = ("Income report", "Informe de ingresos"),
        ["ShiftReport"] = ("Shift report", "Informe de turno"),
        ["Branch"] = ("Branch", "Sede"),
        ["Period"] = ("Period", "Periodo"),
        ["Generated"] = ("Generated", "Generado"),
        ["Income"] = ("Income", "Ingresos"),
        ["Sales"] = ("Sales", "Ventas"),
        ["Refunds"] = ("Refunds", "Devoluciones"),
        ["Sessions"] = ("Sessions", "Sesiones"),
        ["FreeSessions"] = ("Free sessions", "Sesiones gratis"),
        ["HoursSold"] = ("Hours sold", "Horas vendidas"),
        ["ByDay"] = ("By day", "Por día"),
        ["ByEmployee"] = ("By employee", "Por empleado"),
        ["ByMachine"] = ("By machine", "Por equipo"),
        ["BusiestHours"] = ("Busiest hours", "Horas más ocupadas"),
        ["Date"] = ("Date", "Fecha"),
        ["Employee"] = ("Employee", "Empleado"),
        ["Machine"] = ("Machine", "Equipo"),
        ["Hour"] = ("Hour", "Hora"),
        ["Opened"] = ("Opened", "Apertura"),
        ["Closed"] = ("Closed", "Cierre"),
        ["OpeningCash"] = ("Opening cash", "Efectivo inicial"),
        ["ExpectedCash"] = ("Expected cash", "Efectivo esperado"),
        ["CountedCash"] = ("Counted cash", "Efectivo contado"),
        ["Difference"] = ("Difference", "Diferencia"),
        ["Notes"] = ("Notes", "Notas"),
        ["Open"] = ("Open", "Abierto"),
        ["Balanced"] = ("Cash is balanced", "El efectivo cuadra"),
        ["Short"] = ("Cash is short", "Falta efectivo"),
        ["Over"] = ("Cash is over", "Sobra efectivo"),
        ["Time"] = ("Time", "Hora"),
        ["Action"] = ("Action", "Acción"),
        ["User"] = ("User", "Usuario"),
        ["OldValue"] = ("Old value", "Valor anterior"),
        ["NewValue"] = ("New value", "Valor nuevo"),
        ["Reason"] = ("Reason", "Motivo"),
        ["Ip"] = ("IP address", "Dirección IP"),
        ["System"] = ("System", "Sistema"),
        ["Page"] = ("Page", "Página"),
    }.ToFrozenDictionary();

    private readonly bool _spanish;

    public ExportText(string? language) =>
        _spanish = language is not null && language.StartsWith("es", StringComparison.OrdinalIgnoreCase);

    public string this[string key] => Texts.TryGetValue(key, out var t) ? (_spanish ? t.Es : t.En) : key;

    public System.Globalization.CultureInfo Culture =>
        System.Globalization.CultureInfo.GetCultureInfo(_spanish ? "es-CO" : "en-US");
}
