using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using RunJargon.App.Models;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace RunJargon.App.Windows;

public partial class SelectionOverlayWindow : Window
{
    private Point? _dragStart;

    public SelectionOverlayWindow()
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += SelectionOverlayWindow_Loaded;
    }

    public ScreenRegion? SelectedRegion { get; private set; }

    private void SelectionOverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RootCanvas.Focus();
        PositionCaptureToolbar();
    }

    private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(RootCanvas);
        SelectionBorder.Visibility = Visibility.Visible;
        Mouse.Capture(RootCanvas);
        UpdateSelection(e.GetPosition(RootCanvas));
    }

    private void RootCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateSelection(e.GetPosition(RootCanvas));
    }

    private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null)
        {
            return;
        }

        UpdateSelection(e.GetPosition(RootCanvas));
        Mouse.Capture(null);

        var left = Canvas.GetLeft(SelectionBorder);
        var top = Canvas.GetTop(SelectionBorder);
        var width = SelectionBorder.Width;
        var height = SelectionBorder.Height;

        _dragStart = null;

        if (width < 8 || height < 8)
        {
            DialogResult = false;
            return;
        }

        SelectedRegion = new ScreenRegion(Left + left, Top + top, width, height);
        DialogResult = true;
    }

    private void RootCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private void UpdateSelection(Point current)
    {
        if (_dragStart is null)
        {
            return;
        }

        var left = Math.Min(_dragStart.Value.X, current.X);
        var top = Math.Min(_dragStart.Value.Y, current.Y);
        var width = Math.Abs(current.X - _dragStart.Value.X);
        var height = Math.Abs(current.Y - _dragStart.Value.Y);

        Canvas.SetLeft(SelectionBorder, left);
        Canvas.SetTop(SelectionBorder, top);
        SelectionBorder.Width = width;
        SelectionBorder.Height = height;
    }

    private void PositionCaptureToolbar()
    {
        var toolbarWidth = CaptureToolbar.Width > 0 ? CaptureToolbar.Width : 569d;
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var relativeScreenLeft = screen.Bounds.Left - Left;
        var relativeScreenTop = screen.Bounds.Top - Top;
        var left = relativeScreenLeft + Math.Max(0, (screen.Bounds.Width - toolbarWidth) / 2d);

        Canvas.SetLeft(CaptureToolbar, left);
        Canvas.SetTop(CaptureToolbar, Math.Max(0, relativeScreenTop));
    }
}
