using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSystemColors = System.Windows.SystemColors;

namespace RASharp.App.Menus;

internal enum MenuDropVisualKind
{
    Before,
    After,
    Inside,
    Root,
}

internal sealed class MenuDragAdorner : Adorner
{
    private WpfPoint cursor;
    private Rect targetBounds;
    private MenuDropVisualKind visualKind;
    private string previewText = string.Empty;

    public MenuDragAdorner(UIElement adornedElement)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    public void Update(WpfPoint cursorPosition, Rect target, MenuDropVisualKind kind, string text)
    {
        cursor = cursorPosition;
        targetBounds = target;
        visualKind = kind;
        previewText = text;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var accentBrush = WpfSystemColors.HighlightBrush;
        var accentPen = new WpfPen(accentBrush, 2.5);
        accentPen.Freeze();

        if (visualKind == MenuDropVisualKind.Inside)
        {
            var fill = accentBrush.Clone();
            fill.Opacity = 0.16;
            fill.Freeze();
            drawingContext.DrawRoundedRectangle(fill, accentPen, targetBounds, 4, 4);
        }
        else
        {
            var y = visualKind == MenuDropVisualKind.Before
                ? targetBounds.Top
                : targetBounds.Bottom;
            drawingContext.DrawLine(
                accentPen,
                new WpfPoint(targetBounds.Left, y),
                new WpfPoint(targetBounds.Right, y));
            drawingContext.DrawEllipse(accentBrush, null, new WpfPoint(targetBounds.Left, y), 3.5, 3.5);
        }

        DrawPreview(drawingContext);
    }

    private void DrawPreview(DrawingContext drawingContext)
    {
        if (previewText.Length == 0)
        {
            return;
        }

        var formattedText = new FormattedText(
            previewText,
            CultureInfo.CurrentUICulture,
            WpfFlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            13,
            WpfSystemColors.WindowTextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = 260,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var width = Math.Min(280, formattedText.Width + 20);
        var height = formattedText.Height + 12;
        var x = Math.Min(cursor.X + 16, Math.Max(4, ActualWidth - width - 4));
        var y = Math.Min(cursor.Y + 18, Math.Max(4, ActualHeight - height - 4));
        var bounds = new Rect(Math.Max(4, x), Math.Max(4, y), width, height);
        var background = WpfSystemColors.WindowBrush.Clone();
        background.Opacity = 0.92;
        background.Freeze();
        var borderPen = new WpfPen(WpfSystemColors.ControlDarkBrush, 1);
        borderPen.Freeze();
        drawingContext.DrawRoundedRectangle(background, borderPen, bounds, 4, 4);
        drawingContext.DrawText(formattedText, new WpfPoint(bounds.Left + 10, bounds.Top + 6));
    }
}
