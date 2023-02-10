using System;
using System.Threading.Tasks;
using System.Windows;
using ICSharpCode.AvalonEdit;

namespace RoslynPad.Editor
{
    public abstract class CodeTextEditorBase : TextEditor
    {
        public static readonly RoutedEvent ToolTipRequestEvent = CommonEvent.Register<CodeTextEditor, ToolTipRequestEventArgs>(
            nameof(ToolTipRequest), RoutingStrategy.Bubble);

        public Func<ToolTipRequestEventArgs, Task>? AsyncToolTipRequest { get; set; }

        public event EventHandler<ToolTipRequestEventArgs> ToolTipRequest
        {
            add => AddHandler(ToolTipRequestEvent, value);
            remove => RemoveHandler(ToolTipRequestEvent, value);
        }
    }
}

