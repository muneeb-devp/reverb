using System.Globalization;
using System.Numerics;

namespace Reverb;

/// <summary>
/// A UI control bound to a variable. In the editor its value follows the user; with
/// <c>dotnet run</c> it keeps the default value it was created with.
/// </summary>
public abstract class Widget
{
    private protected Widget(string label) => Label = label;

    public string Label { get; }

    /// <summary>Set by a live host so user changes can find this widget. Null under <c>dotnet run</c>.</summary>
    public string? Id { get; internal set; }

    /// <summary>"slider", "dropdown", "text", "textarea", "checkbox", "number" or "button".</summary>
    public abstract string Kind { get; }

    public abstract object? BoxedValue { get; }

    /// <summary>
    /// For hosts: sets the value from user input. Returns false (and changes nothing) when the
    /// value doesn't fit this widget, e.g. it's outside a slider's range.
    /// </summary>
    public abstract bool TrySetValue(object? value);

    public override string ToString() =>
        $"[{Kind} {Label} = {Convert.ToString(BoxedValue, CultureInfo.CurrentCulture)}]";
}

public sealed class Slider : Widget
{
    internal Slider(string label, double min, double max, double value, double step) : base(label)
    {
        if (min > max) throw new ArgumentException($"Slider '{label}': min ({min}) is greater than max ({max}).");
        if (step <= 0) throw new ArgumentException($"Slider '{label}': step must be greater than zero.");
        (Min, Max, Step) = (min, max, step);
        Value = Math.Clamp(value, min, max);
    }

    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    public double Value { get; private set; }
    public override string Kind => "slider";
    public override object? BoxedValue => Value;

    public override bool TrySetValue(object? value)
    {
        if (value is not IConvertible c) return false;
        var d = c.ToDouble(CultureInfo.InvariantCulture);
        if (double.IsNaN(d) || d < Min || d > Max) return false;
        Value = d;
        return true;
    }
}

/// <summary>Non-generic view of a dropdown, for hosts.</summary>
public interface IDropdown
{
    IReadOnlyList<string> OptionTexts { get; }
    int SelectedIndex { get; }

    /// <summary>Selects an option by position. Returns false if there's no such option.</summary>
    bool TrySelect(int index);
}

/// <summary>Non-generic view of a number box, for hosts.</summary>
public interface INumberBox
{
    double? MinValue { get; }
    double? MaxValue { get; }
    double? StepValue { get; }
}

public sealed class Dropdown<T> : Widget, IDropdown
{
    internal Dropdown(string label, IReadOnlyList<T> options, T value) : base(label)
    {
        if (options.Count == 0) throw new ArgumentException($"Dropdown '{label}' needs at least one option.");
        if (!options.Contains(value)) throw new ArgumentException($"Dropdown '{label}': the value '{value}' isn't one of the options.");
        Options = options;
        Value = value;
    }

    public IReadOnlyList<T> Options { get; }
    public T Value { get; private set; }
    public override string Kind => "dropdown";

    IReadOnlyList<string> IDropdown.OptionTexts => [.. Options.Select(o => TextFormatter.Format(o))];
    int IDropdown.SelectedIndex => Options.ToList().IndexOf(Value);

    bool IDropdown.TrySelect(int index)
    {
        if (index < 0 || index >= Options.Count) return false;
        Value = Options[index];
        return true;
    }
    public override object? BoxedValue => Value;

    /// <summary>Accepts an option value or its index.</summary>
    public override bool TrySetValue(object? value)
    {
        if (value is T t && Options.Contains(t)) { Value = t; return true; }
        if (value is int i && i >= 0 && i < Options.Count) { Value = Options[i]; return true; }
        return false;
    }
}

public sealed class TextBox : Widget
{
    internal TextBox(string label, string value) : base(label) => Value = value;

    public string Value { get; private set; }
    public override string Kind => "text";
    public override object? BoxedValue => Value;

    public override bool TrySetValue(object? value)
    {
        if (value is not string s) return false;
        Value = s;
        return true;
    }
}

public sealed class TextArea : Widget
{
    internal TextArea(string label, string value, int rows) : base(label)
    {
        if (rows < 1) throw new ArgumentException($"Text area '{label}': rows must be at least 1.");
        Value = value;
        Rows = rows;
    }

    /// <summary>How many lines of text the box shows before it scrolls.</summary>
    public int Rows { get; }
    public string Value { get; private set; }
    public override string Kind => "textarea";
    public override object? BoxedValue => Value;

    /// <summary>The text's lines, trimmed, without the blank ones: one item per line someone typed or pasted.</summary>
    public IReadOnlyList<string> Lines =>
        Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public override bool TrySetValue(object? value)
    {
        if (value is not string s) return false;
        Value = s.ReplaceLineEndings("\n");
        return true;
    }
}

public sealed class Checkbox : Widget
{
    internal Checkbox(string label, bool value) : base(label) => Value = value;

    public bool Value { get; private set; }
    public override string Kind => "checkbox";
    public override object? BoxedValue => Value;

    public override bool TrySetValue(object? value)
    {
        if (value is not bool b) return false;
        Value = b;
        return true;
    }
}

public sealed class NumberBox<T> : Widget, INumberBox where T : struct, INumber<T>
{
    internal NumberBox(string label, T value, T? min, T? max, T? step) : base(label)
    {
        if (min is { } lo && max is { } hi && lo > hi) throw new ArgumentException($"Number '{label}': min ({lo}) is greater than max ({hi}).");
        (Min, Max, Step) = (min, max, step);
        Value = Fits(value) ? value : min ?? max ?? value;
    }

    public T? Min { get; }
    public T? Max { get; }
    public T? Step { get; }
    public T Value { get; private set; }
    public override string Kind => "number";

    double? INumberBox.MinValue => Min is { } v ? double.CreateChecked(v) : null;
    double? INumberBox.MaxValue => Max is { } v ? double.CreateChecked(v) : null;
    double? INumberBox.StepValue => Step is { } v ? double.CreateChecked(v) : null;
    public override object? BoxedValue => Value;

    bool Fits(T v) => (Min is not { } lo || v >= lo) && (Max is not { } hi || v <= hi);

    public override bool TrySetValue(object? value)
    {
        T v;
        try
        {
            v = value switch
            {
                T t => t,
                IConvertible c => T.CreateChecked(c.ToDouble(CultureInfo.InvariantCulture)),
                _ => throw new InvalidCastException(),
            };
        }
        catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException)
        {
            return false;
        }
        if (!Fits(v)) return false;
        Value = v;
        return true;
    }
}

public sealed class Button : Widget
{
    internal Button(string label) : base(label)
    {
    }

    /// <summary>How many times the button has been clicked.</summary>
    public int Value { get; private set; }
    public override string Kind => "button";
    public override object? BoxedValue => Value;

    public override bool TrySetValue(object? value)
    {
        if (value is not int n || n < 0) return false;
        Value = n;
        return true;
    }
}

/// <summary>Widget factories. Assign the result to a variable; cells that read it re-run when it changes.</summary>
public static class Ui
{
    public static Slider Slider(string label, double min = 0, double max = 100, double value = 0, double step = 1) =>
        Register(new Slider(label, min, max, value, step));

    public static Dropdown<T> Dropdown<T>(string label, IEnumerable<T> options)
    {
        var list = options.ToArray();
        return Register(new Dropdown<T>(label, list, list.Length > 0 ? list[0] : default!));
    }

    public static Dropdown<T> Dropdown<T>(string label, IEnumerable<T> options, T value) =>
        Register(new Dropdown<T>(label, options.ToArray(), value));

    public static TextBox Text(string label, string value = "") => Register(new TextBox(label, value));

    public static TextArea TextArea(string label, string value = "", int rows = 4) =>
        Register(new TextArea(label, value.ReplaceLineEndings("\n"), rows));

    public static Checkbox Checkbox(string label, bool value = false) => Register(new Checkbox(label, value));

    public static NumberBox<T> Number<T>(string label, T value, T? min = null, T? max = null, T? step = null)
        where T : struct, INumber<T> =>
        Register(new NumberBox<T>(label, value, min, max, step));

    public static Button Button(string label) => Register(new Button(label));

    static T Register<T>(T widget) where T : Widget
    {
        ReverbHost.Effective.WidgetCreated(widget);
        return widget;
    }
}
