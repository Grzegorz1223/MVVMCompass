namespace MVVMCompass.Sample;

internal sealed partial class UnifiedNativeCoverage
{
    internal async Task UiRegressionsAsync()
    {
        Success(await Current.Navigation.SetRoot<FlyoutDemoViewModel>(), "UI: prepare flyout regression fixture");
        var root = Root;
        var owner = (FlyoutDemoView)root.Current!.View;
        var model = owner.ViewModel;
        Success(await model.Navigation.Select("personal"), "UI: select plain detail");
        var child = root.Current.Children!;
        var current = root.Deepest.Current!;
        var toolbar = root.View.Toolbar;
        var calls = 0;
        var refresh = new ToolbarButton { Icon = "oversize_refresh.png", AccessibilityLabel = "Refresh records", Command = new Command(() => calls++) };
        var phone = new ToolbarButton { Icon = "oversize_phone.png", AccessibilityLabel = "Connection status", Command = new Command(() => calls++) };
        var definition = new NavigationToolbarDefinition { Title = "INVOICES TO BE PROCESSED", RightItems = [refresh, phone] };
        current.View.Toolbar = definition;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(1, 0),
            GradientStops = [new(Colors.Orange, 0), new(Colors.Purple, 1)]
        };
        definition.Background = gradient;
        definition.ForegroundColor = Colors.White;
        await ToolbarStabilityAsync(toolbar, current.View, definition, refresh, phone);
        // Reproduce a primary-button style with dimensions, rounding and state colors.
        root.View.Resources.Add(new Style(typeof(Button)) { ApplyToDerivedTypes = true, Setters =
        {
            new() { Property = Button.CornerRadiusProperty, Value = 20 },
            new() { Property = Button.FontSizeProperty, Value = 22d },
            new() { Property = View.MarginProperty, Value = new Thickness(7) },
            new() { Property = VisualElement.HeightRequestProperty, Value = 88d },
            new() { Property = VisualElement.MinimumWidthRequestProperty, Value = 160d },
            new() { Property = VisualElement.BackgroundColorProperty, Value = Colors.Purple },
            new() { Property = VisualStateManager.VisualStateGroupsProperty, Value = new VisualStateGroupList
            { new VisualStateGroup { Name = "CommonStates", States =
            {
                new VisualState { Name = "Normal" },
                new VisualState { Name = "Disabled", Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = Colors.Red } } }
            } } } }
        } });
        toolbar.HorizontalOptions = LayoutOptions.Center;
        foreach (var width in new[] { 320d, 360d })
        {
            toolbar.WidthRequest = width;
            await Ready(toolbar); await Task.Delay(180);
            var bounds = NativeCoveragePlatform.Bounds(toolbar);
            var title = Descendants(toolbar).OfType<Label>().Single(label => label.Text == definition.Title);
            var paint = NativeCoveragePlatform.Bounds(toolbar.Content);
            Check(Math.Abs(paint.Left - bounds.Left) <= 1 && Math.Abs(paint.Top - bounds.Top) <= 1
                && Math.Abs(paint.Right - bounds.Right) <= 1 && Math.Abs(paint.Bottom - bounds.Bottom) <= 1,
                $"UI: {width} DIP toolbar background covers all padding; bar={bounds}, background={paint}");
            Check(ReferenceEquals(toolbar.EffectiveBackground, gradient) && title.TextColor == Colors.White,
                "UI: screen background and default title foreground are resolved");
            var icons = Descendants(toolbar).OfType<Image>().ToArray();
            Check(bounds.Height <= 60, $"UI: {width} DIP toolbar stays compact with 100x100 and 68x112 SVGs; bounds={bounds}");
            Check(icons.Length == 2 && icons.All(icon => NativeCoveragePlatform.Bounds(icon).Width <= 25 && NativeCoveragePlatform.Bounds(icon).Height <= 25),
                $"UI: {width} DIP toolbar constrains intrinsic asset dimensions");
            Check(NativeCoveragePlatform.Bounds(title).Width >= 150, $"UI: {width} DIP toolbar reclaims title space; title={NativeCoveragePlatform.Bounds(title)}");
            foreach (var input in Descendants(toolbar).OfType<Button>().Where(button => button.BindingContext is ToolbarButton))
            {
                var rect = NativeCoveragePlatform.Bounds(input);
                Check(rect.Width >= 44 && rect.Height >= 44 && rect.Right <= bounds.Right + 1, $"UI: complete 44 DIP action target; {rect}");
                Check(!string.IsNullOrEmpty(NativeCoveragePlatform.Label(input)), "UI: icon-only action has a native accessibility name");
            }
            await CaptureUiAsync("toolbar-gradient-" + width);
        }
        await CaptureUiAsync("toolbar-compact");
        definition.Title = "Short";
        await Task.Delay(150);
        var centered = Descendants(toolbar).OfType<Label>().Single(label => label.Text == "Short");
        Check(Math.Abs(NativeCoveragePlatform.Bounds(centered).Center.X - NativeCoveragePlatform.Bounds(toolbar).Center.X) <= 2,
            "UI: short title remains geometrically centered when space permits");

        // Icon+text and text-only actions retain native text and command behavior.
        refresh.Text = "Reload";
        phone.Icon = null; phone.Text = "Status";
        await Task.Delay(150);
        var combined = Descendants(toolbar).OfType<Button>().Single(button => button.Text == "Reload");
        var actionHandler = combined.Handler;
        var actionCommand = combined.Command;
        definition.ForegroundColor = Colors.Yellow;
        await Task.Delay(100);
        Check(ReferenceEquals(combined.Handler, actionHandler) && ReferenceEquals(combined.Command, actionCommand)
            && combined.TextColor == Colors.Yellow, "UI: live foreground retains the native action and guarded command");
        definition.ForegroundColor = Colors.White;
        Check(NativeCoveragePlatform.Bounds(combined).Width >= 90, "UI: combined action reserves icon and text space");
        Check(NativeCoveragePlatform.Enabled(combined) && combined.Command!.CanExecute(null), "UI: combined action is enabled before input");
        NativeCoveragePlatform.TapAt(combined);
        await Until(() => calls > 0);
        Check(calls == 1, "UI: combined action receives native input exactly once");
        toolbar.FlowDirection = FlowDirection.RightToLeft;
        await Task.Delay(150);
        var combinedIcon = Descendants((Element)combined.Parent).OfType<Image>().Single();
        Check(NativeCoveragePlatform.Bounds(combinedIcon).Center.X > NativeCoveragePlatform.Bounds(combined).Center.X && combined.Padding.Right >= 38,
            "UI: combined action mirrors its icon and text padding in right-to-left layout");
        toolbar.FlowDirection = FlowDirection.LeftToRight;
        var items = definition.RightItems!;
        for (var index = 0; index < 8; index++) items.Add(new() { Text = "Action " + index, Command = new Command(() => calls++) });
        await Task.Delay(180);
        var actionScroll = Descendants(toolbar).OfType<ScrollView>().Single();
        Check(actionScroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Always, "UI: action overflow enables the native scroll indicator");
        // Automatic edge anchoring may already have reached this native position.
        // Bound the completion wait; the actual viewport and input are checked below.
        var scroll = actionScroll.ScrollToAsync(actionScroll.Content.Width, 0, false);
        await Task.WhenAny(scroll, Task.Delay(1500));
        if (scroll.IsCompleted) await scroll;
        await Task.Delay(150);
        var lastAction = Descendants(toolbar).OfType<Button>().Single(button => button.Text == "Action 7");
        var lastBounds = NativeCoveragePlatform.Bounds(lastAction);
        var scrollBounds = NativeCoveragePlatform.Bounds(actionScroll);
        Check(lastBounds.Left >= scrollBounds.Left - 2 && lastBounds.Right <= scrollBounds.Right + 2, "UI: last overflow action can be brought fully into view");
        NativeCoveragePlatform.TapAt(lastAction);
        await Until(() => calls > 1);
        Check(calls == 2, "UI: last overflow action receives native input");

        definition.Title = "INVOICES TO BE PROCESSED";
        definition.RightItems = [refresh, phone];
        refresh.Text = ""; phone.Text = ""; phone.Icon = "oversize_phone.png";
        await Until(() => Descendants(toolbar).OfType<Button>().Count(button => button.BindingContext is ToolbarButton && button.IsVisible) == 2);
        await Task.Delay(80);
        Check(Math.Abs(actionScroll.ScrollX) <= 1, "UI: replacing overflow actions resets the persistent scroller for non-overflow content");
        toolbar.WidthRequest = -1; toolbar.HorizontalOptions = LayoutOptions.Fill;
        owner.SharedContent = null;
        owner.FlyoutHeaderContent = new Label { Text = "Navigation", FontSize = 24, Padding = 16 };
        owner.FlyoutFooterContent = new Label { Text = "Drawer footer", Padding = 16 };
        owner.FlyoutListPadding = 0;
        owner.FlyoutItemSpacing = 0;
        owner.FlyoutScrollBarVisibility = ScrollBarVisibility.Always;
        var template = new DataTemplate(() =>
        {
            var label = new Label { HeightRequest = 64, Padding = new Thickness(16, 0), VerticalTextAlignment = TextAlignment.Center, BackgroundColor = Colors.LightBlue };
            label.SetBinding(Label.TextProperty, nameof(NavigationItemContext.Title));
            return label;
        });
        owner.SelectedFlyoutItemTemplate = owner.UnselectedFlyoutItemTemplate = template;
        Success(await model.ReplaceItems([new(typeof(DocumentViewModel), "Personal", "personal"),
            .. Enumerable.Range(0, 18).Select(index => new NavigationItem(typeof(DocumentViewModel), "Destination " + index, "item-" + index))]),
            "UI: populate scrollable flyout");
        var entry = new Entry { Text = "Background editor" };
        current.View.Content = entry;
        await Ready(entry);
        entry.Focus();
        await Task.Delay(100);
        child.SetFlyout(true);
        var menu = Descendants(root.View).OfType<NavigationSelector>().Single(selector => ReferenceEquals(selector.ItemsSource, child.MenuItems));
        await Ready(menu); await Task.Delay(180);
        var scrim = Descendants(root.View).OfType<Button>().Single(button => SemanticProperties.GetDescription(button) == "Close menu");
        var scrimBounds = NativeCoveragePlatform.Bounds(scrim);
        var hostBounds = NativeCoveragePlatform.Bounds(root.View);
        Check(Math.Abs(scrimBounds.Top - hostBounds.Top) <= 1 && Math.Abs(scrimBounds.Height - hostBounds.Height) <= 1,
            $"UI: drawer scrim covers toolbar and body; scrim={scrimBounds}, host={hostBounds}");
        Check(!entry.IsFocused && !entry.IsEnabled, "UI: background editor loses focus and is disabled while drawer is open");
        var toolbarAction = Descendants(toolbar).OfType<Button>().Single(button => SemanticProperties.GetDescription(button) == "Connection status");
        Check(!NativeCoveragePlatform.Enabled(toolbarAction) && !toolbarAction.Command!.CanExecute(null), "UI: background toolbar is disabled for native and command input");
        var firstInput = Descendants(menu).OfType<Button>().Single(button => button.AutomationId == "destination-personal");
        Check(firstInput.CornerRadius == 0 && firstInput.Margin == new Thickness(0) && NativeCoveragePlatform.Bounds(firstInput).Height <= 65,
            "UI: selector geometry ignores the application's primary-button style");
        Check(Math.Abs(NativeCoveragePlatform.Bounds(firstInput).Left - NativeCoveragePlatform.Bounds(menu).Left) <= 1,
            "UI: zero list padding removes the leading selection inset");
        Check(scrim.CornerRadius == 0 && scrim.Margin == new Thickness(0), "UI: scrim has square edges without style margins");
        var background = scrim.BackgroundColor;
        scrim.IsEnabled = false;
        await Task.Delay(50);
        Check(scrim.BackgroundColor == background, "UI: primary-button disabled colors do not repaint the scrim");
        scrim.IsEnabled = true;
        await CaptureUiAsync("drawer-open");
        // This coordinate belongs to the background toolbar; the scrim must receive it.
        NativeCoveragePlatform.TapAt(toolbarAction);
        await Until(() => !child.IsFlyoutOpen);
        Check(calls == 2, "UI: tapping a covered toolbar action closes the drawer without executing it");
        Check(entry.IsEnabled && toolbarAction.Command!.CanExecute(null), "UI: closing the drawer restores background input");
        child.SetFlyout(true);
        await Ready(menu); await Task.Delay(100);
        var listScroll = (ScrollView)menu.Content;
        Check(listScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Always, "UI: public flyout scrollbar policy reaches the native list");
        await listScroll.ScrollToAsync(0, listScroll.Content.Height, false).WaitAsync(TimeSpan.FromSeconds(12));
        await Task.Delay(150);
        var finalItem = Descendants(menu).OfType<Button>().Single(button => button.AutomationId == "destination-item-17");
        var itemBounds = NativeCoveragePlatform.Bounds(finalItem);
        var listBounds = NativeCoveragePlatform.Bounds(listScroll);
        Check(itemBounds.Top >= listBounds.Top - 2 && itemBounds.Bottom <= listBounds.Bottom + 2, "UI: final drawer destination is reachable by scrolling");
        NativeCoveragePlatform.TapAt(finalItem);
        await Until(() => child.SelectedDestinationId == "item-17" && !child.IsFlyoutOpen && !root.IsNavigating);
        Check(!ReferenceEquals(root.Deepest.Current, current), "UI: scrolled drawer input selects the intended destination");
        Success(await model.Navigation.Select("personal"), "UI: restore original destination");
        Check(ReferenceEquals(root.Deepest.Current, current), "UI: drawer navigation retains the original detail and history");
        await TrailingContentUiAsync(root, owner);
        Success(await Current.Navigation.SetRoot<WelcomeViewModel>(), "UI: restore sample after regressions");
    }

    private async Task ToolbarStabilityAsync(NavigationToolbar toolbar, ViewBase screen,
        NavigationToolbarDefinition definition, ToolbarButton first, ToolbarButton second)
    {
        var originalTitle = definition.Title;
        definition.Title = "Stable";
        first.Text = second.Text = "";
        first.IsVisible = second.IsVisible = true;

        foreach (var custom in new[] { false, true })
        {
            Grid? customCenter = null;
            if (custom)
            {
                screen.ToolbarLeadingTemplate = new DataTemplate(() =>
                {
                    var button = StableButton("stable-leading");
                    button.SetBinding(Button.CommandProperty, nameof(NavigationToolbar.LeadingCommand));
                    button.SetBinding(SemanticProperties.DescriptionProperty, nameof(NavigationToolbar.LeadingText));
                    return button;
                });
                definition.RightItemTemplate = new DataTemplate(() =>
                {
                    var button = StableButton();
                    button.Text = "•";
                    button.SetBinding(Button.CommandProperty, nameof(ToolbarButton.Command));
                    button.SetBinding(Button.CommandParameterProperty, nameof(ToolbarButton.CommandParameter));
                    button.SetBinding(SemanticProperties.DescriptionProperty, nameof(ToolbarButton.AccessibilityLabel));
                    return button;
                });
                customCenter = new Grid { AutomationId = "stable-center", HeightRequest = 28 };
                customCenter.Add(new Label { Text = "Line one\nLine two", FontSize = 10,
                    HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center });
                definition.CenterContent = customCenter;
            }
            else
            {
                screen.ToolbarLeadingTemplate = null;
                definition.RightItemTemplate = null;
                definition.CenterContent = null;
            }

            foreach (var direction in new[] { FlowDirection.LeftToRight, FlowDirection.RightToLeft })
            foreach (var width in new[] { 320d, 360d })
            {
                toolbar.FlowDirection = direction;
                toolbar.HorizontalOptions = LayoutOptions.Center;
                toolbar.WidthRequest = width;
                second.IsVisible = false;
                await Until(() => StableActions(toolbar).Length == 1);
                await Task.Delay(80);
                var baseline = Snapshot();
                var maximumLeadingDelta = 0d;
                var maximumTrailingDelta = 0d;
                var maximumCenterDelta = Math.Abs(baseline.Center - baseline.ToolbarCenter);
                var minimumTarget = Math.Min(baseline.LeadingSize, baseline.ActionSize);

                for (var cycle = 0; cycle < 50; cycle++)
                {
                    second.IsVisible = true;
                    await Until(() => StableActions(toolbar).Length == 2);
                    await Task.Delay(16);
                    Record(Snapshot());
                    second.IsVisible = false;
                    await Until(() => StableActions(toolbar).Length == 1);
                    await Task.Delay(16);
                    Record(Snapshot());
                }

                var mode = custom ? "custom" : "default";
                Check(ReferenceEquals(toolbar, Root.View.Toolbar) && maximumLeadingDelta <= 1 && maximumTrailingDelta <= 1,
                    $"UI: {mode} {direction} {width} DIP toolbar edges stay fixed through 50 A/B/A cycles " +
                    $"(leading {maximumLeadingDelta:F2}, trailing {maximumTrailingDelta:F2})");
                Check(maximumCenterDelta <= 1 && minimumTarget >= 43.5,
                    $"UI: {mode} {direction} {width} DIP center and targets remain stable " +
                    $"(center {maximumCenterDelta:F2}, target {minimumTarget:F2})");

                void Record(ToolbarBounds sample)
                {
                    maximumLeadingDelta = Math.Max(maximumLeadingDelta, Math.Abs(sample.Leading - baseline.Leading));
                    maximumTrailingDelta = Math.Max(maximumTrailingDelta, Math.Abs(sample.Trailing - baseline.Trailing));
                    maximumCenterDelta = Math.Max(maximumCenterDelta, Math.Abs(sample.Center - sample.ToolbarCenter));
                    minimumTarget = Math.Min(minimumTarget, Math.Min(sample.LeadingSize, sample.ActionSize));
                }

                ToolbarBounds Snapshot()
                {
                    var bar = NativeCoveragePlatform.Bounds(toolbar);
                    var leading = custom
                        ? Descendants(toolbar).OfType<Button>().Single(button => button.AutomationId == "stable-leading")
                        : Descendants(toolbar).OfType<Button>().Single(button => button.BindingContext is not ToolbarButton);
                    var leadingBounds = NativeCoveragePlatform.Bounds(leading);
                    var actionBounds = StableActions(toolbar).Select(NativeCoveragePlatform.Bounds).ToArray();
                    var center = custom ? NativeCoveragePlatform.Bounds(customCenter!)
                        : NativeCoveragePlatform.Bounds(Descendants(toolbar).OfType<Label>().Single(label => label.Text == "Stable"));
                    return direction == FlowDirection.RightToLeft
                        ? new(leadingBounds.Right, actionBounds.Min(item => item.Left), center.Center.X, bar.Center.X,
                            Math.Min(leadingBounds.Width, leadingBounds.Height), actionBounds.Min(item => Math.Min(item.Width, item.Height)))
                        : new(leadingBounds.Left, actionBounds.Max(item => item.Right), center.Center.X, bar.Center.X,
                            Math.Min(leadingBounds.Width, leadingBounds.Height), actionBounds.Min(item => Math.Min(item.Width, item.Height)));
                }
            }
        }

        screen.ToolbarLeadingTemplate = null;
        definition.RightItemTemplate = null;
        definition.CenterContent = null;
        definition.Title = originalTitle;
        first.IsVisible = second.IsVisible = true;
        toolbar.FlowDirection = FlowDirection.LeftToRight;
        toolbar.WidthRequest = -1;
        toolbar.HorizontalOptions = LayoutOptions.Fill;

        static Button StableButton(string? automationId = null) => new()
        {
            AutomationId = automationId,
            WidthRequest = 44,
            HeightRequest = 44,
            MinimumWidthRequest = 44,
            MinimumHeightRequest = 44,
            Padding = 0,
            Margin = 0,
            CornerRadius = 0,
            BorderWidth = 0,
            BackgroundColor = Colors.Transparent,
            Resources = new ResourceDictionary { new Style(typeof(Button)) }
        };

        static Button[] StableActions(NavigationToolbar toolbar) => Descendants(toolbar).OfType<Button>()
            .Where(button => button.BindingContext is ToolbarButton && button.IsVisible).ToArray();
    }

    private readonly record struct ToolbarBounds(double Leading, double Trailing, double Center, double ToolbarCenter,
        double LeadingSize, double ActionSize);

    private async Task TrailingContentUiAsync(NavigationContext root, FlyoutDemoView owner)
    {
        var model = owner.ViewModel;
        var child = root.Current!.Children!;
        root.View.HeightRequest = 360;
        root.View.VerticalOptions = LayoutOptions.Start;
        root.View.HorizontalOptions = LayoutOptions.Start;
        var aboutCalls = 0;
        Task? aboutCompletion = null;
        var about = new Button
        {
            Text = "À propos", MinimumHeightRequest = 44, Padding = 12, Margin = 0,
            Resources = new ResourceDictionary { new Style(typeof(Button)) }
        };
        about.Command = new Command(() => aboutCompletion = OpenAbout());
        async Task OpenAbout()
        {
            aboutCalls++;
            Success(await model.Navigation.CloseFlyout(), "UI: About closes drawer through the public scoped API");
            await Current.Navigation.DisplayPopup<DemoPopupViewModel, string>();
        }
        var editor = new Entry { Placeholder = "Drawer editor", HeightRequest = 44 };
        var trailing = new VerticalStackLayout { Spacing = 0, Children = { about, editor } };
        var footer = new Label { Text = "Version 1.1.0-dev.3", Padding = 8 };
        owner.FlyoutHeaderContent = null;
        owner.FlyoutFooterContent = footer;
        owner.FlyoutTrailingContent = trailing;
        owner.FlyoutItemSpacing = 4;
        owner.FlyoutListPadding = new(8, 4, 8, 4);
        var parentChanges = 0;
        Element? parent = null;
        foreach (var count in new[] { 6, 4, 0, 6 })
        {
            root.View.WidthRequest = count == 4 ? 320 : 360;
            Success(await model.ReplaceItems(Enumerable.Range(0, 6).Select(index =>
                new NavigationItem(typeof(DocumentViewModel), "Destination " + index, "trailing-" + index) { IsVisible = index < count })),
                $"UI: configure {count} visible destinations");
            root.View.Toolbar.LeadingCommand.Execute(null);
            await Until(() => child.IsFlyoutOpen);
            await Ready(trailing); await Task.Delay(160);
            var menu = Descendants(root.View).OfType<NavigationSelector>().Single(selector => ReferenceEquals(selector.ItemsSource, child.MenuItems));
            var scroll = (ScrollView)menu.Content;
            if (parent == null) { parent = trailing.Parent; trailing.ParentChanged += (_, _) => parentChanges++; }
            Check(ReferenceEquals(parent, trailing.Parent) && ReferenceEquals(trailing.BindingContext, model),
                "UI: trailing content retains its native parent and container model");
            Check(Grid.GetRow(trailing) == count && ReferenceEquals(scroll.Content, trailing.Parent),
                $"UI: trailing content is row {count} inside the destination scroller");
            await scroll.ScrollToAsync(trailing, ScrollToPosition.End, false).WaitAsync(TimeSpan.FromSeconds(12));
            await Task.Delay(100);
            var trailingBounds = NativeCoveragePlatform.Bounds(trailing);
            var scrollBounds = NativeCoveragePlatform.Bounds(scroll);
            var hostBounds = NativeCoveragePlatform.Bounds(root.View);
            Check(hostBounds.Width <= 361 && hostBounds.Height <= 361, $"UI: compact drawer viewport; {hostBounds}");
            Check(trailingBounds.Top >= scrollBounds.Top - 2 && trailingBounds.Bottom <= scrollBounds.Bottom + 2,
                $"UI: About and editor are reachable with {count} visible destinations");
            if (count > 0)
            {
                var final = Descendants(menu).OfType<Button>().Single(button => button.AutomationId == "destination-trailing-" + (count - 1));
                Check(Math.Abs(trailingBounds.Top - NativeCoveragePlatform.Bounds(final).Bottom - 4) <= 2,
                    "UI: About immediately follows the last destination with the configured gap");
            }
            Check(NativeCoveragePlatform.Bounds(footer).Top >= scrollBounds.Bottom - 1,
                "UI: version footer remains below the scrolling list");
            Check(Math.Abs(NativeCoveragePlatform.Bounds(footer).Bottom - hostBounds.Bottom) <= 1,
                "UI: version footer stays at the compact panel bottom");
            await CaptureUiAsync("trailing-" + count);
            Success(await model.Navigation.CloseFlyout(), "UI: close trailing-content drawer");
        }
        Check(parentChanges == 0, "UI: visibility and membership updates preserve the trailing native parent");
        root.View.Toolbar.LeadingCommand.Execute(null);
        await Until(() => child.IsFlyoutOpen);
        var activeMenu = Descendants(root.View).OfType<NavigationSelector>().Single(selector => ReferenceEquals(selector.ItemsSource, child.MenuItems));
        await ((ScrollView)activeMenu.Content).ScrollToAsync(trailing, ScrollToPosition.End, false).WaitAsync(TimeSpan.FromSeconds(12));
        await Task.Delay(160);
        editor.Focus(); await Task.Delay(150);
        Success(await model.Navigation.CloseFlyout(), "UI: close drawer with focused editor");
        Check(!editor.IsFocused, "UI: closing the drawer releases editable trailing focus");
        root.View.Toolbar.LeadingCommand.Execute(null);
        await Until(() => child.IsFlyoutOpen);
        await ((ScrollView)activeMenu.Content).ScrollToAsync(trailing, ScrollToPosition.End, false).WaitAsync(TimeSpan.FromSeconds(12));
        await Task.Delay(160);
        NativeCoveragePlatform.TapAt(about);
        await Until(() => host.Window.Navigation.ModalStack.Count != 0);
        var popup = Descendants(host.Window.Navigation.ModalStack[^1]).OfType<DemoPopupView>().Single().ViewModel;
        Check(aboutCalls == 1 && !child.IsFlyoutOpen, "UI: trailing About command opens one scoped popup after closing the drawer");
        Success(await popup.Navigation.ClosePopup(), "UI: close About popup");
        await aboutCompletion!.WaitAsync(TimeSpan.FromSeconds(10));
        owner.FlyoutTrailingContent = null;
        Check(trailing.Parent == null, "UI: clearing trailing content detaches it from the native list");
        root.View.WidthRequest = root.View.HeightRequest = -1;
        root.View.HorizontalOptions = root.View.VerticalOptions = LayoutOptions.Fill;
    }

    private static async Task CaptureUiAsync(string name)
    {
        var result = await Screenshot.Default.CaptureAsync();
        var directory = FileSystem.AppDataDirectory;
#if ANDROID
        directory = Android.App.Application.Context.GetExternalFilesDir(null)!.AbsolutePath;
#endif
        await using var output = File.Create(Path.Combine(directory, "ui-" + name + ".png"));
        await result.CopyToAsync(output, ScreenshotFormat.Png);
    }
}
