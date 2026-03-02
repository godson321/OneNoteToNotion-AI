using System.Text.RegularExpressions;
using OneNoteToNotion.Domain;

namespace OneNoteToNotion.Mapping;

public sealed class NotionStyleMappingRules
{
    // Notion color name -> representative RGB for nearest-match fallback
    private static readonly (string Name, int R, int G, int B)[] NotionPalette =
    [
        ("gray",   155, 154, 151),
        ("brown",  147, 114, 100),
        ("orange", 217, 133, 56),
        ("yellow", 203, 176, 47),
        ("green",  68,  131, 97),
        ("blue",   51,  126, 169),
        ("purple", 144, 101, 176),
        ("pink",   193, 76,  138),
        ("red",    212, 76,  71)
    ];

    private static readonly Dictionary<string, string> ColorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Blacks / near-black → gray (Notion doesn't have pure black)
        ["#000000"] = "gray",
        ["#1f1f1f"] = "gray",
        ["#333333"] = "gray",
        ["#212121"] = "gray",

        // Reds
        ["#e03e2d"] = "red",
        ["#ff0000"] = "red",
        ["#c0504d"] = "red",
        ["#cc0000"] = "red",
        ["#d4351c"] = "red",
        ["#ff3333"] = "red",
        ["#993333"] = "red",

        // Greens
        ["#2dc26b"] = "green",
        ["#00ff00"] = "green",
        ["#008000"] = "green",
        ["#00b050"] = "green",
        ["#339966"] = "green",
        ["#448844"] = "green",
        ["#4f6128"] = "green",

        // Blues
        ["#3598db"] = "blue",
        ["#0000ff"] = "blue",
        ["#0070c0"] = "blue",
        ["#4472c4"] = "blue",
        ["#1f4e79"] = "blue",
        ["#0563c1"] = "blue",
        ["#2e75b6"] = "blue",
        ["#5b9bd5"] = "blue",
        ["#17a2b8"] = "blue",

        // Yellows
        ["#f1c40f"] = "yellow",
        ["#ffff00"] = "yellow",
        ["#ffc000"] = "yellow",
        ["#e2b714"] = "yellow",
        ["#bf8f00"] = "yellow",
        ["#cccc00"] = "yellow",

        // Oranges
        ["#ff8c00"] = "orange",
        ["#ed7d31"] = "orange",
        ["#e36c09"] = "orange",
        ["#ff6600"] = "orange",
        ["#f39c12"] = "orange",
        ["#d68910"] = "orange",
        ["#ffa500"] = "orange",

        // Purples
        ["#843fa1"] = "purple",
        ["#7030a0"] = "purple",
        ["#800080"] = "purple",
        ["#9b59b6"] = "purple",
        ["#6a0dad"] = "purple",
        ["#8e44ad"] = "purple",

        // Pinks
        ["#ff00ff"] = "pink",
        ["#ff69b4"] = "pink",
        ["#e91e63"] = "pink",
        ["#c77986"] = "pink",
        ["#ffc0cb"] = "pink",

        // Browns
        ["#8b4513"] = "brown",
        ["#a52a2a"] = "brown",
        ["#984806"] = "brown",
        ["#806000"] = "brown",

        // Grays
        ["#95a5a6"] = "gray",
        ["#808080"] = "gray",
        ["#a5a5a5"] = "gray",
        ["#7f7f7f"] = "gray",
        ["#999999"] = "gray",
        ["#666666"] = "gray",
        ["#bfbfbf"] = "gray",
        ["#d9d9d9"] = "gray",

        // Whites → default
        ["#ffffff"] = "default",
        ["#fafafa"] = "default",
        ["#f5f5f5"] = "default",

        // Common OneNote light table backgrounds
        ["#deebf6"] = "blue",      // Light blue header
        ["#fff2cc"] = "yellow",    // Light yellow data rows
        ["#e7e6e6"] = "gray",      // Light gray
        ["#d9e1f2"] = "blue",      // Another light blue
        ["#fce4d6"] = "orange",    // Light orange
        ["#e2efda"] = "green",     // Light green
        ["#f4b084"] = "orange",    // Light orange
        ["#c6e0b4"] = "green"      // Light green
    };

    public string MapColor(string? htmlColor)
    {
        if (string.IsNullOrWhiteSpace(htmlColor))
        {
            return "default";
        }

        var normalized = NormalizeColor(htmlColor.Trim());
        if (normalized is null)
        {
            return "default";
        }

        if (ColorMap.TryGetValue(normalized, out var notionColor))
        {
            return notionColor;
        }

        // Nearest-color fallback
        return FindNearestNotionColor(normalized);
    }

    private static string? NormalizeColor(string color)
    {
        // Handle rgb(r,g,b) and rgba(r,g,b,a)
        var rgbMatch = Regex.Match(color, @"rgba?\s*\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
        if (rgbMatch.Success)
        {
            var r = int.Parse(rgbMatch.Groups[1].Value);
            var g = int.Parse(rgbMatch.Groups[2].Value);
            var b = int.Parse(rgbMatch.Groups[3].Value);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        // Handle shorthand #RGB -> #RRGGBB
        var shortHex = Regex.Match(color, @"^#([0-9a-fA-F])([0-9a-fA-F])([0-9a-fA-F])$");
        if (shortHex.Success)
        {
            var r = shortHex.Groups[1].Value;
            var g = shortHex.Groups[2].Value;
            var b = shortHex.Groups[3].Value;
            return $"#{r}{r}{g}{g}{b}{b}";
        }

        // Standard #RRGGBB
        if (Regex.IsMatch(color, @"^#[0-9a-fA-F]{6}$"))
        {
            return color;
        }

        // Named CSS colors (common ones)
        return color.ToLowerInvariant() switch
        {
            "red" => "#FF0000",
            "green" => "#008000",
            "blue" => "#0000FF",
            "yellow" => "#FFFF00",
            "orange" => "#FFA500",
            "purple" => "#800080",
            "pink" => "#FFC0CB",
            "gray" or "grey" => "#808080",
            "black" => "#000000",
            "white" => "#FFFFFF",
            "brown" => "#A52A2A",
            _ => null
        };
    }

    private static string FindNearestNotionColor(string hexColor)
    {
        if (!TryParseHex(hexColor, out var cr, out var cg, out var cb))
        {
            return "default";
        }

        // Very dark → default (but allow light colors to map)
        var luminance = 0.299 * cr + 0.587 * cg + 0.114 * cb;
        if (luminance < 30)
        {
            return "default";
        }
        
        // Very light colors (> 240) still try to map to closest color
        // This allows light table backgrounds to be preserved
        // (Notion colors will be darker, but better than losing color entirely)

        var bestName = "default";
        var bestDist = double.MaxValue;

        foreach (var (name, r, g, b) in NotionPalette)
        {
            var dist = Math.Pow(cr - r, 2) + Math.Pow(cg - g, 2) + Math.Pow(cb - b, 2);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestName = name;
            }
        }

        return bestName;
    }

    private static bool TryParseHex(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (hex.Length != 7 || hex[0] != '#') return false;

        return int.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
            && int.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
            && int.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
    }

    public string? BuildAlignmentFallbackMessage(ParagraphAlignment alignment)
    {
        return alignment switch
        {
            ParagraphAlignment.Left => null,
            ParagraphAlignment.Center => "原文为居中对齐，Notion API 无独立段落对齐能力，已降级为普通段落。",
            ParagraphAlignment.Right => "原文为右对齐，Notion API 无独立段落对齐能力，已降级为普通段落。",
            ParagraphAlignment.Justify => "原文为两端对齐，Notion API 无独立段落对齐能力，已降级为普通段落。",
            _ => null
        };
    }

    public string? BuildLayoutFallbackMessage(LayoutHint layoutHint)
    {
        return layoutHint switch
        {
            LayoutHint.Normal => null,
            LayoutHint.AbsolutePositioned => "原文包含绝对定位元素，已按阅读顺序降级为普通块。",
            LayoutHint.ColumnLike => "原文可能为分栏布局，已按线性顺序降级为普通块。",
            LayoutHint.FloatingObject => "原文包含浮动对象，已降级为普通块并保留文本。",
            _ => null
        };
    }
}
