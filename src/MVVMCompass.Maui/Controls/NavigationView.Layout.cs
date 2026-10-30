namespace MVVMCompass;

internal sealed partial class NavigationView
{
    internal ControlTemplate? BodyTemplate { get; set; }
    internal Thickness DetailMargin { get; set; }
    private ContentView? bodyFrame;
    internal View? TabBarContent { get; set; } = null;
    internal bool IsTabBarVisible { get; set; } = true;
    internal bool IsTabBarEnabled { get; set; } = true;
    internal double TabBarHeight { get; set; } = -1d;
    internal View? LeadingContent { get; set; } = null;
    internal GridLength LeadingContentWidth { get; set; } = GridLength.Auto;
    internal bool IsLeadingContentVisible { get; set; } = true;
    internal View? BodyRowOverlayContent { get; set; } = null;
    private Grid? bodyRow;
    private ContentView? leadingHost, bodyRowOverlayHost, customTabBarHost;

    private void RefreshBodyLayout()
    {
        if (bodyFrame != null && BodyTemplate == null)
        {
            // Clearing a MAUI template removes its visual children. Restore the
            // original body explicitly so undocking cannot leave an empty host.
            bodyFrame.ControlTemplate = null;
            bodyFrame.Content = null;
            chrome.Remove(bodyFrame);
            bodyFrame = null;
            chrome.Add(body, 0, 2);
        }
        // Ordinary containers keep their original layout. The app supplies frame styling
        // through a standard MAUI template, without gaining ownership of the body.
        if (bodyFrame == null && BodyTemplate != null)
        {
            chrome.Remove(body);
            bodyFrame = new ContentView { Content = body };
            chrome.Add(bodyFrame, 0, 2);
        }
        if (bodyFrame != null) bodyFrame.ControlTemplate = BodyTemplate;
        Presenter.Margin = DetailMargin;
        if (bodyRow == null && (LeadingContent != null || BodyRowOverlayContent != null))
        {
            leadingHost = new ContentView();
            bodyRowOverlayHost = new ContentView();
            bodyRow = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
            detail.Remove(bodyLayers);
            bodyRow.Add(leadingHost, 0, 0); bodyRow.Add(bodyLayers, 1, 0);
            bodyRow.Add(bodyRowOverlayHost, 0, 0); Grid.SetColumnSpan(bodyRowOverlayHost, 2);
            detail.Add(bodyRow, 0, 1);
        }
        if (bodyRow == null) return;
        var visible = IsLeadingContentVisible && LeadingContent != null;
        leadingHost!.Content = LeadingContent; leadingHost.IsVisible = visible;
        bodyRow.ColumnDefinitions[0].Width = visible ? LeadingContentWidth : new GridLength(0);
        bodyRowOverlayHost!.Content = BodyRowOverlayContent;
        bodyRowOverlayHost.IsVisible = BodyRowOverlayContent != null;
        bodyRowOverlayHost.InputTransparent = BodyRowOverlayContent == null;
    }

    private void RefreshTabLayout(TabBarPosition position, bool hasTabs)
    {
        if (customTabBarHost == null && TabBarContent != null)
        { customTabBarHost = new ContentView(); body.Add(customTabBarHost); }
        if (customTabBarHost == null) return;
        customTabBarHost.Content = TabBarContent;
        customTabBarHost.IsVisible = hasTabs && TabBarContent != null;
        customTabBarHost.HeightRequest = position is TabBarPosition.Top or TabBarPosition.Bottom ? TabBarHeight : -1;
        Grid.SetRow(customTabBarHost, position == TabBarPosition.Bottom ? 2 : position == TabBarPosition.Top ? 0 : 1);
        Grid.SetColumn(customTabBarHost, position == TabBarPosition.Left ? 0 : position == TabBarPosition.Right ? 2 : 1);
    }

    private void ClearLayoutContent()
    {
        BodyTemplate = null;
        if (bodyFrame != null) bodyFrame.ControlTemplate = null;
        TabBarContent = null; LeadingContent = null; BodyRowOverlayContent = null;
        if (leadingHost != null) leadingHost.Content = null;
        if (bodyRowOverlayHost != null) bodyRowOverlayHost.Content = null;
        if (customTabBarHost != null) customTabBarHost.Content = null;
    }
}
