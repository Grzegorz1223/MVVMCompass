namespace MVVMCompass.Sample;

// Platform observations stay in the sample; the library has no test-only public hooks.
internal static class NativeCoveragePlatform
{
    [System.Diagnostics.CodeAnalysis.DynamicDependency("SendTapped", typeof(TapGestureRecognizer))]
    internal static void TapPopupOverlay(ContentPage page)
    {
        var layout = (Layout)page.Content!;
        var overlay = layout.Children.OfType<BoxView>().Single();
#if ANDROID
        var view = (Android.Views.View)overlay.Handler!.PlatformView!;
        var root = view.RootView!;
        int[] position = new int[2]; view.GetLocationOnScreen(position);
        int[] origin = new int[2]; root.GetLocationOnScreen(origin);
        var now = Android.OS.SystemClock.UptimeMillis();
        var x = position[0] - origin[0] + 8; var y = position[1] - origin[1] + view.Height / 2f;
        using var down = Android.Views.MotionEvent.Obtain(now, now, Android.Views.MotionEventActions.Down, x, y, 0);
        using var up = Android.Views.MotionEvent.Obtain(now, now + 10, Android.Views.MotionEventActions.Up, x, y, 0);
        root.DispatchTouchEvent(down); root.DispatchTouchEvent(up);
#else
        // MAUI gesture delivery after checking the handled native overlay. Synthetic UITouch
        // is unavailable, so Apple/Windows use MAUI's platform gesture-delivery method.
        if (!Visible(overlay) || Bounds(overlay).Width <= 0) throw new InvalidOperationException("Popup overlay is not handled.");
        typeof(TapGestureRecognizer).GetMethod("SendTapped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(overlay.GestureRecognizers.OfType<TapGestureRecognizer>().Single(), [overlay, (Func<IElement?, Point?>)(_ => new(4, 4))]);
#endif
    }

    internal static Rect Bounds(VisualElement element)
    {
#if ANDROID
        var view = (Android.Views.View)element.Handler!.PlatformView!;
        int[] location = new int[2]; view.GetLocationOnScreen(location);
        var density = view.Resources!.DisplayMetrics!.Density;
        return new(location[0] / density, location[1] / density, view.Width / density, view.Height / density);
#elif IOS
        var view = (UIKit.UIView)element.Handler!.PlatformView!;
        var rect = view.ConvertRectToView(view.Bounds, view.Window);
        return new((double)rect.X, (double)rect.Y, (double)rect.Width, (double)rect.Height);
#elif WINDOWS
        // Labels with vertical text alignment have a MAUI layout container. The
        // inner TextBlock's glyph-sized bounds do not describe the arranged control.
        var handler = (IViewHandler)element.Handler!;
        var view = (Microsoft.UI.Xaml.FrameworkElement)(handler.ContainerView ?? handler.PlatformView)!;
        var rect = view.TransformToVisual(null).TransformBounds(new(0, 0, view.ActualWidth, view.ActualHeight));
        return new(rect.X, rect.Y, rect.Width, rect.Height);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    internal static string? Label(VisualElement element)
    {
#if ANDROID
        // MAUI supplies semantics through its accessibility delegate, not necessarily the
        // raw view property. Query the node Android exposes to accessibility services.
        using var node = ((Android.Views.View)element.Handler!.PlatformView!).CreateAccessibilityNodeInfo();
        var description = node?.ContentDescription?.ToString();
        return string.IsNullOrEmpty(description) ? node?.Text?.ToString() : description;
#elif IOS
        return ((UIKit.UIView)element.Handler!.PlatformView!).AccessibilityLabel;
#elif WINDOWS
        return Microsoft.UI.Xaml.Automation.AutomationProperties.GetName((Microsoft.UI.Xaml.DependencyObject)element.Handler!.PlatformView!);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    internal static bool Enabled(VisualElement element)
    {
#if ANDROID
        return ((Android.Views.View)element.Handler!.PlatformView!).Enabled;
#elif IOS
        return ((UIKit.UIControl)element.Handler!.PlatformView!).Enabled;
#elif WINDOWS
        return ((Microsoft.UI.Xaml.Controls.Control)element.Handler!.PlatformView!).IsEnabled;
#else
        throw new PlatformNotSupportedException();
#endif
    }

    internal static bool Visible(VisualElement element)
    {
#if ANDROID
        return ((Android.Views.View)element.Handler!.PlatformView!).IsShown;
#elif IOS
        var view = (UIKit.UIView)element.Handler!.PlatformView!;
        if (view.Window == null) return false;
        for (var current = view; current != null; current = current.Superview)
            if (current.Hidden || current.Alpha <= 0) return false;
        return true;
#elif WINDOWS
        var view = (Microsoft.UI.Xaml.FrameworkElement)element.Handler!.PlatformView!;
        return view.IsLoaded && view.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
#else
        throw new PlatformNotSupportedException();
#endif
    }

    // Targets a coordinate in the native hierarchy, so an overlay must receive the action.
    // Android dispatches touch events; iOS/Windows invoke the native control found at that point.
    internal static void TapAt(VisualElement target)
    {
#if ANDROID
        var view = (Android.Views.View)target.Handler!.PlatformView!;
        // A modal page has its own native window. Dispatch through the target's
        // root so the touch reaches that window instead of the covered activity.
        var root = view.RootView!;
        int[] position = new int[2]; view.GetLocationOnScreen(position);
        int[] origin = new int[2]; root.GetLocationOnScreen(origin);
        var x = position[0] - origin[0] + view.Width / 2f; var y = position[1] - origin[1] + view.Height / 2f;
        var now = Android.OS.SystemClock.UptimeMillis();
        using var down = Android.Views.MotionEvent.Obtain(now, now, Android.Views.MotionEventActions.Down, x, y, 0);
        using var up = Android.Views.MotionEvent.Obtain(now, now + 10, Android.Views.MotionEventActions.Up, x, y, 0);
        root.DispatchTouchEvent(down); root.DispatchTouchEvent(up);
#elif IOS
        var view = (UIKit.UIView)target.Handler!.PlatformView!;
        var point = Bounds(target).Center;
        var hit = view.Window!.HitTest(new CoreGraphics.CGPoint(point.X, point.Y), null);
        while (hit != null && hit is not UIKit.UIControl) hit = hit.Superview;
        if (hit is not UIKit.UIControl control) throw new InvalidOperationException($"No native control at coverage point {point}; target={view.GetType().Name}, frame={view.Frame}, enabled={view.UserInteractionEnabled}, hidden={view.Hidden}, alpha={view.Alpha}, hit={view.Window.HitTest(new CoreGraphics.CGPoint(point.X, point.Y), null)?.GetType().Name}.");
        control.SendActionForControlEvents(UIKit.UIControlEvent.TouchUpInside);
#elif WINDOWS
        var view = (Microsoft.UI.Xaml.FrameworkElement)target.Handler!.PlatformView!;
        var root = (Microsoft.UI.Xaml.FrameworkElement)view.XamlRoot.Content;
        root.UpdateLayout();
        var point = Bounds(target).Center;
        var hits = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(new Windows.Foundation.Point(point.X, point.Y), root).ToArray();
        var hit = hits.FirstOrDefault();
        Microsoft.UI.Xaml.DependencyObject? current = hit;
        while (current != null && current is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        if (current is not Microsoft.UI.Xaml.FrameworkElement button)
        {
            var ancestors = new List<string>();
            for (Microsoft.UI.Xaml.DependencyObject? parent = view; parent != null;
                 parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent))
                if (parent is Microsoft.UI.Xaml.FrameworkElement element) ancestors.Add(Describe(element));
            throw new InvalidOperationException($"No native button at coverage point {point}; root={Describe(root)}; "
                + $"hits=[{string.Join("; ", hits.OfType<Microsoft.UI.Xaml.FrameworkElement>().Select(Describe))}]; "
                + $"target ancestry=[{string.Join("; ", ancestors)}].");
        }
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(button);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        static string Describe(Microsoft.UI.Xaml.FrameworkElement element)
        {
            var rect = element.TransformToVisual(null).TransformBounds(new(0, 0, element.ActualWidth, element.ActualHeight));
            return $"{element.GetType().Name}/{element.Name}: {rect}, hit={element.IsHitTestVisible}, visible={element.Visibility}, clip={element.Clip?.Rect}";
        }
#else
        throw new PlatformNotSupportedException();
#endif
    }

    internal static void CheckSafeArea(NavigationContext context, View footer, Action<bool, string> check)
    {
        var toolbar = Bounds(context.View.Toolbar); var bottom = Bounds(footer).Bottom;
#if ANDROID
        var view = (Android.Views.View)context.View.Handler!.PlatformView!;
        var activity = (Android.App.Activity)context.Window.Handler!.PlatformView!;
        var insets = AndroidX.Core.View.ViewCompat.GetRootWindowInsets(view)?.GetInsets(
            AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars() | AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout())
            ?? throw new InvalidOperationException("Native insets are unavailable.");
        var density = view.Resources!.DisplayMetrics!.Density;
        check(toolbar.Top >= insets.Top / density - 1, "toolbar clears Android status bar and cutout");
        check(bottom <= (activity.Window!.DecorView!.Height - insets.Bottom) / density + 1, "footer clears Android system navigation area");
#elif IOS
        var window = ((UIKit.UIView)context.View.Handler!.PlatformView!).Window!;
        check(toolbar.Top >= (double)window.SafeAreaInsets.Top - 1, "toolbar clears iOS safe-area top");
        check(bottom <= (double)(window.Bounds.Height - window.SafeAreaInsets.Bottom) + 1, "footer clears iOS home-indicator area");
#elif WINDOWS
        var root = (Microsoft.UI.Xaml.FrameworkElement)((Microsoft.UI.Xaml.FrameworkElement)context.View.Handler!.PlatformView!).XamlRoot.Content;
        check(toolbar.Top >= 0 && bottom <= root.ActualHeight + 1, "toolbar and footer remain inside Windows client area");
#endif
    }
}
