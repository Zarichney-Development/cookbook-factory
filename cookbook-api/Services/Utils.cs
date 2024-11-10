using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cookbook.Factory.Services;

public static class Utils
{
    public static T Deserialize<T>(string content)
        => JsonSerializer.Deserialize<T>(content,
               new JsonSerializerOptions
               {
                   PropertyNameCaseInsensitive = true,
                   IncludeFields = true
               })
           ?? throw new JsonException($"Failed to deserialize function arguments: {content} for type {typeof(T).Name}");

    public static string SplitCamelCase(string input)
    {
        return string.Concat(input.Select((x, i) => i > 0 && char.IsUpper(x) ? " " + x : x.ToString()));
    }

    // MarkdownHelper methods
    public static string GetPropertyValue(object obj, string propertyName)
    {
        var property = obj.GetType().GetProperty(propertyName);
        return property?.GetValue(obj)?.ToString() ?? string.Empty;
    }

    public static List<string> GetListPropertyValue(object obj, string propertyName)
    {
        var property = obj.GetType().GetProperty(propertyName);
        return (property?.GetValue(obj) as IEnumerable<string> ?? Enumerable.Empty<string>()).ToList();
    }

    // MarkdownConverter methods
    public static string ToMarkdownHeader(string text, int level = 1)
    {
        return $"{new string('#', level)} {text}\n\n";
    }

    public static string ToMarkdownImage(string altText, string url)
    {
        return $"![{altText}]({url})\n\n";
    }

    public static string ToMarkdownProperty(string name, string value)
    {
        return $"**{name}:** {value}\t";
    }

    public static string ToMarkdownList(IEnumerable<string> items, bool numbered = false)
    {
        return string.Join("\n", items.Select((item, index) =>
            numbered ? $"{index + 1}. {item}" : $"- {item}")) + "\n\n";
    }

    public static string ToMarkdownSection(string title, string content)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(title))
        {
            sb.Append($"## {title}\n\n");
        }

        sb.Append($"{content}\n\n");
        return sb.ToString();
    }

    public static string ToMarkdownBlockquote(string text)
    {
        return string.Join("\n", text.Split('\n').Select(line => $"> {line}")) + "\n\n";
    }

    public static string ToMarkdownCodeBlock(string code, string language = "")
    {
        return $"```{language}\n{code}\n```\n\n";
    }

    public static string ToMarkdownLink(string text, string url)
    {
        return $"[{text}]({url})";
    }

    public static string ToMarkdownHorizontalRule()
    {
        return "---\n\n";
    }

    public static string ToMarkdownTable(List<List<string>> rows)
    {
        var headers = rows.Select(_ => " ").ToList();

        return ToMarkdownTable(headers, rows);
    }

    private static string ToMarkdownTable(List<string> headers, List<List<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{.tableStyle}");
        sb.AppendLine($"| {string.Join(" | ", headers)} |");
        sb.AppendLine($"| {string.Join(" | ", headers.Select(_ => "---"))} |");
        foreach (var row in rows)
        {
            sb.AppendLine($"| {string.Join(" | ", row)} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }
}

public static class ObjectExtensions
{
    public static string ToMarkdown(this object obj, string title)
    {
        var sb = new StringBuilder($"## {title}\n");
        var properties = obj.GetType().GetProperties();

        foreach (var prop in properties)
        {
            AppendPropertyIfNotEmpty(sb, obj, prop);
        }

        return sb.ToString();
    }

    private static void AppendPropertyIfNotEmpty(StringBuilder sb, object obj, PropertyInfo prop)
    {
        var value = prop.GetValue(obj);
        if (IsNullOrEmpty(value)) return;

        var propertyName = Utils.SplitCamelCase(prop.Name);

        sb.Append($"{propertyName}: ");

        if (value is IEnumerable<object> list)
        {
            sb.AppendLine(string.Join(", ", list));
        }
        else
        {
            sb.AppendLine(value?.ToString());
        }
    }

    private static bool IsNullOrEmpty(object? value)
    {
        if (value == null) return true;
        if (value is string str) return string.IsNullOrEmpty(str);
        if (value is IEnumerable<object> list) return !list.Any();
        if (value.GetType().IsValueType) return value.Equals(Activator.CreateInstance(value.GetType()));
        return false;
    }

    public static string ToMarkdownHeader(this object obj, string propertyName, int level = 1)
    {
        var value = Utils.GetPropertyValue(obj, propertyName);
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Utils.ToMarkdownHeader(value, level);
    }

    public static string ToMarkdownImage(this object obj, string altTextPropertyName, string urlPropertyName)
    {
        var altText = Utils.GetPropertyValue(obj, altTextPropertyName);
        var url = Utils.GetPropertyValue(obj, urlPropertyName);
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return Utils.ToMarkdownImage(altText, url);
    }

    public static string ToMarkdownProperty(this object obj, string propertyName)
    {
        var value = Utils.GetPropertyValue(obj, propertyName);
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Utils.ToMarkdownProperty(Utils.SplitCamelCase(propertyName), value);
    }

    public static string ToMarkdownList(this object obj, string propertyName)
    {
        var items = Utils.GetListPropertyValue(obj, propertyName);
        if (!items.Any()) return string.Empty;
        return Utils.ToMarkdownHeader(Utils.SplitCamelCase(propertyName), 2) +
               Utils.ToMarkdownList(items);
    }

    public static string ToMarkdownNumberedList(this object obj, string propertyName)
    {
        var items = Utils.GetListPropertyValue(obj, propertyName);
        if (!items.Any()) return string.Empty;
        return Utils.ToMarkdownHeader(Utils.SplitCamelCase(propertyName), 2) +
               Utils.ToMarkdownList(items, true);
    }

    public static string ToMarkdownSection(this object obj, string propertyName, bool includeTitle = true)
    {
        var content = Utils.GetPropertyValue(obj, propertyName);
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        string title = string.Empty;
        if (includeTitle)
        {
            title = Utils.SplitCamelCase(propertyName);
        }

        return Utils.ToMarkdownSection(title, content);
    }
}

public class AtomicCounter
{
    private int _value;
    public int Increment() => Interlocked.Increment(ref _value);
}

public static class HtmlStripper
{
    private static readonly Regex StripHtmlRegex = new Regex(
        @"</?(?!h[1-6]|p|br/?|strong|em|ul|ol|li|blockquote)[^>]+>|<(h[1-6]|p|blockquote)[\s>]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex CleanupRegex = new Regex(
        @"^\s+|&nbsp;|\s+$|\n\s*\n\s*\n",
        RegexOptions.Multiline | RegexOptions.Compiled
    );

    public static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        // Replace <br> tags with newlines
        html = Regex.Replace(html, @"<br\s*/?>\s*", "\n", RegexOptions.IgnoreCase);

        // Strip most HTML tags, but keep headings, paragraphs, and some inline elements
        html = StripHtmlRegex.Replace(html, string.Empty);

        // Convert remaining heading tags to uppercase text with newlines
        html = Regex.Replace(html, @"<h([1-6])>(.*?)</h\1>", m =>
            $"\n{m.Groups[2].Value.ToUpper()}\n", RegexOptions.IgnoreCase);

        // Convert <li> to bullet points
        html = Regex.Replace(html, @"<li>(.*?)</li>", m => $"\n• {m.Groups[1].Value}", RegexOptions.IgnoreCase);

        // Remove any remaining HTML tags
        html = Regex.Replace(html, @"<[^>]+>", string.Empty);

        // Decode HTML entities
        html = System.Net.WebUtility.HtmlDecode(html);

        // Clean up extra whitespace and blank lines
        html = CleanupRegex.Replace(html, string.Empty);

        return html.Trim();
    }
}