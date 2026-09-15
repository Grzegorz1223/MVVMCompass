using Android.App;
using Android.Content.PM;
using Android.OS;

namespace MVVMCompass.Sample;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        System.Environment.SetEnvironmentVariable("MVVMCOMPASS_SMOKE",
            Intent?.GetBooleanExtra("mvvmcompass_smoke", false) == true ? "1" : null);
        System.Environment.SetEnvironmentVariable("MVVMCOMPASS_SMOKE_RUN_ID",
            Intent?.GetStringExtra("mvvmcompass_smoke_id"));
        System.Environment.SetEnvironmentVariable("MVVMCOMPASS_SMOKE_SUITE",
            Intent?.GetStringExtra("mvvmcompass_smoke_suite"));
        base.OnCreate(savedInstanceState);
        if (Intent?.GetBooleanExtra("mvvmcompass_smoke", false) == true)
            Window?.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
    }
}
