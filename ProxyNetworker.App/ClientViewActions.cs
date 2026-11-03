using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using TKCode.Arc;

namespace ProxyNetworker.App;

internal static class ClientViewActions
{
    internal static void ToggleTheme() =>
        ArcTheme.Apply(theme => theme.Appearance(
            ArcThemeManager.Current?.IsDark == true ? ArcAppearance.Light : ArcAppearance.Dark));

    internal static async Task ImportAsync(Control view)
    {
        if (view.DataContext is not MainViewModel model || !model.CanEditConfiguration) return;
        try
        {
            var storage = TopLevel.GetTopLevel(view)?.StorageProvider;
            if (storage is null) return;
            var files = await storage.OpenFilePickerAsync(new()
            {
                Title = "导入客户端配置", AllowMultiple = false,
                FileTypeFilter = [new("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] }]
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            await model.ImportAsync(stream, files[0].TryGetLocalPath());
        }
        catch (Exception exception) { model.Error = exception.Message; }
    }

    internal static async Task ExportAsync(Control view)
    {
        if (view.DataContext is not MainViewModel model) return;
        try
        {
            var storage = TopLevel.GetTopLevel(view)?.StorageProvider;
            if (storage is null) return;
            var file = await storage.SaveFilePickerAsync(new()
            {
                Title = "导出脱敏诊断", SuggestedFileName = "proxynetworker-diagnostics.txt", DefaultExtension = "txt"
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(model.ExportDiagnostics()));
            model.Notice = "诊断已导出";
        }
        catch (Exception exception) { model.Error = exception.Message; }
    }
}
