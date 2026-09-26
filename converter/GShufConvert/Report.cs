using System.Text;

namespace GShufConvert;

/// <summary>Collects what the converter did so the player can read it afterwards.</summary>
public sealed class Report
{
    private readonly StringBuilder _text = new();
    public int Warnings { get; private set; }

    public void Info(string message)
    {
        Console.WriteLine(message);
        _text.AppendLine(message);
    }

    public void Warn(string message)
    {
        Warnings++;
        Console.WriteLine("WARNING: " + message);
        _text.AppendLine("WARNING: " + message);
    }

    public override string ToString() => _text.ToString();
}
