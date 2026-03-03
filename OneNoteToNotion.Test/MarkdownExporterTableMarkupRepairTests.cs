using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using OneNoteToNotion.Infrastructure;
using Xunit;

namespace OneNoteToNotion.Test;

public class MarkdownExporterTableMarkupRepairTests
{
    [Fact]
    public void Export_TableCellCorruptedCloser_ShouldAutoRepair()
    {
        const string fullWidthQuestionMark = "\uFF1F";
        var markdown = $$"""
---
title: Sample
---

# Sample

<table><tr><td>Alpha?/td><td><span>Beta{{fullWidthQuestionMark}}/span></td></tr></table>
""";

        var repaired = InvokeRepair(markdown, "sample-page");

        Assert.DoesNotContain("?/td>", repaired);
        Assert.DoesNotContain("?/span>", repaired);
        Assert.DoesNotContain("\uFF1F/td>", repaired);
        Assert.DoesNotContain("\uFF1F/span>", repaired);

        Assert.Equal(CountMatches(repaired, @"<td\b"), CountMatches(repaired, @"</td>"));
        Assert.Equal(CountMatches(repaired, @"<span\b"), CountMatches(repaired, @"</span>"));
    }

    [Fact]
    public void Export_NonTableTextContainingQuestionSlash_ShouldNotChange()
    {
        const string markdown = """
---
title: Sample
---

# Sample

outside ?/td> should stay
<table><tr><td>inside?/td></tr></table>
""";

        var repaired = InvokeRepair(markdown, "sample-page");

        Assert.Contains("outside ?/td> should stay", repaired);
        Assert.DoesNotContain("<td>inside?/td>", repaired);
        Assert.Contains("<td>inside</td>", repaired);
    }

    [Fact]
    public void Export_CleanTable_ShouldRemainUnchanged()
    {
        const string markdown = """
---
title: Clean
---

# Clean

<table><tr><td>Alpha</td><td><span>Beta</span></td></tr></table>
""";

        var repaired = InvokeRepair(markdown, "clean-page");

        Assert.Equal(markdown, repaired);
    }

    [Fact]
    public void Export_TableBalanceStillBroken_ShouldLogWarning()
    {
        const string markdown = """
---
title: Broken
---

# Broken

<table><tr><td>Alpha</tr></table>
""";

        var writer = new StringWriter();
        var listener = new TextWriterTraceListener(writer);
        Trace.Listeners.Add(listener);

        try
        {
            _ = InvokeRepair(markdown, "broken-page");
            Trace.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        var traceOutput = writer.ToString();
        Assert.Contains("page=broken-page", traceOutput);
        Assert.Contains("before=table=", traceOutput);
        Assert.Contains("after=table=", traceOutput);
    }

    private static string InvokeRepair(string markdown, string pageIdentity)
    {
        var method = typeof(MarkdownExporter).GetMethod(
            "RepairAndValidateTableMarkup",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method!.Invoke(null, [markdown, pageIdentity]) as string;
        Assert.NotNull(result);
        return result!;
    }

    private static int CountMatches(string input, string pattern)
    {
        return Regex.Matches(input, pattern, RegexOptions.IgnoreCase).Count;
    }
}
