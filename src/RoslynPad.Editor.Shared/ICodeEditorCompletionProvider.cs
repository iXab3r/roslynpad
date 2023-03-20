using System.Threading;
using System.Threading.Tasks;

namespace RoslynPad.Editor;

public interface ICodeEditorCompletionProvider
{
<<<<<<< Updated upstream
    Task<CompletionResult> GetCompletionData(int position, char? triggerChar, bool useSignatureHelp);
}
=======
    public interface ICodeEditorCompletionProvider
    {
        Task<CompletionResult> GetCompletionData(int position, char? triggerChar, bool useSignatureHelp, CancellationToken cancellationToken = default);
    }
}
>>>>>>> Stashed changes
