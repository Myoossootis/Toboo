using System.IO.Ports;

namespace Toboo.Desktop.Services;

internal static class SerialPortCatalog
{
    public static string[] List() => Normalize(SerialPort.GetPortNames());
    public static string[] Normalize(IEnumerable<string> ports) => ports
        .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value.Length)
        .ThenBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
}
