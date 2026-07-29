using System.Globalization;
using System.Text;
using ChronoMailBridge.Core;

namespace ChronoMailBridge.Infrastructure;

public sealed class CsvReportWriter : IReportWriter
{
    public async Task<IReadOnlyList<string>> WriteAsync(
        string reportsDirectory,
        MigrationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(reportsDirectory);
        string summaryCsv = Path.Combine(reportsDirectory, "summary.csv");
        string errorsCsv = Path.Combine(reportsDirectory, "errors.csv");
        string summaryText = Path.Combine(reportsDirectory, "summary.txt");

        var summary = new StringBuilder("folder,year,status,messages,bytes\r\n");
        foreach (ReportRow row in snapshot.Rows)
        {
            summary.Append(Csv(row.Folder)).Append(',')
                .Append(row.Year.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Status).Append(',')
                .Append(row.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Bytes.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }

        var errors = new StringBuilder("technical_id,folder,internal_date,size,status,code\r\n");
        foreach (ReviewItem item in snapshot.ReviewItems)
        {
            errors.Append(item.TechnicalId.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(item.Folder)).Append(',')
                .Append(item.InternalDate.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Size.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.Status).Append(',')
                .Append(Csv(item.ErrorCode ?? string.Empty)).Append("\r\n");
        }

        long total = snapshot.Rows.Sum(row => row.Count);
        long bytes = snapshot.Rows.Sum(row => row.Bytes);
        var text = new StringBuilder()
            .AppendLine("ChronoMail Bridge — migration summary")
            .AppendLine(CultureInfo.InvariantCulture, $"Messages recorded: {total:N0}")
            .AppendLine(CultureInfo.InvariantCulture, $"Bytes recorded: {bytes:N0}")
            .AppendLine(CultureInfo.InvariantCulture, $"Items requiring review: {snapshot.ReviewItems.Count:N0}")
            .AppendLine()
            .AppendLine("The report omits all sensitive message content.")
            .AppendLine("Keep the local archive on a BitLocker-protected drive.");

        await File.WriteAllTextAsync(summaryCsv, summary.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(errorsCsv, errors.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(summaryText, text.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        return [summaryCsv, errorsCsv, summaryText];
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
