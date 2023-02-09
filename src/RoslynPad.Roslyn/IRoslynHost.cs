using Microsoft.CodeAnalysis;

namespace RoslynPad.Roslyn
{
    public interface IRoslynHost
    {
        ParseOptions ParseOptions { get; }

        TService GetService<TService>();

        void AddMetadataReference(ProjectId projectId, AssemblyIdentity assemblyIdentity);

        DocumentId AddDocument(DocumentCreationArgs args);

        Document? GetDocument(DocumentId documentId);

        void CloseDocument(DocumentId documentId);

        void UpdateDocument(Document document);

        MetadataReference CreateMetadataReference(string location);
    }
}
