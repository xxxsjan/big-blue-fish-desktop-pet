using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

// Positions the visible character at the monitor edge. Normal mode keeps a separate bubble window;
// transparent-capture mode embeds the bubble in the pet HWND so OBS captures both.
public static class CompanionEdgeLayout
{
    private sealed class Follow
    {
        private readonly Window pet;
        private readonly Border bubble;
        private readonly System.Windows.Controls.TextBlock text;
        private readonly System.Windows.Controls.TextBlock measuringText = new();
        private readonly DependencyPropertyDescriptor textChange;
        private Window? popup;
        private bool embedded;
        private bool updating;
        private bool closed;
        private bool queued;
        // Screen Y of the window bottom while the embedded bubble is shown — keeps the sprite fixed
        // as the bubble grows upward (absolute-position feel).
        private double? embeddedFoot;
        private double? embeddedCenterX;
        private readonly DependencyPropertyDescriptor visibility;
        public Follow(Window w)
        {
            pet = w;
            bubble = ((BigBlueFish.Windows.MainWindow)w).CompanionBubble;
            text = ((BigBlueFish.Windows.MainWindow)w).CompanionBubbleText;
            textChange = DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.TextBlock.TextProperty, typeof(System.Windows.Controls.TextBlock));
            textChange.AddValueChanged(text, Changed);
            visibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Border));
            visibility.AddValueChanged(bubble, Changed);
            pet.Loaded += Loaded;
            pet.LocationChanged += Moved;
            pet.SizeChanged += Changed;
            pet.IsVisibleChanged += Visible;
            pet.DpiChanged += DpiChanged;
            pet.Closed += Closed;
            bubble.SizeChanged += Changed;
            if (pet.IsLoaded)
            {
                SyncHost();
            }
        }

        private void Loaded(object s, RoutedEventArgs e)
        {
            SyncHost();
            Update();
        }

        private void Moved(object? s, EventArgs e)
        {
            // Drag moves the sprite; re-lock the foot so later bubble growth still expands upward only.
            if (!updating && embedded && bubble.Visibility == Visibility.Visible)
            {
                embeddedFoot = pet.Top + pet.Height;
                embeddedCenterX = pet.Left + pet.Width / 2.0;
            }

            Changed(s, e);
        }

        private void Changed(object? s, EventArgs e)
        {
            Update();
            QueueUpdate();
        }

        private void QueueUpdate()
        {
            if (!queued && !closed)
            {
                queued = true;
                pet.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                {
                    queued = false;
                    Update();
                }));
            }
        }

        private void Visible(object s, DependencyPropertyChangedEventArgs e)
        {
            Update();
        }

        private void DpiChanged(object s, System.Windows.DpiChangedEventArgs e)
        {
            Clamp(pet);
        }

        private void Closed(object? s, EventArgs e)
        {
            closed = true;
            visibility.RemoveValueChanged(bubble, Changed);
            textChange.RemoveValueChanged(text, Changed);
            if (popup != null)
            {
                popup.Content = null;
                popup.Close();
            }
        }

        private bool CaptureListed => ((BigBlueFish.Windows.MainWindow)pet).LiveCaptureListed;

        private void SyncHost()
        {
            if (closed)
                return;
            if (CaptureListed)
                EmbedInPet();
            else
                DetachToPopup();
        }

        private void EmbedInPet()
        {
            if (popup != null)
            {
                popup.Content = null;
                popup.Hide();
                popup.Close();
                popup = null;
            }

            var host = (BigBlueFish.Windows.MainWindow)pet;
            var layer = host.CompanionBubbleLayer;
            if (bubble.Parent is System.Windows.Controls.Panel current && !ReferenceEquals(current, layer))
                current.Children.Remove(bubble);
            if (bubble.Parent == null)
                layer.Children.Add(bubble);
            // Absolute positioning on the overlay canvas — never laid out with the sprite layer.
            bubble.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            bubble.VerticalAlignment = VerticalAlignment.Top;
            bubble.Margin = new Thickness(0);
            embedded = true;
            EnsureCaptureFrame();
        }

        // Non-capture path mirrors upstream Bai-Cute: a separate no-activate tool window for the bubble.
        private void DetachToPopup()
        {
            if (popup != null)
            {
                embedded = false;
                if (pet.IsLoaded && popup.Owner == null)
                    popup.Owner = pet;
                ApplyPopupStyle();
                RestorePetBaseSize();
                return;
            }

            if (bubble.Parent is System.Windows.Controls.Panel parent)
                parent.Children.Remove(bubble);
            Canvas.SetLeft(bubble, 0);
            Canvas.SetTop(bubble, 0);
            bubble.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            bubble.VerticalAlignment = VerticalAlignment.Stretch;
            bubble.Margin = new Thickness(0);
            popup = new Window
            {
                Title = "蓝色大肥鱼 · 对话",
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = true,
                Content = bubble
            };
            // WPF forbids Owner before the pet has been shown; attach once Loaded.
            if (pet.IsLoaded)
                popup.Owner = pet;
            popup.SourceInitialized += (object? s, EventArgs e) =>
            {
                nint handle = new WindowInteropHelper(popup).Handle;
                // 0x8000080 = WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW (same as upstream).
                SetWindowLongPtr(handle, -20, new IntPtr(((IntPtr)GetWindowLongPtr(handle, -20)).ToInt64() | 0x8000080));
                HwndSource.FromHwnd(handle)?.AddHook(NoActivate);
            };
            popup.DpiChanged += (object s, System.Windows.DpiChangedEventArgs e) =>
            {
                pet.Dispatcher.BeginInvoke(new Action(Update), DispatcherPriority.Loaded);
            };
            embedded = false;
            RestorePetBaseSize();
        }

        private void ApplyPopupStyle()
        {
            if (popup == null)
                return;
            popup.ShowInTaskbar = false;
            popup.Title = "蓝色大肥鱼 · 对话";
            nint handle = new WindowInteropHelper(popup).Handle;
            if (handle == 0)
                return;
            // Match upstream bubble chrome exactly.
            SetWindowLongPtr(handle, -20, new IntPtr(((IntPtr)GetWindowLongPtr(handle, -20)).ToInt64() | 0x8000080));
        }

        public void ApplyCaptureMode()
        {
            SyncHost();
            Update();
        }

        public bool LocksPetFoot => embedded && embeddedFoot is not null;

        // Fixed top padding in capture mode so OBS (top-left anchored) never sees the window
        // height change while text grows — the sprite stays at a constant offset from the HWND top.
        // Keep this modest: too large pushes the bottom-docked sprite off-screen at the default corner.
        private const double EmbeddedBubbleReserve = 140;
        private const double EmbeddedBubbleGap = 10;

        private static nint NoActivate(nint h, int msg, nint wp, nint lp, ref bool handled)
        {
            if (msg == 33)
            {
                handled = true;
                return new IntPtr(3);
            }

            return IntPtr.Zero;
        }

        private void CaptureFrameMetrics(out double baseWidth, out double baseHeight, out double spaceBelowHead, out double frameWidth, out double frameHeight, out double scale)
        {
            scale = ((BigBlueFish.Windows.MainWindow)pet).CompanionBubbleScale;
            double sprite = SpriteSize(pet);
            baseWidth = sprite * 640 / 360 + 20;
            baseHeight = sprite + 35;
            double headTopInBase = 35 + sprite * 58 / 360;
            spaceBelowHead = baseHeight - headTopInBase;
            frameWidth = Math.Max(baseWidth, 300 * scale + 16);
            frameHeight = EmbeddedBubbleReserve + spaceBelowHead;
        }

        private void EnsureCaptureFrame()
        {
            CaptureFrameMetrics(out double baseWidth, out double baseHeight, out _, out double frameWidth, out double frameHeight, out _);
            var spriteLayer = ((BigBlueFish.Windows.MainWindow)pet).CompanionSpriteLayer;
            spriteLayer.Width = baseWidth;
            spriteLayer.Height = baseHeight;
            spriteLayer.VerticalAlignment = VerticalAlignment.Bottom;
            // Grow/shrink from the current sprite foot so the character stays put on screen.
            double foot = pet.Top + pet.Height;
            double centerX = pet.Left + pet.Width / 2.0;
            if (Math.Abs(pet.Width - frameWidth) > 0.5 || Math.Abs(pet.Height - frameHeight) > 0.5)
            {
                pet.Width = frameWidth;
                pet.Height = frameHeight;
                pet.Top = foot - frameHeight;
                pet.Left = centerX - frameWidth / 2.0;
            }

            // Clamp by the visible character, not the tall transparent chrome — otherwise a
            // bottom-right restore places the sprite below the work area.
            Rect area = WorkArea(pet);
            Rect petBounds = PetBounds(pet);
            pet.Left = Limit(pet.Left, area.Left - petBounds.Left, area.Right - petBounds.Right);
            pet.Top = Limit(pet.Top, area.Top - petBounds.Top, area.Bottom - petBounds.Bottom);
            embeddedFoot = pet.Top + pet.Height;
            embeddedCenterX = pet.Left + pet.Width / 2.0;
        }

        private void RestorePetBaseSize()
        {
            double sprite = SpriteSize(pet);
            double baseWidth = sprite * 640 / 360 + 20;
            double baseHeight = sprite + 35;
            double bottom = embeddedFoot ?? (pet.Top + pet.Height);
            double centerX = embeddedCenterX ?? (pet.Left + pet.Width / 2.0);
            embeddedFoot = null;
            embeddedCenterX = null;
            var spriteLayer = ((BigBlueFish.Windows.MainWindow)pet).CompanionSpriteLayer;
            spriteLayer.Width = baseWidth;
            spriteLayer.Height = baseHeight;
            spriteLayer.VerticalAlignment = VerticalAlignment.Bottom;
            if (Math.Abs(pet.Width - baseWidth) < 0.5 && Math.Abs(pet.Height - baseHeight) < 0.5
                && Math.Abs(pet.Top + pet.Height - bottom) < 0.5)
                return;
            pet.Top = bottom - baseHeight;
            pet.Left = centerX - baseWidth / 2.0;
            pet.Width = baseWidth;
            pet.Height = baseHeight;
        }

        private (double Width, double Height) MeasureBubble(Rect area, double scale, double maxHeightDip)
        {
            double maximumWidth = Math.Max(1.0, Math.Min(300.0, (area.Width - 16.0) / scale));
            bubble.MaxWidth = maximumWidth;
            measuringText.Text = ((BigBlueFish.Windows.MainWindow)pet).CompanionBubbleLayoutText;
            measuringText.FontFamily = text.FontFamily;
            measuringText.FontSize = text.FontSize;
            measuringText.FontWeight = text.FontWeight;
            measuringText.TextWrapping = text.TextWrapping;
            measuringText.Margin = text.Margin;
            double horizontal = bubble.Padding.Left + bubble.Padding.Right + bubble.BorderThickness.Left + bubble.BorderThickness.Right;
            double vertical = bubble.Padding.Top + bubble.Padding.Bottom + bubble.BorderThickness.Top + bubble.BorderThickness.Bottom;
            measuringText.Measure(new System.Windows.Size(Math.Max(1, maximumWidth - horizontal), double.PositiveInfinity));
            double bubbleWidth = Math.Min(maximumWidth, Math.Max(26, measuringText.DesiredSize.Width) + horizontal);
            // WPF rounds the scaled arrange width; reserve two base DIP while
            // measuring so a wrap at that boundary cannot add an unmeasured line.
            measuringText.Measure(new System.Windows.Size(Math.Max(1, bubbleWidth - horizontal - 2), double.PositiveInfinity));
            double contentHeight = Math.Max(22, measuringText.DesiredSize.Height) + vertical;
            double maxUnscaled = Math.Max(1.0, maxHeightDip / scale);
            double bubbleHeight = Math.Min(maxUnscaled, contentHeight);
            return (bubbleWidth * scale, bubbleHeight * scale);
        }

        public void Update()
        {
            // Refresh runs during MainWindow construction (bubble scale). WPF forbids
            // setting Owner to a window that has not been shown yet, so wait for Loaded.
            if (updating || closed || !pet.IsLoaded)
                return;
            SyncHost();
            if (embedded)
                UpdateEmbedded();
            else
                UpdatePopup();
        }

        private void UpdateEmbedded()
        {
            updating = true;
            try
            {
                // Keep a fixed HWND size in capture mode. OBS anchors top-left; resizing the
                // window would move the bottom-docked sprite inside the captured frame.
                EnsureCaptureFrame();
                if (!pet.IsVisible || bubble.Visibility != Visibility.Visible)
                    return;

                Rect area = WorkArea(pet);
                var styledBubble = bubble as BigBlueFish.Windows.CompanionBubble;
                CaptureFrameMetrics(out _, out _, out double spaceBelowHead, out double frameWidth, out double frameHeight, out double scale);
                double headY = frameHeight - spaceBelowHead;
                double maxBubbleHeight = Math.Max(1.0, headY - EmbeddedBubbleGap);
                var (bubbleWidth, bubbleHeight) = MeasureBubble(area, scale, maxBubbleHeight);
                double bubbleLeft = (frameWidth - bubbleWidth) / 2.0;
                double bubbleTop = headY - EmbeddedBubbleGap - bubbleHeight;
                Canvas.SetLeft(bubble, bubbleLeft);
                Canvas.SetTop(bubble, Math.Max(0, bubbleTop));
                styledBubble?.PointAt((frameWidth / 2.0 - bubbleLeft) / scale, false);
            }
            finally
            {
                updating = false;
            }
        }

        // Upstream-aligned separate bubble placement (non-capture mode).
        private void UpdatePopup()
        {
            if (popup == null)
                return;

            updating = true;
            try
            {
                if (!pet.IsVisible || bubble.Visibility != Visibility.Visible)
                {
                    if (popup.IsVisible)
                        popup.Hide();
                    return;
                }

                Rect area = WorkArea(pet);
                var styledBubble = bubble as BigBlueFish.Windows.CompanionBubble;
                double scale = ((BigBlueFish.Windows.MainWindow)pet).CompanionBubbleScale;
                Rect petBounds = PetBounds(pet);
                // Measure in the bubble's own units; position its scaled window in desktop DIP.
                double maximumWidth = Math.Max(1.0, Math.Min(300.0, (area.Width - 16.0) / scale));
                bubble.MaxWidth = maximumWidth;
                measuringText.Text = ((BigBlueFish.Windows.MainWindow)pet).CompanionBubbleLayoutText;
                measuringText.FontFamily = text.FontFamily;
                measuringText.FontSize = text.FontSize;
                measuringText.FontWeight = text.FontWeight;
                measuringText.TextWrapping = text.TextWrapping;
                measuringText.Margin = text.Margin;
                double horizontal = bubble.Padding.Left + bubble.Padding.Right + bubble.BorderThickness.Left + bubble.BorderThickness.Right;
                double vertical = bubble.Padding.Top + bubble.Padding.Bottom + bubble.BorderThickness.Top + bubble.BorderThickness.Bottom;
                measuringText.Measure(new System.Windows.Size(Math.Max(1, maximumWidth - horizontal), double.PositiveInfinity));
                double bubbleWidth = Math.Min(maximumWidth, Math.Max(26, measuringText.DesiredSize.Width) + horizontal);
                // WPF rounds the scaled arrange width; reserve two base DIP while
                // measuring so a wrap at that boundary cannot add an unmeasured line.
                measuringText.Measure(new System.Windows.Size(Math.Max(1, bubbleWidth - horizontal - 2), double.PositiveInfinity));
                double bubbleHeight = Math.Min(Math.Max(1.0, (area.Height - 16.0) / scale), Math.Max(22, measuringText.DesiredSize.Height) + vertical);
                bubbleWidth *= scale;
                bubbleHeight *= scale;
                double left = Limit(pet.Left + petBounds.Left + petBounds.Width / 2.0 - bubbleWidth / 2.0, area.Left + 8.0, area.Right - bubbleWidth - 8.0);
                double top = pet.Top + petBounds.Top - bubbleHeight - 10.0;
                bool belowPet = false;
                if (top < area.Top + 8.0)
                {
                    belowPet = true;
                    top = pet.Top + petBounds.Bottom + 10.0;
                }

                top = Limit(top, area.Top + 8.0, area.Bottom - bubbleHeight - 8.0);
                styledBubble?.PointAt((pet.Left + petBounds.Left + petBounds.Width / 2.0 - left) / scale, belowPet);
                popup.Width = Math.Max(1.0, bubbleWidth);
                popup.Height = Math.Max(1.0, bubbleHeight);
                popup.Left = left;
                popup.Top = top;
                if (!popup.IsVisible)
                {
                    popup.Show();
                    QueueUpdate();
                }
            }
            finally
            {
                updating = false;
            }
        }
    }

    private static readonly ConditionalWeakTable<Window, Follow> followers = new ConditionalWeakTable<Window, Follow>();
    public static double SpriteSize(Window w)
    {
        return ((BigBlueFish.Windows.MainWindow)w).CompanionSpriteSize;
    }

    public static Rect PetBounds(Window w)
    {
        return ((BigBlueFish.Windows.MainWindow)w).CompanionVisibleBounds;
    }

    public static Rect WorkArea(Window w)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(w);
        Rect petBounds = PetBounds(w);
        if (PresentationSource.FromVisual(w) != null)
        {
            System.Windows.Point screenCenter = w.PointToScreen(new System.Windows.Point(petBounds.Left + petBounds.Width / 2.0, petBounds.Top + petBounds.Height / 2.0));
            Rectangle workingArea = Screen.FromPoint(new System.Drawing.Point((int)screenCenter.X, (int)screenCenter.Y)).WorkingArea;
            System.Windows.Point localTopLeft = w.PointFromScreen(new System.Windows.Point(workingArea.Left, workingArea.Top));
            System.Windows.Point localBottomRight = w.PointFromScreen(new System.Windows.Point(workingArea.Right, workingArea.Bottom));
            return new Rect(w.Left + localTopLeft.X, w.Top + localTopLeft.Y, localBottomRight.X - localTopLeft.X, localBottomRight.Y - localTopLeft.Y);
        }

        Rectangle primaryArea = (Screen.PrimaryScreen ?? throw new InvalidOperationException("无法找到主显示器")).WorkingArea;
        return new Rect((double)primaryArea.Left / dpi.DpiScaleX, (double)primaryArea.Top / dpi.DpiScaleY, (double)primaryArea.Width / dpi.DpiScaleX, (double)primaryArea.Height / dpi.DpiScaleY);
    }

    private static double Limit(double v, double min, double max)
    {
        return Math.Clamp(double.IsFinite(v) ? v : min, min, Math.Max(min, max));
    }

    public static void Clamp(Window w)
    {
        Follow follower = followers.GetValue(w, (Window x) => new Follow(x));
        Rect area = WorkArea(w);
        Rect petBounds = PetBounds(w);
        double left = Limit(w.Left, area.Left - petBounds.Left, area.Right - petBounds.Right);
        if (Math.Abs(left + petBounds.Left - area.Left) < 14.0)
            left = area.Left - petBounds.Left;
        if (Math.Abs(left + petBounds.Right - area.Right) < 14.0)
            left = area.Right - petBounds.Right;
        w.Left = left;
        // Always clamp by the visible sprite. Capture frame size is fixed, so this keeps the
        // character on-screen without resizing the HWND during speech.
        double top = Limit(w.Top, area.Top - petBounds.Top, area.Bottom - petBounds.Bottom);
        if (Math.Abs(top + petBounds.Top - area.Top) < 14.0)
            top = area.Top - petBounds.Top;
        if (Math.Abs(top + petBounds.Bottom - area.Bottom) < 14.0)
            top = area.Bottom - petBounds.Bottom;
        w.Top = top;

        follower.Update();
    }

    public static void Refresh(Window w) => followers.GetValue(w, x => new Follow(x)).Update();

    public static void ApplyCaptureMode(Window w) =>
        followers.GetValue(w, x => new Follow(x)).ApplyCaptureMode();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
