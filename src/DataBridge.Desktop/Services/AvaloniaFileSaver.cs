using System.Text;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using DataBridge.Desktop.ViewModels;

namespace DataBridge.Desktop.Services;

/// <summary>Saves text through the main window's native save dialog, as UTF-8.</summary>
internal sealed class AvaloniaFileSaver : IFileSaver
{
    public async Task<bool> SaveTextAsync(string suggestedFileName, string text, CancellationToken cancellationToken)
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window is null)
        {
            return false;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the plan's SQL script",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "sql",
            FileTypeChoices = [new FilePickerFileType("SQL script") { Patterns = ["*.sql"] }],
            ShowOverwritePrompt = true,
        });
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
        return true;
    }
}
