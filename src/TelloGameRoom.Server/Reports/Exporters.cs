using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using TelloGameRoom.Core.Billing;
using TelloGameRoom.Core.Dtos;

namespace TelloGameRoom.Server.Reports;

/// <summary>How to print money and local times in an export.</summary>
public sealed record ExportFormat(ExportText Text, string CurrencySymbol, int CurrencyDecimals, TimeZoneInfo Zone, string ShopName)
{
    public string Money(long cents) => MoneyFormatter.Format(cents, CurrencySymbol, CurrencyDecimals, Text.Culture);

    public string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Hours(long seconds) => (seconds / 3600m).ToString("0.##", CultureInfo.InvariantCulture);
}

public static class CsvExporter
{
    public static byte[] Report(ReportDto r, ExportFormat f)
    {
        var t = f.Text;
        var csv = new Csv()
            .Row(t["IncomeReport"], f.ShopName)
            .Row(t["Branch"], r.BranchName)
            .Row(t["Period"], r.From, r.To)
            .Row()
            .Row(t["Income"], PriceCalculator.FromCents(r.IncomeCents))
            .Row(t["Sales"], PriceCalculator.FromCents(r.SalesCents))
            .Row(t["Refunds"], PriceCalculator.FromCents(r.RefundsCents))
            .Row(t["Sessions"], r.Sessions)
            .Row(t["FreeSessions"], r.FreeSessions)
            .Row(t["HoursSold"], r.SecondsSold / 3600m)
            .Row()
            .Row(t["ByDay"])
            .Row(t["Date"], t["Income"], t["Sessions"], t["HoursSold"]);
        foreach (var d in r.ByDay)
        {
            csv.Row(d.Date, PriceCalculator.FromCents(d.IncomeCents), d.Sessions, d.SecondsSold / 3600m);
        }

        csv.Row().Row(t["ByEmployee"]).Row(t["Employee"], t["Income"], t["Sessions"], t["HoursSold"]);
        foreach (var e in r.ByEmployee)
        {
            csv.Row(e.Name, PriceCalculator.FromCents(e.IncomeCents), e.Sessions, e.SecondsSold / 3600m);
        }

        csv.Row().Row(t["ByMachine"]).Row(t["Machine"], t["Income"], t["Sessions"], t["HoursSold"]);
        foreach (var m in r.ByMachine)
        {
            csv.Row(m.Name, PriceCalculator.FromCents(m.IncomeCents), m.Sessions, m.SecondsSold / 3600m);
        }

        csv.Row().Row(t["BusiestHours"]).Row(t["Hour"], t["Sessions"], t["Income"]);
        foreach (var h in r.ByHour)
        {
            csv.Row(h.Hour, h.Sessions, PriceCalculator.FromCents(h.IncomeCents));
        }

        return csv.ToBytes();
    }

    public static byte[] Shifts(IEnumerable<ShiftDto> shifts, ExportFormat f)
    {
        var t = f.Text;
        var csv = new Csv().Row(t["Employee"], t["Opened"], t["Closed"], t["OpeningCash"], t["Sales"], t["Refunds"],
            t["ExpectedCash"], t["CountedCash"], t["Difference"], t["Sessions"], t["Notes"]);
        foreach (var s in shifts)
        {
            csv.Row(s.UserDisplayName, f.Local(s.OpenedAtUtc), s.ClosedAtUtc is { } c ? f.Local(c) : t["Open"],
                PriceCalculator.FromCents(s.OpeningCashCents), PriceCalculator.FromCents(s.SalesCents), PriceCalculator.FromCents(s.RefundsCents),
                PriceCalculator.FromCents(s.ExpectedCashCents),
                s.CountedCashCents is { } counted ? PriceCalculator.FromCents(counted) : null,
                s.DifferenceCents is { } diff ? PriceCalculator.FromCents(diff) : null,
                s.SessionsStarted, s.Notes);
        }

        return csv.ToBytes();
    }

    public static byte[] Audit(IEnumerable<AuditEntryDto> entries, ExportFormat f)
    {
        var t = f.Text;
        var csv = new Csv().Row(t["Time"], t["Action"], t["User"], t["Machine"], t["OldValue"], t["NewValue"], t["Reason"], t["Ip"]);
        foreach (var e in entries)
        {
            csv.Row(f.Local(e.AtUtc), e.Action, e.Username, e.MachineName, e.OldValue, e.NewValue, e.Reason, e.IpAddress);
        }

        return csv.ToBytes();
    }
}

public static class PdfExporter
{
    private const string Accent = "#4F46E5";
    private const string Muted = "#6B7280";
    private const string Line = "#E5E7EB";

    public static byte[] Report(ReportDto r, ExportFormat f)
    {
        var t = f.Text;
        return Build(f, t["IncomeReport"], $"{r.BranchName}  ·  {r.From:yyyy-MM-dd} – {r.To:yyyy-MM-dd}", column =>
        {
            column.Item().Row(row =>
            {
                Stat(row, t["Income"], f.Money(r.IncomeCents));
                Stat(row, t["Sessions"], r.Sessions.ToString(CultureInfo.InvariantCulture));
                Stat(row, t["HoursSold"], ExportFormat.Hours(r.SecondsSold));
                Stat(row, t["Refunds"], f.Money(r.RefundsCents));
            });

            Section(column, t["ByDay"], [t["Date"], t["Income"], t["Sessions"], t["HoursSold"]],
                r.ByDay.Where(d => d.IncomeCents != 0 || d.Sessions != 0)
                    .Select(d => new[] { d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), f.Money(d.IncomeCents), d.Sessions.ToString(CultureInfo.InvariantCulture), ExportFormat.Hours(d.SecondsSold) }));
            Section(column, t["ByEmployee"], [t["Employee"], t["Income"], t["Sessions"], t["HoursSold"]],
                r.ByEmployee.Select(e => new[] { e.Name, f.Money(e.IncomeCents), e.Sessions.ToString(CultureInfo.InvariantCulture), ExportFormat.Hours(e.SecondsSold) }));
            Section(column, t["ByMachine"], [t["Machine"], t["Income"], t["Sessions"], t["HoursSold"]],
                r.ByMachine.Select(m => new[] { m.Name, f.Money(m.IncomeCents), m.Sessions.ToString(CultureInfo.InvariantCulture), ExportFormat.Hours(m.SecondsSold) }));
            Section(column, t["BusiestHours"], [t["Hour"], t["Sessions"], t["Income"]],
                r.ByHour.Where(h => h.Sessions > 0).OrderByDescending(h => h.Sessions).Take(8)
                    .Select(h => new[] { $"{h.Hour:00}:00", h.Sessions.ToString(CultureInfo.InvariantCulture), f.Money(h.IncomeCents) }));
        });
    }

    public static byte[] Shift(ShiftDto s, string branchName, ExportFormat f)
    {
        var t = f.Text;
        return Build(f, t["ShiftReport"], $"{branchName}  ·  {s.UserDisplayName}", column =>
        {
            Section(column, string.Empty, [string.Empty, string.Empty],
            [
                [t["Opened"], f.Local(s.OpenedAtUtc)],
                [t["Closed"], s.ClosedAtUtc is { } closed ? f.Local(closed) : t["Open"]],
                [t["Sessions"], s.SessionsStarted.ToString(CultureInfo.InvariantCulture)],
                [t["OpeningCash"], f.Money(s.OpeningCashCents)],
                [t["Sales"], f.Money(s.SalesCents)],
                [t["Refunds"], f.Money(s.RefundsCents)],
                [t["ExpectedCash"], f.Money(s.ExpectedCashCents)],
                [t["CountedCash"], s.CountedCashCents is { } counted ? f.Money(counted) : "-"],
                [t["Difference"], s.DifferenceCents is { } diff ? f.Money(diff) : "-"],
            ]);

            if (s.DifferenceCents is { } d)
            {
                var (label, color) = d == 0 ? (t["Balanced"], "#059669") : d < 0 ? (t["Short"], "#DC2626") : (t["Over"], "#D97706");
                column.Item().PaddingTop(12).Text(label).FontSize(14).Bold().FontColor(color);
            }

            if (!string.IsNullOrEmpty(s.Notes))
            {
                column.Item().PaddingTop(12).Text($"{t["Notes"]}: {s.Notes}");
            }
        });
    }

    private static byte[] Build(ExportFormat f, string title, string subtitle, Action<ColumnDescriptor> content) =>
        Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            page.DefaultTextStyle(x => x.FontSize(10));
            page.Header().PaddingBottom(12).Column(header =>
            {
                header.Item().Text(f.ShopName).FontSize(11).FontColor(Muted);
                header.Item().Text(title).FontSize(20).Bold().FontColor(Accent);
                header.Item().Text(subtitle).FontColor(Muted);
            });
            page.Content().Column(column =>
            {
                column.Spacing(6);
                content(column);
            });
            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(x => x.FontSize(8).FontColor(Muted));
                text.Span($"{f.Text["Generated"]} {f.Local(DateTime.UtcNow)}  ·  {f.Text["Page"]} ");
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        })).GeneratePdf();

    private static void Stat(RowDescriptor row, string label, string value) =>
        row.RelativeItem().Padding(4).Border(1).BorderColor(Line).Padding(10).Column(c =>
        {
            c.Item().Text(label).FontSize(9).FontColor(Muted);
            c.Item().Text(value).FontSize(15).Bold();
        });

    private static void Section(ColumnDescriptor column, string title, string[] headers, IEnumerable<string[]> rows)
    {
        var data = rows.ToList();
        if (data.Count == 0)
        {
            return;
        }

        if (title.Length > 0)
        {
            column.Item().PaddingTop(14).Text(title).FontSize(13).SemiBold();
        }

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(2);
                for (var i = 1; i < headers.Length; i++)
                {
                    c.RelativeColumn();
                }
            });

            if (headers.Any(h => h.Length > 0))
            {
                table.Header(h =>
                {
                    for (var i = 0; i < headers.Length; i++)
                    {
                        var cell = h.Cell().BorderBottom(1).BorderColor(Line).PaddingVertical(4);
                        (i == 0 ? cell : cell.AlignRight()).Text(headers[i]).FontColor(Muted).FontSize(9);
                    }
                });
            }

            foreach (var row in data)
            {
                for (var i = 0; i < row.Length; i++)
                {
                    var cell = table.Cell().BorderBottom(1).BorderColor(Line).PaddingVertical(4);
                    (i == 0 ? cell : cell.AlignRight()).Text(row[i]);
                }
            }
        });
    }
}
