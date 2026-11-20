using System.Text;
using System.Text.Json;

namespace Reverb;

/// <summary>
/// One displayed value as a set of representations keyed by MIME type. <c>text/plain</c> is
/// always present; the editor picks the richest one it can show.
/// </summary>
public sealed class Output
{
    public const string PlainText = "text/plain";
    public const string Markdown = "text/markdown";
    public const string Html = "text/html";
    public const string Svg = "image/svg+xml";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Table = "application/vnd.reverb.table+json";
    public const string Object = "application/vnd.reverb.object+json";
    public const string Widget = "application/vnd.reverb.widget+json";
    public const string Error = "application/vnd.reverb.error+json";
    public const string VegaLite = "application/vnd.vegalite.v5+json";

    public Output(IReadOnlyDictionary<string, string> data, string? title = null)
    {
        if (!data.ContainsKey(PlainText))
            throw new ArgumentException("An output needs a text/plain representation.", nameof(data));
        Data = data;
        Title = title;
    }

    public IReadOnlyDictionary<string, string> Data { get; }
    public string? Title { get; }
    public string Text => Data[PlainText];

    public Output WithTitle(string? title) => new(Data, title);

    public static Output FromText(string text) => new(new Dictionary<string, string> { [PlainText] = text });

    public static Output FromMarkdown(string markdown) =>
        new(new Dictionary<string, string> { [PlainText] = markdown, [Markdown] = markdown });

    public static Output FromHtml(string html, string? text = null) =>
        new(new Dictionary<string, string> { [PlainText] = text ?? html, [Html] = html });
}

/// <summary>
/// Turns values into <see cref="Output"/>s. Register a formatter for your own types with
/// <see cref="Register{T}"/>; the most recently registered one that fits wins.
/// </summary>
public static class Formatters
{
    static readonly Lock gate = new();
    static readonly List<(Type Type, Func<object, Output> Format)> registered = [];

    public static void Register<T>(Func<T, Output> format)
    {
        lock (gate)
        {
            registered.RemoveAll(r => r.Type == typeof(T));
            registered.Add((typeof(T), v => format((T)v)));
        }
    }

    /// <summary>Removes all registered formatters.</summary>
    public static void Reset()
    {
        lock (gate) registered.Clear();
    }

    public static Output Format(object? value)
    {
        if (value is not null)
        {
            Func<object, Output>? custom = null;
            lock (gate)
            {
                for (var i = registered.Count - 1; i >= 0 && custom is null; i--)
                    if (registered[i].Type.IsInstanceOfType(value)) custom = registered[i].Format;
            }
            if (custom is not null) return custom(value);
        }

        switch (value)
        {
            case Output o:
                return o;
            case Chart c:
                return c.Output;
            case Widget w:
                return new Output(new Dictionary<string, string> { [Output.PlainText] = w.ToString(), [Output.Widget] = WidgetJson(w) });
            case Exception e:
                return FormatException(e, null);
            case byte[] bytes when ImageType(bytes) is { } mime:
                return new Output(new Dictionary<string, string>
                {
                    [Output.PlainText] = $"[{mime} image, {bytes.Length} bytes]",
                    [mime] = Convert.ToBase64String(bytes),
                });
        }

        var text = TextFormatter.Format(value);
        if (TextFormatter.Table(value) is { } table)
            return new Output(new Dictionary<string, string> { [Output.PlainText] = text, [Output.Table] = TableJson(table) });
        return Output.FromText(text);
    }

    /// <summary>An error output. <paramref name="location"/> is a short "cell 'x', line 3" style hint.</summary>
    public static Output FormatException(Exception e, string? location)
    {
        var text = $"{e.GetType().Name}: {e.Message}{(location is null ? "" : $"\n  at {location}")}";
        var json = Json(w =>
        {
            w.WriteString("type", e.GetType().FullName);
            w.WriteString("message", e.Message);
            if (location is not null) w.WriteString("location", location);
            w.WriteString("stackTrace", e.StackTrace);
        });
        return new Output(new Dictionary<string, string> { [Output.PlainText] = text, [Output.Error] = json });
    }

    static string? ImageType(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? Output.Png
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? Output.Jpeg
        : null;

    public static string TableJson(TableData table) => Json(w =>
    {
        w.WriteStartArray("columns");
        foreach (var c in table.Columns) w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteStartArray("rows");
        foreach (var row in table.Rows)
        {
            w.WriteStartArray();
            foreach (var cell in row) w.WriteStringValue(cell);
            w.WriteEndArray();
        }
        w.WriteEndArray();
        w.WriteNumber("total", table.Total);
    });

    static string WidgetJson(Widget widget) => Json(w =>
    {
        w.WriteString("kind", widget.Kind);
        w.WriteString("label", widget.Label);
        if (widget.Id is not null) w.WriteString("id", widget.Id);
        w.WritePropertyName("value");
        WriteValue(w, widget.BoxedValue);
        switch (widget)
        {
            case Slider s:
                w.WriteNumber("min", s.Min);
                w.WriteNumber("max", s.Max);
                w.WriteNumber("step", s.Step);
                break;
            case TextArea t:
                w.WriteNumber("rows", t.Rows);
                break;
            case IDropdown d:
                w.WriteStartArray("options");
                foreach (var o in d.OptionTexts) w.WriteStringValue(o);
                w.WriteEndArray();
                w.WriteNumber("index", d.SelectedIndex);
                break;
            case INumberBox n:
                if (n.MinValue is { } min) w.WriteNumber("min", min);
                if (n.MaxValue is { } max) w.WriteNumber("max", max);
                if (n.StepValue is { } step) w.WriteNumber("step", step);
                break;
        }
    });

    static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case string s: w.WriteStringValue(s); break;
            case IConvertible c when value.GetType().IsPrimitive || value is decimal:
                w.WriteNumberValue(c.ToDouble(System.Globalization.CultureInfo.InvariantCulture));
                break;
            default: w.WriteStringValue(TextFormatter.Format(value)); break;
        }
    }

    static string Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
