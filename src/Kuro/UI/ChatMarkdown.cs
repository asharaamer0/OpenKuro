using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables;
using DocumentBlock = System.Windows.Documents.Block;
using DocumentInline = System.Windows.Documents.Inline;

namespace Kuro.UI;

internal static class ChatMarkdown
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().UsePipeTables().Build();

    public static RichTextBox Create(string markdown)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = 12, LineHeight = 19 };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Text");
        foreach (var block in Markdown.Parse(markdown, Pipeline)) document.Blocks.Add(Block(block));
        return new RichTextBox(document)
        {
            IsReadOnly = true, IsDocumentEnabled = true, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    static DocumentBlock Block(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var header = TextParagraph(heading.Inline);
                header.FontSize = heading.Level < 3 ? 15 : 13;
                header.FontWeight = FontWeights.SemiBold;
                header.Margin = new Thickness(0, 6, 0, 5);
                return header;
            case ParagraphBlock paragraph:
                return TextParagraph(paragraph.Inline);
            case CodeBlock code:
                var pre = new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new FontFamily("Consolas"), FontSize = 11, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 3, 0, 9) };
                pre.SetResourceReference(Paragraph.BackgroundProperty, "Elevated");
                return pre;
            case ListBlock list:
                var items = new List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Padding = new Thickness(19, 0, 0, 0), Margin = new Thickness(0, 1, 0, 8) };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var first)) items.StartIndex = Math.Max(1, first);
                foreach (var entry in list.OfType<ListItemBlock>())
                {
                    var item = new ListItem();
                    foreach (var child in entry) { var content = Block(child); content.Margin = new Thickness(0, 0, 0, 4); item.Blocks.Add(content); }
                    items.ListItems.Add(item);
                }
                return items;
            case QuoteBlock quote:
                var section = new Section { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(9, 0, 0, 0), Margin = new Thickness(0, 3, 0, 8) };
                section.SetResourceReference(Section.BorderBrushProperty, "Muted");
                foreach (var child in quote) section.Blocks.Add(Block(child));
                return section;
            case MdTable.Table table:
                return Table(table);
            case ThematicBreakBlock:
                var line = new Paragraph { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 6), LineHeight = 2 };
                line.SetResourceReference(Paragraph.BorderBrushProperty, "Border");
                return line;
            default:
                return TextParagraph(null);
        }
    }

    static Table Table(MdTable.Table source)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 3, 0, 10), FontSize = 11 };
        for (var col = 0; col < source.ColumnDefinitions.Count; col++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (var sourceRow in source.OfType<MdTable.TableRow>())
        {
            var row = new TableRow();
            group.Rows.Add(row);
            foreach (var sourceCell in sourceRow.OfType<MdTable.TableCell>())
            {
                var cell = new TableCell { Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(.5), FontWeight = sourceRow.IsHeader ? FontWeights.SemiBold : FontWeights.Normal };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Border");
                if (sourceRow.IsHeader) cell.SetResourceReference(TableCell.BackgroundProperty, "Elevated");
                foreach (var child in sourceCell) { var content = Block(child); content.Margin = new Thickness(0); cell.Blocks.Add(content); }
                row.Cells.Add(cell);
            }
        }
        return table;
    }

    static Paragraph TextParagraph(ContainerInline? source)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        Add(paragraph.Inlines, source);
        return paragraph;
    }

    static void Add(InlineCollection target, ContainerInline? source)
    {
        if (source == null) return;
        foreach (var child in source) target.Add(Inline(child));
    }

    static DocumentInline Inline(Markdig.Syntax.Inlines.Inline source)
    {
        switch (source)
        {
            case LiteralInline text: return new Run(text.Content.ToString());
            case CodeInline code:
                var inlineCode = new Run(code.Content) { FontFamily = new FontFamily("Consolas"), FontSize = 11 };
                inlineCode.SetResourceReference(Run.BackgroundProperty, "Elevated");
                return inlineCode;
            case LineBreakInline line: return line.IsHard ? new LineBreak() : new Run(" ");
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                Add(span.Inlines, emphasis);
                return span;
            case LinkInline link when !link.IsImage && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http":
                var hyperlink = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
                hyperlink.SetResourceReference(Hyperlink.ForegroundProperty, "Accent");
                hyperlink.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
                Add(hyperlink.Inlines, link);
                return hyperlink;
            case ContainerInline container:
                var group = new Span();
                Add(group.Inlines, container);
                return group;
            case HtmlEntityInline entity: return new Run(entity.Transcoded.ToString());
            default: return new Run();
        }
    }
}
