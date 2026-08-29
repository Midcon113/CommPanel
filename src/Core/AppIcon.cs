using System.Reflection;

namespace CommPanel.Core;

/// <summary>Loads the embedded application icon at the size Windows asks for.</summary>
internal static class AppIcon
{
    private const string ResourceName = "CommPanel.ico";

    public static Icon? Load(int size)
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null) return null;
            return new Icon(stream, size, size);
        }
        catch
        {
            return null;
        }
    }

    public static Icon? LoadTray() =>
        Load(SystemInformation.SmallIconSize.Width <= 0 ? 16 : SystemInformation.SmallIconSize.Width);
}
