using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.Mobile;
using SharedApp = ProxyNetworker.App.App;
#if DEBUG
using Spyglass.Agent;
#endif

namespace ProxyNetworker.App.Android;

[Application]
public sealed class AndroidApplication(nint reference, JniHandleOwnership transfer)
    : AvaloniaAndroidApplication<SharedApp>(reference, transfer)
{
    public override void OnCreate()
    {
        SharedApp.ConfigurationPath = Path.Combine(FilesDir!.AbsolutePath, "appsettings.json");
        SharedApp.ControllerFactory = () => AndroidClientController.Shared;
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        builder = builder.With(new FontManagerOptions
        {
            FontFallbacks =
            [
                new FontFallback
                {
                    FontFamily = new FontFamily("avares://ProxyNetworker.App/Assets/Fonts#Noto Sans CJK SC"),
                    UnicodeRange = UnicodeRange.Parse("U+2E80-9FFF,U+F900-FAFF,U+FF00-FFEF")
                }
            ]
        }).UseArc(arc => arc.UseArcControls().UseArcMobile()
            .WithTheme(theme => theme.Appearance(ArcAppearance.System).Accent(ArcAccentPreset.Green)));
#if DEBUG
        builder = builder.UseSpyglass();
#endif
        return base.CustomizeAppBuilder(builder);
    }
}
