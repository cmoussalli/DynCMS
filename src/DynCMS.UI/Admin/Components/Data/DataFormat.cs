namespace DynCMS.UI.Admin.Components;

/// <summary>Formatting helpers shared by the Data tab components.</summary>
public static class DataFormat
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "—";
        double value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    public static string Rows(long? rows) => rows is null ? "—" : rows.Value.ToString("N0");

    public static string Local(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} d ago";
        return utc.ToLocalTime().ToString("d MMM yyyy");
    }
}
