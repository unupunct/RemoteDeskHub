using System.Net;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using RemoteDeskHub.Core.Models;

namespace RemoteDeskHub.Infrastructure.Services;

public static class ReportWriter
{
    static ReportWriter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static ReportFile Create(string baseName, string format, string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        format = (format ?? "csv").Trim().ToLowerInvariant();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{baseName}-{stamp}.{format}";
        return format switch
        {
            "csv" => new ReportFile(fileName, "text/csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ToCsv(columns, rows))).ToArray()),
            "html" => new ReportFile(fileName, "text/html", Encoding.UTF8.GetBytes(ToHtml(title, columns, rows))),
            "pdf" => new ReportFile(fileName, "application/pdf", ToPdf(title, columns, rows)),
            _ => throw new ArgumentException($"Unsupported report format '{format}'. Use csv, html or pdf.", nameof(format))
        };
    }

    private static string ToCsv(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', columns.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(',', row.Select(EscapeCsv)));
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@')
        {
            text = "'" + text;
        }

        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            return '"' + text.Replace("\"", "\"\"") + '"';
        }

        return text;
    }

    private static string ToHtml(string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(title)).AppendLine("</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#12161c;color:#e6edf3;padding:24px}h1{color:#33c6ff;font-size:22px}table{border-collapse:collapse;width:100%;font-size:13px}th{background:#1c2530;color:#9fb3c8;text-align:left;padding:8px;border-bottom:1px solid #2d3a48}td{padding:7px 8px;border-bottom:1px solid #222c37}tr:nth-child(even) td{background:#171d25}.meta{color:#7d8b9a;margin-bottom:18px}</style></head><body>");
        sb.Append("<h1>").Append(WebUtility.HtmlEncode(title)).AppendLine("</h1>");
        sb.Append("<div class=\"meta\">Generated ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" by RemoteDesk Hub ").Append(RemoteDeskHub.Shared.HubConstants.ProductVersion).AppendLine("</div>");
        sb.AppendLine("<table><thead><tr>");
        foreach (var column in columns)
        {
            sb.Append("<th>").Append(WebUtility.HtmlEncode(column)).AppendLine("</th>");
        }

        sb.AppendLine("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            sb.AppendLine("<tr>");
            foreach (var cell in row)
            {
                sb.Append("<td>").Append(WebUtility.HtmlEncode(cell ?? string.Empty)).AppendLine("</td>");
            }

            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static byte[] ToPdf(string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(style => style.FontSize(8.5f));
                page.Header().Column(header =>
                {
                    header.Item().Text(title).FontSize(16).SemiBold().FontColor(Colors.Blue.Medium);
                    header.Item().Text($"RemoteDesk Hub {RemoteDeskHub.Shared.HubConstants.ProductVersion}  •  Generated {DateTime.Now:yyyy-MM-dd HH:mm}")
                        .FontSize(8).FontColor(Colors.Grey.Medium);
                    header.Item().PaddingTop(6).LineHorizontal(0.7f).LineColor(Colors.Grey.Lighten2);
                });
                page.Content().PaddingTop(8).Table(table =>
                {
                    table.ColumnsDefinition(definition =>
                    {
                        foreach (var _ in columns)
                        {
                            definition.RelativeColumn();
                        }
                    });

                    table.Header(headerRow =>
                    {
                        foreach (var column in columns)
                        {
                            headerRow.Cell().Background(Colors.Grey.Lighten3).Padding(3)
                                .Text(column).SemiBold().FontSize(8);
                        }
                    });

                    foreach (var row in rows)
                    {
                        foreach (var cell in row)
                        {
                            table.Cell().BorderBottom(0.4f).BorderColor(Colors.Grey.Lighten3)
                                .Padding(3).Text(cell ?? string.Empty).FontSize(7.6f);
                        }
                    }
                });
                page.Footer().AlignRight().Text(text =>
                {
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }
}
