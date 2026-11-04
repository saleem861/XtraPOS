using System.Text;
using Microsoft.Extensions.Hosting;

namespace XtraPOS.Api.Diagnostics;

public sealed class DiagnosticEvent
{
    public string? Area { get; set; }
    public string? Action { get; set; }
    public string? Detail { get; set; }
}

public static class DiagnosticLog
{
    private static readonly object Gate = new();
    private const int MaxBytes = 1_500_000;

    public static string FilePath(IHostEnvironment environment) =>
        Path.Combine(environment.ContentRootPath, "App_Data", "myLog.txt");

    public static void Write(IHostEnvironment environment, string area, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\t{area}\t{Sanitize(message)}{Environment.NewLine}";
        var path = FilePath(environment);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line, Encoding.UTF8);
            var info = new FileInfo(path);
            if (info.Length <= MaxBytes) return;
            var text = File.ReadAllText(path, Encoding.UTF8);
            var keep = text.Substring(text.Length / 2);
            var cut = keep.IndexOf('\n');
            if (cut >= 0) keep = keep.Substring(cut + 1);
            File.WriteAllText(path, keep, Encoding.UTF8);
        }
    }

    public static string Read(IHostEnvironment environment)
    {
        var path = FilePath(environment);
        lock (Gate)
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        }
    }

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Length > 500 ? value[..500] : value;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)(password|secret|token|otp|authorization|cookie)\s*[:=]\s*\S+", "$1=(redacted)");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\d{7,}", "###");
        return text.Replace('\r', ' ').Replace('\n', ' ');
    }
}
