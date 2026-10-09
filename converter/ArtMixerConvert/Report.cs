using System.Text;

namespace ArtMixerConvert;

/// <summary>Collects what the converter did so the player can read it afterwards.</summary>
public sealed class Report
{
    private readonly StringBuilder _text = new();
    public int Warnings { get; private set; }
    /// <summary>Receives every line as it is written (console or window log).</summary>
    public Action<string>? Sink { get; init; }

    public void Info(string message)
    {
        Sink?.Invoke(message);
        _text.AppendLine(message);
    }

    public void Warn(string message)
    {
        Warnings++;
        Sink?.Invoke("WARNING: " + message);
        _text.AppendLine("WARNING: " + message);
    }

    public override string ToString() => _text.ToString();

    /// <summary>A count with its noun in singular or plural form: "1 pack", "2 packs".</summary>
    public static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
