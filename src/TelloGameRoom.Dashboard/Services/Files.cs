using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Serilog;

namespace TelloGameRoom.Dashboard.Services;

/// <summary>Save-file and open-file helpers for exports, backups and the logo.</summary>
public static class Files
{
    /// <summary>Asks where to save, writes the bytes and opens the file. Returns false when the user cancelled.</summary>
    public static bool Save(byte[] bytes, string suggestedName, string filter, bool open = true)
    {
        var dialog = new SaveFileDialog { FileName = suggestedName, Filter = filter };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true)
        {
            return false;
        }

        File.WriteAllBytes(dialog.FileName, bytes);
        if (open)
        {
            try
            {
                Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Information("Saved {File} but could not open it: {Message}", dialog.FileName, ex.Message);
            }
        }

        return true;
    }

    public static byte[]? Open(string filter, long maxBytes)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true)
        {
            return null;
        }

        var info = new FileInfo(dialog.FileName);
        if (info.Length > maxBytes)
        {
            throw new IOException(Loc.T("Common_FileTooLarge"));
        }

        return File.ReadAllBytes(dialog.FileName);
    }

    public const string CsvFilter = "CSV|*.csv";
    public const string PdfFilter = "PDF|*.pdf";
    public const string BackupFilter = "TelloGameRoom backup|*.db";
    public const string ImageFilter = "PNG, JPEG|*.png;*.jpg;*.jpeg";
}
