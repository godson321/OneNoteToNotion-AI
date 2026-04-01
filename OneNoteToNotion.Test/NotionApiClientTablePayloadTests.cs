using System.Reflection;
using System.Text.Json;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Notion;
using Xunit;

namespace OneNoteToNotion.Test;

public class NotionApiClientTablePayloadTests
{
    [Fact]
    public void ToBlockObject_Table_ShouldNestChildrenUnderTablePayload()
    {
        var tableRow = new NotionBlockInput
        {
            Type = "table_row",
            Value = new
            {
                cells = new object[]
                {
                    new object[]
                    {
                        new
                        {
                            type = "text",
                            text = new { content = "A1" },
                            annotations = new
                            {
                                bold = false,
                                italic = false,
                                underline = false,
                                strikethrough = false,
                                code = false,
                                color = "default"
                            }
                        }
                    }
                }
            }
        };

        var table = new NotionBlockInput
        {
            Type = "table",
            Value = new
            {
                table_width = 1,
                has_column_header = true,
                has_row_header = false
            },
            Children = new List<NotionBlockInput> { tableRow }
        };

        var method = typeof(NotionApiClient).GetMethod(
            "ToBlockObject",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var payloadObject = method!.Invoke(null, [table]);
        var json = JsonSerializer.Serialize(payloadObject);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("table", out var tablePayload));
        Assert.True(tablePayload.TryGetProperty("children", out var nestedChildren));
        Assert.Equal(JsonValueKind.Array, nestedChildren.ValueKind);
        Assert.Equal(1, nestedChildren.GetArrayLength());

        Assert.False(root.TryGetProperty("children", out _));
    }
}
