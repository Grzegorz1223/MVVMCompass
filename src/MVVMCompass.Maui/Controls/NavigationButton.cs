namespace MVVMCompass;

// These buttons provide navigation input, rather than an application's primary
// button appearance. A local implicit style stops MAUI from merging an ancestor's
// Button style (including its visual states) into the control.
internal static class NavigationButton
{
    internal static Button Create() => new()
    {
        Resources = new ResourceDictionary { new Style(typeof(Button)) },
        BackgroundColor = Colors.Transparent,
        BorderWidth = 0,
        CornerRadius = 0,
        Padding = 0,
        MinimumWidthRequest = 44,
        MinimumHeightRequest = 44
    };
}
