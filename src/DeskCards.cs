using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("卡片助手")]
[assembly: AssemblyProduct("卡片助手")]
[assembly: AssemblyDescription("悬浮在桌面的每日待办卡片")]
[assembly: AssemblyVersion("1.0.0.0")]

public sealed class TaskCard : INotifyPropertyChanged
{
    private bool done;
    private int theme;
    public DateTime Day { get; set; }
    public string Title { get; set; }
    public int Theme
    {
        get { return theme; }
        set
        {
            if (theme == value) return;
            theme = value;
            Notify("Theme");
        }
    }
    public bool Done
    {
        get { return done; }
        set
        {
            if (done == value) return;
            done = value;
            Notify("Done");
        }
    }
    public event PropertyChangedEventHandler PropertyChanged;
    private void Notify(string property)
    {
        var changed = PropertyChanged;
        if (changed != null) changed(this, new PropertyChangedEventArgs(property));
    }
}

public enum DockSide { None, Left, Right }

public sealed class CardData
{
    public bool Topmost { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public DockSide Dock { get; set; }
    public List<TaskCard> Tasks { get; set; }

    public CardData()
    {
        Topmost = true;
        Tasks = new List<TaskCard>();
    }
}

internal sealed class CardWindow
{
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(CardData));
    private static readonly string[] ComposerThemes = { "lime", "blue", "amber", "violet" };
    private const int ThemeCount = 4;
    private const double CardHeight = 278;
    private const double FanStep = 74;
    private const double FanCardHeight = 232;
    private const double SlideOverlap = 34;
    private const double MaxPreviewPull = 116;
    private const double MaxComposePull = 146;
    private const double ComposerStart = CardHeight - 14 - SlideOverlap;
    private const double DockDistance = 26;
    private const double DockPeek = 12;
    private readonly string file;
    private readonly CardData data;
    private readonly Window window;
    private readonly ContentControl card;
    private readonly ContentControl adjacentCard;
    private readonly Grid swipeArea;
    private readonly Grid dockSurface;
    private readonly TranslateTransform dockOffset;
    private readonly TranslateTransform cardOffset;
    private readonly TranslateTransform adjacentOffset;
    private readonly Grid front;
    private readonly Grid pullPreview;
    private readonly ContentControl peekBack;
    private readonly ContentControl peekNear;
    private readonly TranslateTransform peekNearOffset;
    private readonly Border composer;
    private readonly TranslateTransform composerOffset;
    private readonly TextBox draft;
    private readonly Button saveDraft;
    private readonly DropShadowEffect overlapShadow = new DropShadowEffect
    {
        BlurRadius = 14, ShadowDepth = 3, Opacity = .26, Color = Colors.Black
    };
    private readonly ScrollViewer fan;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer tuckTimer;
    private List<TaskCard> dayTasks = new List<TaskCard>();
    private int cardIndex;
    private bool dragging;
    private Point dragStart;
    private bool? horizontalDrag;
    private int? pendingSlide;
    private TaskCard pressedFanCard;
    private bool expanded;
    private bool composing;
    private DockSide dockSide;
    private bool tucked;
    private bool tuckAfterSnap;
    private double collapsedTop;
    private DateTime today = DateTime.Today;
    private DateTime selected = DateTime.Today;
    private bool dirty;

    private CardWindow(string file)
    {
        this.file = file;
        data = Read(file);
        using (var layout = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeskCards.xaml"))
            window = (Window)XamlReader.Load(layout);

        card = Element<ContentControl>("ActiveCard");
        adjacentCard = Element<ContentControl>("AdjacentCard");
        swipeArea = Element<Grid>("SwipeArea");
        dockSurface = Element<Grid>("DockSurface");
        dockOffset = (TranslateTransform)swipeArea.RenderTransform;
        front = Element<Grid>("Front");
        cardOffset = (TranslateTransform)front.RenderTransform;
        adjacentOffset = (TranslateTransform)adjacentCard.RenderTransform;
        pullPreview = Element<Grid>("PullPreview");
        peekBack = Element<ContentControl>("PeekBack");
        peekNear = Element<ContentControl>("PeekNear");
        peekNearOffset = (TranslateTransform)peekNear.RenderTransform;
        composer = Element<Border>("Composer");
        composerOffset = (TranslateTransform)composer.RenderTransform;
        draft = Element<TextBox>("Draft");
        var draftFocus = Element<Border>("DraftFocus");
        draft.IsKeyboardFocusWithinChanged += delegate
        {
            var opacity = draft.IsKeyboardFocusWithin ? 1d : 0d;
            if (SystemParameters.ClientAreaAnimation)
                draftFocus.BeginAnimation(UIElement.OpacityProperty, Motion(draftFocus.Opacity, opacity, 135));
            else
                draftFocus.Opacity = opacity;
        };
        saveDraft = Element<Button>("SaveDraft");
        draft.TextChanged += delegate { saveDraft.IsEnabled = draft.Text.Trim().Length > 0; };
        saveDraft.IsEnabled = false;
        saveDraft.Click += delegate { SubmitDraft(); };
        Element<Button>("CancelDraft").Click += delegate { CloseComposer(); };
        fan = Element<ScrollViewer>("Fan");
        foreach (var group in data.Tasks.GroupBy(task => task.Day))
        {
            var index = 0;
            foreach (var task in group)
            {
                if (task.Theme < 1 || task.Theme > ThemeCount) task.Theme = index % ThemeCount + 1;
                task.PropertyChanged += TaskChanged;
                index++;
            }
        }
        var area = SystemParameters.WorkArea;
        window.Left = Math.Max(area.Left, area.Right - window.Width - 12);
        window.Top = area.Top + 10;
        if (RestorePosition()) dockSide = data.Dock;
        window.Topmost = data.Topmost;
        tuckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
        tuckTimer.Tick += delegate
        {
            tuckTimer.Stop();
            if (dockSide != DockSide.None && !composing && !dragging && (tuckAfterSnap || !dockSurface.IsMouseOver))
                SetTucked(true);
            tuckAfterSnap = false;
        };
        dockSurface.MouseEnter += delegate
        {
            if (tucked) SetTucked(false);
            else if (!tuckAfterSnap) tuckTimer.Stop();
        };
        dockSurface.MouseLeave += delegate { ScheduleTuck(false); };
        window.Loaded += delegate
        {
            if (dockSide == DockSide.None) return;
            AlignToEdge(MonitorWorkArea());
            SetTucked(true, false);
        };
        window.ContextMenuOpening += delegate(object sender, ContextMenuEventArgs e) { e.Handled = true; };
        window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (composing)
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    CloseComposer();
                }
                return;
            }
            if (e.Key == Key.Up || (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control))
            {
                e.Handled = true;
                OpenComposer();
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                ExpandOrAdvance();
            }
            else if (e.Key == Key.Escape && expanded)
            {
                e.Handled = true;
                SetExpanded(false);
            }
            else if (e.Key == Key.Left || e.Key == Key.Right)
                e.Handled = MoveCard(e.Key == Key.Left ? -1 : 1);
            else if (e.Key == Key.Delete && card.Content is TaskCard)
            {
                e.Handled = true;
                DeleteTask((TaskCard)card.Content);
            }
            else if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                TogglePin();
            }
        };
        swipeArea.PreviewMouseLeftButtonDown += StartSwipe;
        swipeArea.PreviewMouseMove += DragSwipe;
        swipeArea.PreviewMouseLeftButtonUp += EndSwipe;
        swipeArea.LostMouseCapture += delegate
        {
            if (!dragging) return;
            dragging = false;
            CancelDrag();
            if (!dockSurface.IsMouseOver) ScheduleTuck(false);
        };
        window.Closing += delegate(object sender, CancelEventArgs e) { if (dirty) e.Cancel = !TrySave(); };
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += delegate { CheckDate(); };
        window.Activated += delegate { CheckDate(); };
        window.Closed += delegate { timer.Stop(); tuckTimer.Stop(); };
        timer.Start();
        SelectDay(today);
    }

    private T Element<T>(string name) where T : FrameworkElement
    {
        return (T)window.FindName(name);
    }

    private static CardData Read(string path)
    {
        if (!File.Exists(path)) return new CardData();
        using (var stream = File.OpenRead(path))
            return (CardData)Serializer.Deserialize(stream);
    }

    private bool TrySave()
    {
        if (file == null) return true;
        var temporary = file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using (var stream = File.Create(temporary))
            {
                Serializer.Serialize(stream, data);
                stream.Flush(true);
            }
            if (File.Exists(file)) File.Replace(temporary, file, null);
            else File.Move(temporary, file);
            dirty = false;
            return true;
        }
        catch (Exception error)
        {
            MessageBox.Show(window, "暂时无法保存任务。请检查本机存储空间和文件权限后重试。\n\n" + error.Message,
                "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void SaveChange()
    {
        dirty = true;
        TrySave();
    }

    private void TogglePin()
    {
        data.Topmost = window.Topmost = !window.Topmost;
        SaveChange();
    }

    private bool RestorePosition()
    {
        if (!data.WindowLeft.HasValue || !data.WindowTop.HasValue) return false;
        var left = data.WindowLeft.Value;
        var top = data.WindowTop.Value;
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top)) return false;
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!desktop.Contains(new Point(left + window.Width / 2, top + 14))) return false;
        window.Left = left;
        window.Top = top;
        return true;
    }

    private void MoveWindow()
    {
        tuckTimer.Stop();
        tuckAfterSnap = false;
        SetTucked(false, false);
        dockSide = DockSide.None;
        CompleteSlide();
        SetExpanded(false);
        window.DragMove();
        SnapToEdge(MonitorWorkArea());
        SavePlacement();
        ScheduleTuck(true);
    }

    private void SavePlacement()
    {
        if (data.WindowLeft != window.Left || data.WindowTop != window.Top || data.Dock != dockSide)
        {
            data.WindowLeft = window.Left;
            data.WindowTop = window.Top;
            data.Dock = dockSide;
            SaveChange();
        }
    }

    private Rect MonitorWorkArea()
    {
        var source = PresentationSource.FromVisual(window);
        if (source == null || source.CompositionTarget == null) return SystemParameters.WorkArea;
        var area = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
        var toDip = source.CompositionTarget.TransformFromDevice;
        return new Rect(toDip.Transform(new Point(area.Left, area.Top)),
            toDip.Transform(new Point(area.Right, area.Bottom)));
    }

    private void SnapToEdge(Rect area)
    {
        var leftDistance = window.Left - area.Left;
        var rightDistance = area.Right - (window.Left + window.Width);
        dockSide = DockSide.None;
        if (Math.Min(leftDistance, rightDistance) > DockDistance) return;
        dockSide = leftDistance < rightDistance ? DockSide.Left : DockSide.Right;
        AlignToEdge(area);
    }

    private void AlignToEdge(Rect area)
    {
        window.Left = dockSide == DockSide.Left ? area.Left : area.Right - window.Width;
        window.Top = Math.Max(area.Top, Math.Min(window.Top, area.Bottom - CardHeight));
    }

    private void ScheduleTuck(bool afterSnap)
    {
        if (dockSide == DockSide.None || tucked || composing) return;
        tuckAfterSnap |= afterSnap;
        tuckTimer.Stop();
        tuckTimer.Start();
    }

    private void SetTucked(bool value, bool animate = true)
    {
        if (value && dockSide == DockSide.None) return;
        if (tucked == value && animate) return;
        tuckTimer.Stop();
        tuckAfterSnap = false;
        tucked = value;
        if (value)
        {
            if (pendingSlide.HasValue) CompleteSlide();
            SetExpanded(false);
            HidePullPreview();
            HideComposerPreview();
            AnimateCard(0, 0);
        }
        var from = dockOffset.X;
        var travel = window.Width - DockPeek;
        var to = value ? (dockSide == DockSide.Left ? -travel : travel) : 0;
        dockOffset.BeginAnimation(TranslateTransform.XProperty, null);
        dockOffset.X = to;
        if (!animate || !SystemParameters.ClientAreaAnimation) return;
        var slide = Motion(from, to, value ? 280 : 220);
        if (value) slide.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
        dockOffset.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void ShowFromTray()
    {
        if (!window.IsVisible) window.Show();
        SetTucked(false);
        window.Activate();
    }

    private Forms.NotifyIcon CreateTrayIcon(System.Drawing.Icon icon, Forms.ContextMenuStrip menu)
    {
        var visibility = menu.Items.Add("显示卡片");
        menu.Items.Add("退出卡片助手", null, delegate { window.Close(); });
        menu.Opening += delegate { visibility.Text = window.IsVisible && !tucked ? "隐藏卡片" : "显示卡片"; };
        visibility.Click += delegate
        {
            if (window.IsVisible && !tucked) window.Hide();
            else ShowFromTray();
        };
        var tray = new Forms.NotifyIcon { Icon = icon, Text = "悬浮卡片助手", ContextMenuStrip = menu };
        tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Left) ShowFromTray();
        };
        return tray;
    }

    private void CheckDate()
    {
        if (composing) return;
        var now = DateTime.Today;
        if (today == now) return;
        var followToday = selected == today;
        today = now;
        SelectDay(followToday ? today : selected);
    }

    private void SelectDay(DateTime day)
    {
        if (selected != day.Date)
        {
            SetExpanded(false);
            cardIndex = 0;
        }
        selected = day.Date;
        RefreshCards(cardIndex);
    }

    private void RefreshCards(int index)
    {
        dayTasks = data.Tasks.Where(task => task.Day == selected).ToList();
        cardIndex = Math.Max(0, Math.Min(index, dayTasks.Count - 1));
        SetCard();
        Element<Border>("Empty").Visibility = dayTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Element<Border>("StackMiddle").Visibility = dayTasks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Element<Border>("StackBack").Visibility = dayTasks.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCard()
    {
        pendingSlide = null;
        HidePullPreview();
        HideComposerPreview();
        card.Content = dayTasks.Count == 0 ? null : dayTasks[cardIndex];
        adjacentCard.Visibility = Visibility.Collapsed;
        adjacentCard.Content = null;
        front.Effect = null;
        adjacentOffset.BeginAnimation(TranslateTransform.XProperty, null);
        AnimateCard(0, 0);
    }

    private int AdjacentIndex(int direction)
    {
        return (cardIndex + direction + dayTasks.Count) % dayTasks.Count;
    }

    private bool MoveCard(int direction)
    {
        if (dayTasks.Count < 2) return false;
        if (pendingSlide.HasValue) CompleteSlide();
        SetExpanded(false);
        var width = swipeArea.ActualWidth;
        var distance = width - SlideOverlap;
        var x = cardOffset.X;
        ShowAdjacent(direction, x, distance);
        pendingSlide = AdjacentIndex(direction);
        if (!SystemParameters.ClientAreaAnimation)
        {
            CompleteSlide();
            return true;
        }
        var target = -direction * width;
        var duration = 130 + 95 * Math.Min(1, Math.Abs(target - x) / width);
        var incoming = Motion(adjacentOffset.X, 0, duration);
        incoming.Completed += delegate { CompleteSlide(); };
        cardOffset.BeginAnimation(TranslateTransform.XProperty, Motion(x, target, duration));
        adjacentOffset.BeginAnimation(TranslateTransform.XProperty, incoming);
        return true;
    }

    private void CompleteSlide()
    {
        if (pendingSlide.HasValue) cardIndex = pendingSlide.Value;
        SetCard();
    }

    private void ShowAdjacent(int direction, double offset, double distance)
    {
        adjacentCard.Content = dayTasks[AdjacentIndex(direction)];
        adjacentCard.Visibility = Visibility.Visible;
        overlapShadow.Direction = direction > 0 ? 0 : 180;
        front.Effect = overlapShadow;
        adjacentOffset.BeginAnimation(TranslateTransform.XProperty, null);
        adjacentOffset.X = offset + direction * distance;
    }

    private void CancelDrag()
    {
        if (composer.Visibility == Visibility.Visible && !composing)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                AnimateCard(0, 0);
                HideComposerPreview();
                return;
            }
            var retract = Motion(composerOffset.Y, ComposerStart, 170);
            retract.Completed += delegate { HideComposerPreview(); };
            composerOffset.BeginAnimation(TranslateTransform.YProperty, retract);
            AnimateOffset(TranslateTransform.YProperty, cardOffset.Y);
            return;
        }
        if (pullPreview.Visibility == Visibility.Visible)
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                AnimateCard(0, 0);
                HidePullPreview();
                return;
            }
            var retract = Motion(cardOffset.Y, 0, 170);
            retract.Completed += delegate { HidePullPreview(); };
            cardOffset.BeginAnimation(TranslateTransform.YProperty, retract);
            return;
        }
        if (adjacentCard.Visibility != Visibility.Visible || !SystemParameters.ClientAreaAnimation)
        {
            AnimateCard(cardOffset.X, cardOffset.Y);
            adjacentCard.Visibility = Visibility.Collapsed;
            front.Effect = null;
            return;
        }
        var direction = Math.Sign(adjacentOffset.X - cardOffset.X);
        var target = direction * (swipeArea.ActualWidth - SlideOverlap);
        var retreat = Motion(adjacentOffset.X, target, 165);
        retreat.Completed += delegate
        {
            adjacentCard.Visibility = Visibility.Collapsed;
            front.Effect = null;
        };
        adjacentOffset.BeginAnimation(TranslateTransform.XProperty, retreat);
        cardOffset.BeginAnimation(TranslateTransform.XProperty, Motion(cardOffset.X, 0, 165));
    }

    private void AnimateCard(double x, double y)
    {
        AnimateOffset(TranslateTransform.XProperty, x);
        AnimateOffset(TranslateTransform.YProperty, y);
    }

    private void AnimateOffset(DependencyProperty property, double from)
    {
        cardOffset.BeginAnimation(property, null);
        cardOffset.SetValue(property, 0d);
        if (from == 0 || !SystemParameters.ClientAreaAnimation) return;
        cardOffset.BeginAnimation(property, Motion(from, 0, 190));
    }

    private static DoubleAnimation Motion(double from, double to, double milliseconds)
    {
        return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
    }

    private void StartSwipe(object sender, MouseButtonEventArgs e)
    {
        if (tucked)
        {
            SetTucked(false);
            e.Handled = true;
            return;
        }
        tuckTimer.Stop();
        tuckAfterSnap = false;
        if (composing) return;
        pressedFanCard = null;
        var moveWindow = Keyboard.Modifiers == ModifierKeys.Shift;
        for (var node = e.OriginalSource as Visual; node != null && node != swipeArea;
            node = VisualTreeHelper.GetParent(node) as Visual)
        {
            if (node is ButtonBase || node is ScrollBar) return;
            var element = node as FrameworkElement;
            if (element != null && Equals(element.Tag, "move")) moveWindow = true;
            if (expanded && element != null && element.DataContext is TaskCard)
                pressedFanCard = (TaskCard)element.DataContext;
        }

        if (moveWindow)
        {
            e.Handled = true;
            MoveWindow();
            return;
        }
        if (pendingSlide.HasValue) CompleteSlide();
        if (pullPreview.Visibility == Visibility.Visible)
        {
            AnimateCard(0, 0);
            HidePullPreview();
        }
        if (composer.Visibility == Visibility.Visible)
        {
            AnimateCard(0, 0);
            HideComposerPreview();
        }
        dragStart = e.GetPosition(swipeArea);
        horizontalDrag = null;
        swipeArea.Focus();
        dragging = swipeArea.CaptureMouse();
        e.Handled = dragging;
    }

    private void DragSwipe(object sender, MouseEventArgs e)
    {
        if (!dragging || expanded) return;
        var movement = e.GetPosition(swipeArea) - dragStart;
        if (!horizontalDrag.HasValue)
        {
            if (Math.Max(Math.Abs(movement.X), Math.Abs(movement.Y)) < 9) return;
            horizontalDrag = Math.Abs(movement.X) >= Math.Abs(movement.Y);
        }
        AnimateCard(0, 0);
        if (horizontalDrag.Value)
        {
            TrackHorizontalSwipe(movement.X);
        }
        else
        {
            TrackVerticalSwipe(movement.Y);
        }
    }

    private void TrackVerticalSwipe(double distance)
    {
        if (distance < 0)
        {
            HidePullPreview();
            cardOffset.Y = -Math.Min(-distance * .86, MaxComposePull);
            ShowComposerPreview(cardOffset.Y);
            return;
        }
        HideComposerPreview();
        if (dayTasks.Count < 2 || distance == 0)
        {
            HidePullPreview();
            cardOffset.Y = Math.Max(-60, Math.Min(distance * .35, 60));
            return;
        }

        var offset = Math.Min(distance * .82, MaxPreviewPull);
        if (pullPreview.Visibility != Visibility.Visible)
        {
            peekBack.Content = dayTasks.Count > 2 ? dayTasks[AdjacentIndex(-2)] : null;
            peekNear.Content = dayTasks[AdjacentIndex(-1)];
            pullPreview.Visibility = Visibility.Visible;
            window.Height = CardHeight + MaxPreviewPull;
        }
        pullPreview.Height = offset + 19;
        peekNearOffset.Y = peekBack.Content == null ? 0 : offset * .48;
        cardOffset.Y = offset;
    }

    private void HidePullPreview()
    {
        if (pullPreview.Visibility != Visibility.Visible) return;
        pullPreview.Visibility = Visibility.Collapsed;
        peekBack.Content = null;
        peekNear.Content = null;
        if (!expanded) window.Height = CardHeight;
    }

    private void TrackHorizontalSwipe(double offset)
    {
        var distance = swipeArea.ActualWidth - SlideOverlap;
        if (dayTasks.Count < 2)
        {
            cardOffset.X = Math.Max(-28, Math.Min(offset * .2, 28));
            return;
        }
        cardOffset.X = Math.Max(-distance, Math.Min(offset, distance));
        ShowAdjacent(offset <= 0 ? 1 : -1, cardOffset.X, distance);
    }

    private void EndSwipe(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        swipeArea.ReleaseMouseCapture();
        var movement = e.GetPosition(swipeArea) - dragStart;
        if (horizontalDrag.HasValue)
        {
            if (horizontalDrag.Value) movement.Y = 0;
            else movement.X = 0;
        }
        FinishSwipe(movement, pressedFanCard);
        if (!dockSurface.IsMouseOver) ScheduleTuck(false);
        e.Handled = true;
    }

    private void FinishSwipe(Vector movement, TaskCard picked = null)
    {
        var x = Math.Abs(movement.X);
        var y = Math.Abs(movement.Y);
        if (x >= 44 && x >= y && MoveCard(movement.X < 0 ? 1 : -1)) return;
        var pulledOffset = cardOffset.Y;
        if (y >= 44 && y > x && movement.Y > 0)
        {
            ExpandOrAdvance(pulledOffset);
            if (expanded) return;
        }
        if (y >= 44 && y > x && movement.Y < 0)
        {
            OpenComposer();
            return;
        }
        CancelDrag();
        if (expanded && picked != null && x < 8 && y < 8)
        {
            var index = dayTasks.IndexOf(picked);
            if (index < 0) return;
            cardIndex = index;
            SetExpanded(false);
            SetCard();
            AnimateCard(0, 14);
        }
    }

    private void ExpandOrAdvance(double pulledOffset = 0)
    {
        if (expanded) fan.ScrollToVerticalOffset(fan.VerticalOffset - 2 * FanStep);
        else if (dayTasks.Count > 1) SetExpanded(true, pulledOffset);
    }

    private void SetExpanded(bool value, double pulledOffset = 0)
    {
        if (value == expanded) return;
        expanded = value;
        HidePullPreview();
        AnimateCard(0, 0);
        Element<Grid>("Deck").Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        fan.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded)
        {
            var cards = Element<ItemsControl>("FanCards");
            var afterActive = cardIndex + 1;
            cards.ItemsSource = dayTasks.Skip(afterActive).Concat(dayTasks.Take(afterActive)).ToList();
            collapsedTop = window.Top;
            var area = MonitorWorkArea();
            window.Height = Math.Min(FanCardHeight + (dayTasks.Count - 1) * FanStep, Math.Min(570, area.Height));
            if (area.Contains(new Point(window.Left, collapsedTop)))
                window.Top = Math.Max(area.Top, Math.Min(collapsedTop, area.Bottom - window.Height));
            fan.ScrollToEnd();
            window.UpdateLayout();
            if (SystemParameters.ClientAreaAnimation)
            {
                var offset = (TranslateTransform)fan.RenderTransform;
                offset.BeginAnimation(TranslateTransform.YProperty, Motion(collapsedTop - window.Top, 0, 310));
                AnimateFan(cards, pulledOffset);
            }
        }
        else
        {
            window.Height = CardHeight;
            window.Top = collapsedTop;
        }
    }

    private void AnimateFan(ItemsControl cards, double pulledOffset)
    {
        var first = Math.Max(0, (int)Math.Floor((fan.VerticalOffset - FanCardHeight) / FanStep));
        var last = cards.Items.Count - 1;
        for (var i = first; i <= last; i++)
        {
            var item = cards.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (item == null) continue;
            var offset = new TranslateTransform();
            item.RenderTransform = offset;
            var top = i * FanStep - fan.VerticalOffset;
            var alreadyVisible = pulledOffset > 0 && i >= last - 2;
            var from = -Math.Max(0, top) * .86;
            if (i == last) from = pulledOffset - top;
            else if (alreadyVisible) from = (i == last - 1 && last > 1 ? pulledOffset * .48 : 0) - top;
            var reveal = Motion(from, 0, i == last ? 320 : 275);
            offset.BeginAnimation(TranslateTransform.YProperty, reveal);
            if (i != last && !alreadyVisible)
                item.BeginAnimation(UIElement.OpacityProperty, Motion(.0, 1, 225));
        }
    }

    private int NextTheme()
    {
        return dayTasks.Count == 0 ? 1 : dayTasks[dayTasks.Count - 1].Theme % ThemeCount + 1;
    }

    private void ShowComposerPreview(double outgoingY)
    {
        composer.Tag = ComposerThemes[NextTheme() - 1];
        composer.Visibility = Visibility.Visible;
        composerOffset.BeginAnimation(TranslateTransform.YProperty, null);
        composerOffset.Y = ComposerStart + outgoingY;
    }

    private void HideComposerPreview()
    {
        composerOffset.BeginAnimation(TranslateTransform.YProperty, null);
        composer.Visibility = Visibility.Collapsed;
        composer.IsHitTestVisible = false;
    }

    private void OpenComposer()
    {
        if (composing) return;
        tuckTimer.Stop();
        tuckAfterSnap = false;
        if (pendingSlide.HasValue) CompleteSlide();
        SetExpanded(false);
        HidePullPreview();
        composing = true;
        draft.Clear();
        ShowComposerPreview(cardOffset.Y);
        draft.Focus();
        if (!SystemParameters.ClientAreaAnimation)
        {
            ActivateComposer();
            return;
        }
        var enter = Motion(composerOffset.Y, 0, 220);
        enter.Completed += delegate { ActivateComposer(); };
        cardOffset.BeginAnimation(TranslateTransform.YProperty, Motion(cardOffset.Y, -CardHeight, 220));
        composerOffset.BeginAnimation(TranslateTransform.YProperty, enter);
    }

    private void ActivateComposer()
    {
        if (!composing) return;
        composerOffset.BeginAnimation(TranslateTransform.YProperty, null);
        composerOffset.Y = 0;
        front.Visibility = Visibility.Hidden;
        composer.IsHitTestVisible = true;
        if (!draft.IsKeyboardFocusWithin) draft.Focus();
    }

    private void CloseComposer()
    {
        composing = false;
        draft.Clear();
        HideComposerPreview();
        front.Visibility = Visibility.Visible;
        AnimateCard(0, -22);
        swipeArea.Focus();
        if (!dockSurface.IsMouseOver) ScheduleTuck(false);
    }

    private void SubmitDraft()
    {
        var title = draft.Text.Trim();
        if (!composing || title.Length == 0) return;
        CloseComposer();
        AddTask(title);
        AnimateCard(0, 12);
    }

    private void AddTask(string title)
    {
        SetExpanded(false);
        var task = new TaskCard { Title = title, Day = selected, Theme = NextTheme() };
        task.PropertyChanged += TaskChanged;
        data.Tasks.Add(task);
        RefreshCards(dayTasks.Count);
        SaveChange();
    }

    private void TaskChanged(object sender, PropertyChangedEventArgs args)
    {
        SaveChange();
    }

    private void DeleteTask(TaskCard task)
    {
        if (MessageBox.Show(window, "删除「" + task.Title + "」？", "删除卡片", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        task.PropertyChanged -= TaskChanged;
        data.Tasks.Remove(task);
        SetExpanded(false);
        RefreshCards(cardIndex);
        SaveChange();
    }

#if VERIFY
    private static T FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            var match = child as T ?? FindVisual<T>(child);
            if (match != null) return match;
        }
        return null;
    }

    private void Verify(string preview)
    {
        window.ShowActivated = false;
        window.Show();
        var family = new FontFamily(window.FontFamily.Source.Split(',')[0]);
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        GlyphTypeface glyphs;
        Require(typeface.TryGetGlyphTypeface(out glyphs) && glyphs.CharacterToGlyphMap.ContainsKey('卡') &&
            Path.GetFileName(glyphs.FontUri.LocalPath).Equals("simyou.ttf", StringComparison.OrdinalIgnoreCase),
            "Rounded Chinese font is resolved");
        foreach (var title in new[] { "整理会议纪要", "检查本周交付清单", "回复待办邮件" })
            AddTask(title);
        Require(dayTasks.Count == 3 && ((TaskCard)card.Content).Title == "回复待办邮件", "New task becomes the active card");
        Require(dayTasks.Select(task => task.Theme).SequenceEqual(new[] { 1, 2, 3 }), "New cards cycle through styles");
        window.UpdateLayout();
        TrackHorizontalSwipe(-120);
        Require(cardOffset.X == -120 && adjacentOffset.X > 0 &&
            ReferenceEquals(adjacentCard.Content, dayTasks[0]), "First card follows the last during the drag");
        Require(cardOffset.X + swipeArea.ActualWidth - adjacentOffset.X == SlideOverlap,
            "The outgoing card overlaps the incoming card without a gap");
        FinishSwipe(new Vector(-90, 12));
        WaitForMotion();
        Require(cardIndex == 0 && adjacentCard.Visibility == Visibility.Collapsed, "Left swipe wraps from last to first");
        FinishSwipe(new Vector(20, 12));
        Require(cardIndex == 0, "Short drags do not turn the card");
        TrackHorizontalSwipe(120);
        Require(ReferenceEquals(adjacentCard.Content, dayTasks[2]), "Last card follows the first during the drag");
        Require(adjacentOffset.X + swipeArea.ActualWidth - cardOffset.X == SlideOverlap,
            "The same overlap is present in the reverse direction");
        FinishSwipe(new Vector(90, 12));
        WaitForMotion();
        Require(cardIndex == 2, "Right swipe wraps from first to last");
        TrackVerticalSwipe(27);
        Require(pullPreview.IsVisible, "A short downward pull previews the deck before release");
        FinishSwipe(new Vector(0, 27));
        WaitForMotion();
        Require(!expanded && !pullPreview.IsVisible && window.Height == CardHeight, "A short pull retracts the preview");
        AnimateCard(0, 0);
        TrackVerticalSwipe(125);
        window.UpdateLayout();
        Require(ReferenceEquals(peekBack.Content, dayTasks[0]) && ReferenceEquals(peekNear.Content, dayTasks[1]),
            "The pull preview shows the actual neighboring cards");
        Require(!expanded && pullPreview.IsVisible && cardOffset.Y > 90,
            "The card stack is visible before the gesture ends");
        RenderPreview(Path.ChangeExtension(preview, ".pull.png"));
        FinishSwipe(new Vector(10, 90));
        WaitForMotion();
        var fanItems = Element<ItemsControl>("FanCards").Items;
        Require(expanded && fan.IsVisible && Element<ItemsControl>("FanCards").Items.Count == 3 &&
            window.Height > CardHeight, "Down swipe expands the cards");
        Require(ReferenceEquals(fanItems[2], dayTasks[2]) && ReferenceEquals(fanItems[0], dayTasks[0]),
            "The pulled card stays at the bottom and in front of the other cards");
        RenderPreview(Path.ChangeExtension(preview, ".expanded.png"));
        FinishSwipe(new Vector(0, 0), dayTasks[1]);
        Require(!expanded && cardIndex == 1, "Click a spread card to select it");
        FinishSwipe(new Vector(0, 90));
        Require(ReferenceEquals(fanItems[0], dayTasks[2]) && ReferenceEquals(fanItems[2], dayTasks[1]),
            "A card pulled from the middle also moves in front of the stack");
        FinishSwipe(new Vector(0, 0), dayTasks[1]);
        window.UpdateLayout();
        var check = FindVisual<CheckBox>(card);
        var container = FindVisual<ContentPresenter>(card);
        var template = card.ContentTemplate;
        var status = (TextBlock)template.FindName("TaskStatus", container);
        var surface = (Border)template.FindName("Card", container);
        Require(surface.Background.ToString() == "#FFA6E9FC", "Blue active style");
        Require(swipeArea.ContextMenu == null && FindVisual<Button>(card) == null, "No context menu or overflow button");
        check.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        window.UpdateLayout();
        Require(status.Text == "已完成" && surface.Background.ToString() == "#FF292D27", "Completed appearance");
        Require(Read(file).Tasks[1].Done, "Completion persists");
        Require(window.AllowsTransparency && window.WindowStyle == WindowStyle.None, "Transparent borderless window");
        WaitForMotion();
        RenderPreview(preview);

        FindVisual<CheckBox>(card).SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        Require(!Read(file).Tasks[1].Done, "Undo completion persists");
        AnimateCard(0, 0);
        TrackVerticalSwipe(-120);
        window.UpdateLayout();
        Require(!composing && composer.IsVisible && !composer.IsHitTestVisible && cardOffset.Y < -80,
            "Upward drag slides a new card into view before release");
        Require(composerOffset.Y == ComposerStart + cardOffset.Y && Equals(composer.Tag, "violet"),
            "The incoming add card tracks the drag and uses the next task style");
        RenderPreview(Path.ChangeExtension(preview, ".push.png"), false);
        FinishSwipe(new Vector(0, -120));
        WaitForMotion();
        Require(composing && composer.IsHitTestVisible && front.Visibility == Visibility.Hidden && composerOffset.Y == 0,
            "The composer snaps into place and becomes editable");
        RenderPreview(Path.ChangeExtension(preview, ".compose.png"));
        var cancel = Element<Button>("CancelDraft");
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancel));
        Require(!composing && !composer.IsVisible && dayTasks.Count == 3, "Cancel returns to the unchanged deck");
        SelectDay(today.AddDays(-1));
        Require(dayTasks.Count == 0 && card.Content == null, "Previous day starts empty");
        FinishSwipe(new Vector(8, -90));
        WaitForMotion();
        draft.Text = "昨日任务：整理资料";
        Require(saveDraft.IsEnabled, "Entering text enables save");
        Require(saveDraft.IsDefault, "Enter uses the standard Windows default button");
        AccessKeyManager.ProcessKey(PresentationSource.FromVisual(window), "\r", false);
        Require(dayTasks.Count == 1 && dayTasks[0].Title == "昨日任务：整理资料", "Enter saves Chinese text on an empty day");
        Require(!MoveCard(1), "A one-card deck stays on the same card");
        SelectDay(today);
        Require(dayTasks.Count == 3 && cardIndex == 0, "Today's tasks remain separate");
        TogglePin();
        Require(!window.Topmost && !Read(file).Topmost, "Pin toggles and persists");
        VerifyDocking(preview);
        var reopened = new CardWindow(file);
        Require(reopened.dayTasks.Count == 3 && reopened.dayTasks[1].Theme == 2 && !reopened.window.Topmost, "Reopening restores saved tasks, style and pin");
        Require(reopened.window.Left == data.WindowLeft && reopened.window.Top == data.WindowTop &&
            reopened.dockSide == DockSide.Right, "Reopening restores the window position and dock side");
        reopened.window.Close();
        var oldXml = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @"<Theme>\d+</Theme>", "");
        File.WriteAllText(file, oldXml);
        var upgraded = new CardWindow(file);
        Require(upgraded.dayTasks.Select(task => task.Theme).SequenceEqual(new[] { 1, 2, 3 }), "Existing tasks get distinct styles");
        Require(upgraded.dayTasks[1].Title == "检查本周交付清单" && !upgraded.dayTasks[1].Done, "Existing task data is preserved");
        upgraded.window.Close();
        window.Close();
    }

    private void VerifyDocking(string preview)
    {
        Require(!window.ShowInTaskbar, "The taskbar button is disabled");
        var area = MonitorWorkArea();
        window.Left = area.Left + 8;
        window.Top = area.Top + 60;
        SnapToEdge(area);
        Require(dockSide == DockSide.Left && window.Left == area.Left, "Near the left edge, snap left");
        ScheduleTuck(true);
        WaitForMotion(900);
        Require(tucked && dockOffset.X == -(window.Width - DockPeek),
            "The left dock leaves a strip of the same card");
        RequireDockSlice(true);
        dockSurface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        WaitForMotion();
        Require(!tucked && dockOffset.X == 0, "Hovering over the strip restores the card");
        window.Left = area.Right - window.Width - 8;
        SnapToEdge(area);
        SetTucked(true, false);
        Require(dockSide == DockSide.Right && window.Left == area.Right - window.Width &&
            tucked && dockOffset.X == window.Width - DockPeek,
            "Near the right edge, snap and tuck right");
        window.UpdateLayout();
        RequireDockSlice(false);
        RenderPreview(Path.ChangeExtension(preview, ".docked.png"));
        SavePlacement();
        Require(Read(file).Dock == DockSide.Right, "The edge preference is saved");
    }

    private void RequireDockSlice(bool left)
    {
        var image = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render((Visual)window.Content);
        var edge = new byte[4];
        var outside = new byte[4];
        var width = image.PixelWidth;
        image.CopyPixels(new Int32Rect(left ? 6 : width - 7, 138, 1, 1), edge, 4, 0);
        image.CopyPixels(new Int32Rect(left ? 18 : width - 19, 138, 1, 1), outside, 4, 0);
        Require(edge.SequenceEqual(new byte[] { 0x82, 0xF9, 0xD9, 0xFF }) && outside[3] == 0,
            "Only the clipped edge of the actual task card is painted");
    }

    private static void WaitForMotion(int milliseconds = 450)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private void RenderPreview(string path, bool checkCorner = true)
    {
        var image = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render((Visual)window.Content);
        var corner = new byte[4];
        image.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
        if (checkCorner) Require(corner[3] == 0, "Transparent outside rounded card");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
#endif

    [STAThread]
    public static int Main(string[] args)
    {
#if VERIFY
        try
        {
            var application = new Application();
            new CardWindow(args[0]).Verify(args[1]);
            Console.WriteLine("Passed: both dock edges, automatic tuck, reveal, saved edge, hidden taskbar button, swipe gestures, input and existing data.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
#else
        bool first;
        using (var singleInstance = new Mutex(true, "Local\\Codex.DeskCards.2026", out first))
        {
            if (!first)
            {
                MessageBox.Show("卡片助手已在运行，可以从右下角通知区域打开。", "卡片助手");
                return 0;
            }
            try
            {
                var app = new Application();
                var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeskCards", "tasks.xml");
                var assistant = new CardWindow(file);
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                using (var menu = new Forms.ContextMenuStrip())
                using (var tray = assistant.CreateTrayIcon(icon, menu))
                {
                    tray.Visible = true;
                    return app.Run(assistant.window);
                }
            }
            catch (Exception error)
            {
                MessageBox.Show("卡片助手无法打开。\n\n" + error.Message, "卡片助手",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
#endif
    }
}
