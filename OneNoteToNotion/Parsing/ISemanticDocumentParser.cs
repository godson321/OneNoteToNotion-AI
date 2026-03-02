using OneNoteToNotion.Domain;

namespace OneNoteToNotion.Parsing;

public interface ISemanticDocumentParser
{
    SemanticDocument Parse(string oneNotePageXml);
}
