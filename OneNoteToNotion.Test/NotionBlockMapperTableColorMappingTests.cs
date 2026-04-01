using System.Text.Json;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Notion;
using Xunit;

namespace OneNoteToNotion.Test;

public class NotionBlockMapperTableColorMappingTests
{
    private readonly NotionBlockMapper _mapper = new();

    [Fact]
    public void Map_TableCellBackgroundMode_ShouldKeepForegroundColorOnly()
    {
        var table = CreateSingleCellTable(
            text: "A1",
            foregroundColor: "#ff0000",
            backgroundColor: "#fff2cc");

        var blocks = _mapper.Map(table, TableCellColorMappingMode.Background);

        Assert.Equal("red", GetCellFirstAnnotationColor(blocks[0], 0, 0));
        Assert.Equal("yellow_background", blocks[0].TableRowBackgroundColors?[0]);
    }

    [Fact]
    public void Map_TableCellTextMode_ShouldKeepLegacyTextColorMapping()
    {
        var table = CreateSingleCellTable(
            text: "A1",
            foregroundColor: "#ff0000",
            backgroundColor: "#fff2cc");

        var blocks = _mapper.Map(table, TableCellColorMappingMode.Text);

        Assert.Equal("yellow", GetCellFirstAnnotationColor(blocks[0], 0, 0));
        Assert.Null(blocks[0].TableRowBackgroundColors?[0]);
    }

    [Fact]
    public void Map_TableCellBackgroundMode_WhenNoCellBackground_ShouldFallbackToForeground()
    {
        var table = CreateSingleCellTable(
            text: "A1",
            foregroundColor: "#ff0000",
            backgroundColor: null);

        var blocks = _mapper.Map(table, TableCellColorMappingMode.Background);

        Assert.Equal("red", GetCellFirstAnnotationColor(blocks[0], 0, 0));
        Assert.Null(blocks[0].TableRowBackgroundColors?[0]);
    }

    [Fact]
    public void Map_TableCellBackgroundMode_ForEmptyColoredCell_ShouldUseInvisiblePlaceholder()
    {
        var table = CreateSingleCellTable(
            text: string.Empty,
            foregroundColor: null,
            backgroundColor: "#deebf6");

        var blocks = _mapper.Map(table, TableCellColorMappingMode.Background);

        Assert.Equal("default", GetCellFirstAnnotationColor(blocks[0], 0, 0));
        Assert.Equal("\u200B", GetCellFirstTextContent(blocks[0], 0, 0));
        Assert.Equal("blue_background", blocks[0].TableRowBackgroundColors?[0]);
    }

    private static SemanticDocument CreateSingleCellTable(
        string text,
        string? foregroundColor,
        string? backgroundColor)
    {
        var runStyle = new TextStyleStyle(
            ForegroundColor: foregroundColor,
            BackgroundColor: backgroundColor);
        var run = new TextRun(text, runStyle);
        var cell = new TableCellBlock([run], BackgroundColor: backgroundColor);
        var row = (IReadOnlyList<TableCellBlock>)new List<TableCellBlock> { cell };
        var document = new SemanticDocument { Title = "TableTest" };
        document.Blocks.Add(new TableBlock([row]));
        return document;
    }

    private static string? GetCellFirstAnnotationColor(NotionBlockInput tableBlock, int rowIndex, int columnIndex)
    {
        using var rowJson = JsonDocument.Parse(JsonSerializer.Serialize(tableBlock.Children[rowIndex].Value));
        return rowJson.RootElement
            .GetProperty("cells")[columnIndex][0]
            .GetProperty("annotations")
            .GetProperty("color")
            .GetString();
    }

    private static string? GetCellFirstTextContent(NotionBlockInput tableBlock, int rowIndex, int columnIndex)
    {
        using var rowJson = JsonDocument.Parse(JsonSerializer.Serialize(tableBlock.Children[rowIndex].Value));
        return rowJson.RootElement
            .GetProperty("cells")[columnIndex][0]
            .GetProperty("text")
            .GetProperty("content")
            .GetString();
    }
}
