using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MudAI.App.ViewModels;
using MudAI.Core.Models;

namespace MudAI.App;

/// <summary>
/// Code-behind handles only terminal rendering — turning each <see cref="MudMessage"/> into
/// coloured WPF <see cref="Run"/>s in the RichTextBox. All state/logic lives in the view-model.
/// </summary>
public partial class MainWindow : Window
{
    private const int MaxInlines = 8000;
    private const int TrimBatch = 2000;

    private readonly Paragraph _paragraph;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        _paragraph = new Paragraph { Margin = new Thickness(2) };
        TerminalBox.Document = new FlowDocument(_paragraph)
        {
            PagePadding = new Thickness(4),
            Background = Brushes.Transparent,
            LineHeight = 15
        };

        viewModel.MessageReceived += AppendMessage;
        viewModel.TranscriptCleared += ClearTerminal;

        // PasswordBox can't be data-bound (by design); sync it manually.
        LoginPasswordBox.Password = viewModel.InitialLoginPassword;
        LoginPasswordBox.PasswordChanged += (_, _) => viewModel.SetLoginPassword(LoginPasswordBox.Password);
    }

    private void ClearTerminal()
    {
        _paragraph.Inlines.Clear();
    }

    private void AppendMessage(MudMessage message)
    {
        switch (message.Direction)
        {
            case MessageDirection.Outgoing:
                _paragraph.Inlines.Add(new Run(message.PlainText)
                {
                    Foreground = OutgoingBrush,
                    FontStyle = FontStyles.Italic
                });
                _paragraph.Inlines.Add(new LineBreak());
                break;

            case MessageDirection.System:
                _paragraph.Inlines.Add(new Run(message.PlainText)
                {
                    Foreground = SystemBrush,
                    FontStyle = FontStyles.Italic
                });
                _paragraph.Inlines.Add(new LineBreak());
                break;

            default: // Incoming
                foreach (var segment in message.Segments)
                    _paragraph.Inlines.Add(BuildRun(segment));
                _paragraph.Inlines.Add(new LineBreak());
                break;
        }

        TrimIfNeeded();
        TerminalBox.ScrollToEnd();
    }

    private static Run BuildRun(AnsiSegment seg)
    {
        Brush foreground;
        Brush? background;

        if (seg.Inverse)
        {
            foreground = seg.Background == AnsiColor.Default ? TerminalBg : NormalBrush(seg.Background);
            background = seg.Foreground == AnsiColor.Default ? DefaultFg : PaletteBrush(seg.Foreground, seg.Bold);
        }
        else
        {
            foreground = seg.Foreground == AnsiColor.Default ? DefaultFg : PaletteBrush(seg.Foreground, seg.Bold);
            background = seg.Background == AnsiColor.Default ? null : NormalBrush(seg.Background);
        }

        var run = new Run(seg.Text) { Foreground = foreground };
        if (background is not null) run.Background = background;
        if (seg.Bold) run.FontWeight = FontWeights.Bold;
        if (seg.Underline) run.TextDecorations = TextDecorations.Underline;
        return run;
    }

    private void TrimIfNeeded()
    {
        if (_paragraph.Inlines.Count <= MaxInlines) return;
        int toRemove = TrimBatch;
        while (toRemove-- > 0 && _paragraph.Inlines.FirstInline is { } first)
            _paragraph.Inlines.Remove(first);
    }

    // --- ANSI palette ---

    private static Brush PaletteBrush(AnsiColor color, bool bold) =>
        (bold ? BrightBrushes : NormalBrushes)[(int)color];

    private static Brush NormalBrush(AnsiColor color) => NormalBrushes[(int)color];

    private static readonly SolidColorBrush DefaultFg = Frozen(0xD0, 0xD0, 0xD0);
    private static readonly SolidColorBrush TerminalBg = Frozen(0x0B, 0x0B, 0x0B);
    private static readonly SolidColorBrush OutgoingBrush = Frozen(0x6E, 0xC6, 0xFF);
    private static readonly SolidColorBrush SystemBrush = Frozen(0x9A, 0x9A, 0x9A);

    private static readonly SolidColorBrush[] NormalBrushes =
    [
        Frozen(0x00, 0x00, 0x00), // black
        Frozen(0xCD, 0x3A, 0x3A), // red
        Frozen(0x33, 0xB0, 0x33), // green
        Frozen(0xCD, 0xCD, 0x00), // yellow
        Frozen(0x4A, 0x7B, 0xEE), // blue
        Frozen(0xCD, 0x00, 0xCD), // magenta
        Frozen(0x00, 0xCD, 0xCD), // cyan
        Frozen(0xE5, 0xE5, 0xE5)  // white
    ];

    private static readonly SolidColorBrush[] BrightBrushes =
    [
        Frozen(0x7F, 0x7F, 0x7F), // bright black (grey)
        Frozen(0xFF, 0x6E, 0x6E), // bright red
        Frozen(0x6E, 0xFF, 0x6E), // bright green
        Frozen(0xFF, 0xFF, 0x6E), // bright yellow
        Frozen(0x7E, 0x9E, 0xFF), // bright blue
        Frozen(0xFF, 0x6E, 0xFF), // bright magenta
        Frozen(0x6E, 0xFF, 0xFF), // bright cyan
        Frozen(0xFF, 0xFF, 0xFF)  // bright white
    ];

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
