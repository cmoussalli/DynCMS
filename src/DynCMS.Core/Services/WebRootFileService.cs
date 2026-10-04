using System.IO.Compression;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace DynCMS.Core.Services;

/// <summary>One file or folder inside the site's <c>wwwroot</c>.</summary>
public sealed record WebRootEntry(string Name, string Path, bool IsFolder, long SizeBytes, DateTime ModifiedUtc)
{
    public string Extension => IsFolder ? string.Empty : System.IO.Path.GetExtension(Name).ToLowerInvariant();
}

/// <summary>Result of extracting a ZIP archive into a folder.</summary>
public sealed record ZipExtractResult(int Files, int Folders, long Bytes);

/// <summary>
/// Manages the files in the site's <c>wwwroot</c> folder for the back office Files tab. Every path is relative to
/// <c>wwwroot</c> (forward slashes, no leading slash, empty = the root itself) and is validated so nothing outside
/// of it can be read or written.
/// </summary>
public interface IWebRootFileService
{
    /// <summary>Absolute path of the folder the service manages.</summary>
    string RootPath { get; }

    /// <summary>Extensions the built-in text editor opens.</summary>
    bool IsEditable(string path);

    IReadOnlyList<WebRootEntry> List(string folder);
    Task<string> ReadTextAsync(string path, CancellationToken ct = default);
    Task WriteTextAsync(string path, string content, CancellationToken ct = default);
    Task SaveAsync(string folder, string fileName, Stream content, CancellationToken ct = default);
    void CreateFolder(string folder, string name);
    void CreateFile(string folder, string name);
    void Delete(string path);
    string Rename(string path, string newName);

    /// <summary>Extracts <paramref name="zip"/> into <paramref name="folder"/>, creating it when missing.</summary>
    Task<ZipExtractResult> ExtractZipAsync(string folder, Stream zip, CancellationToken ct = default);

    /// <summary>Resolves a file for download, or <c>null</c> when it does not exist.</summary>
    string? FindFile(string path);

    /// <summary>Writes the folder (the whole <c>wwwroot</c> for an empty path) as a ZIP archive into <paramref name="output"/>.</summary>
    Task ZipFolderAsync(string folder, Stream output, CancellationToken ct = default);
}

public sealed class WebRootFileService : IWebRootFileService
{
    /// <summary>Largest text file the editor opens.</summary>
    public const long MaxEditableBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".mjs", ".json", ".map", ".html", ".htm", ".txt", ".md", ".xml", ".svg", ".csv",
        ".webmanifest", ".yml", ".yaml", ".ini", ".config", ".less", ".scss", ".ts"
    };

    private readonly DynCmsOptions _options;

    public WebRootFileService(IWebHostEnvironment environment, IOptions<DynCmsOptions> options, DynCmsPaths paths)
    {
        _options = options.Value;
        var root = environment.WebRootPath;
        if (string.IsNullOrEmpty(root)) root = Path.Combine(paths.BasePath, "wwwroot");
        RootPath = Path.GetFullPath(root);
    }

    public string RootPath { get; }

    public bool IsEditable(string path) => EditableExtensions.Contains(Path.GetExtension(path));

    // ---- paths ------------------------------------------------------------------------------------

    /// <summary>Normalises a relative path and maps it to an absolute one inside <c>wwwroot</c>.</summary>
    private string Resolve(string? relative, out string normalized)
    {
        var parts = new List<string>();
        foreach (var part in (relative ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidOperationException($"\"{part}\" is not a valid file or folder name.");
            parts.Add(part);
        }
        normalized = string.Join('/', parts);

        var full = Path.GetFullPath(Path.Combine(RootPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(full)) throw new InvalidOperationException("The path is outside of wwwroot.");
        return full;
    }

    private bool IsInside(string full) =>
        string.Equals(full, RootPath, StringComparison.OrdinalIgnoreCase) ||
        full.StartsWith(RootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ValidName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Enter a valid name (no slashes or special characters).");
        return name;
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    // ---- browsing ---------------------------------------------------------------------------------

    public IReadOnlyList<WebRootEntry> List(string folder)
    {
        var full = Resolve(folder, out var rel);
        if (!Directory.Exists(full))
        {
            if (rel.Length == 0) Directory.CreateDirectory(full);
            else throw new InvalidOperationException("That folder does not exist.");
        }

        var info = new DirectoryInfo(full);
        var entries = new List<WebRootEntry>();
        foreach (var dir in info.EnumerateDirectories())
            entries.Add(new WebRootEntry(dir.Name, Join(rel, dir.Name), true, 0, dir.LastWriteTimeUtc));
        foreach (var file in info.EnumerateFiles())
            entries.Add(new WebRootEntry(file.Name, Join(rel, file.Name), false, file.Length, file.LastWriteTimeUtc));

        return entries
            .OrderByDescending(e => e.IsFolder)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? FindFile(string path)
    {
        try
        {
            var full = Resolve(path, out _);
            return File.Exists(full) ? full : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    // ---- text files -------------------------------------------------------------------------------

    public async Task<string> ReadTextAsync(string path, CancellationToken ct = default)
    {
        var full = Resolve(path, out _);
        var info = new FileInfo(full);
        if (!info.Exists) throw new InvalidOperationException("That file does not exist.");
        if (info.Length > MaxEditableBytes) throw new InvalidOperationException("The file is too large for the editor (limit 2 MB).");
        return await File.ReadAllTextAsync(full, ct);
    }

    public async Task WriteTextAsync(string path, string content, CancellationToken ct = default)
    {
        var full = Resolve(path, out _);
        if (!File.Exists(full)) throw new InvalidOperationException("That file does not exist.");

        // Keep a byte-order mark if the file had one; otherwise write plain UTF-8.
        var bom = false;
        await using (var probe = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var head = new byte[3];
            bom = await probe.ReadAsync(head, ct) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        await File.WriteAllTextAsync(full, content, new System.Text.UTF8Encoding(bom), ct);
    }

    // ---- changes ----------------------------------------------------------------------------------

    public async Task SaveAsync(string folder, string fileName, Stream content, CancellationToken ct = default)
    {
        var dir = Resolve(folder, out var rel);
        var full = Resolve(Join(rel, ValidName(fileName)), out _);
        Directory.CreateDirectory(dir);
        await using var fs = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(fs, ct);
    }

    public void CreateFolder(string folder, string name)
    {
        Resolve(folder, out var rel);
        var full = Resolve(Join(rel, ValidName(name)), out _);
        if (Directory.Exists(full) || File.Exists(full)) throw new InvalidOperationException("A file or folder with that name already exists.");
        Directory.CreateDirectory(full);
    }

    public void CreateFile(string folder, string name)
    {
        Resolve(folder, out var rel);
        var full = Resolve(Join(rel, ValidName(name)), out _);
        if (Directory.Exists(full) || File.Exists(full)) throw new InvalidOperationException("A file or folder with that name already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, []);
    }

    public void Delete(string path)
    {
        var full = Resolve(path, out var rel);
        if (rel.Length == 0) throw new InvalidOperationException("The wwwroot folder itself cannot be deleted.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else if (File.Exists(full)) File.Delete(full);
        else throw new InvalidOperationException("That file or folder does not exist.");
    }

    public string Rename(string path, string newName)
    {
        var full = Resolve(path, out var rel);
        if (rel.Length == 0) throw new InvalidOperationException("The wwwroot folder itself cannot be renamed.");
        var target = Resolve(Join(rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : string.Empty, ValidName(newName)), out var targetRel);
        if (string.Equals(full, target, StringComparison.Ordinal)) return targetRel;
        if (File.Exists(target) || Directory.Exists(target)) throw new InvalidOperationException("A file or folder with that name already exists.");

        if (Directory.Exists(full)) Directory.Move(full, target);
        else if (File.Exists(full)) File.Move(full, target);
        else throw new InvalidOperationException("That file or folder does not exist.");
        return targetRel;
    }

    // ---- ZIP --------------------------------------------------------------------------------------

    public async Task<ZipExtractResult> ExtractZipAsync(string folder, Stream zip, CancellationToken ct = default)
    {
        var dir = Resolve(folder, out _);
        Directory.CreateDirectory(dir);

        // ZipArchive needs a seekable stream; Blazor upload streams are not.
        var tempPath = Path.Combine(Path.GetTempPath(), "dyncms-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await zip.CopyToAsync(temp, ct);

            ZipArchive archive;
            try { archive = new ZipArchive(new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read); }
            catch (InvalidDataException) { throw new InvalidOperationException("That is not a valid ZIP file."); }

            using (archive)
            {
                var limit = _options.MaxZipExtractBytes;
                if (archive.Entries.Sum(e => e.Length) > limit)
                    throw new InvalidOperationException($"The archive expands to more than {limit / (1024 * 1024)} MB, which is the extraction limit.");

                long bytes = 0;
                int files = 0, folders = 0;
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || name.EndsWith(".DS_Store", StringComparison.Ordinal)) continue;

                    // Zip-slip guard: the target must stay inside the folder being extracted to.
                    var target = Path.GetFullPath(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar)));
                    var dirPrefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!target.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase) || !IsInside(target))
                        throw new InvalidOperationException($"The archive contains an unsafe path (\"{entry.FullName}\") and was not extracted.");

                    if (name.EndsWith('/'))
                    {
                        if (!Directory.Exists(target)) { Directory.CreateDirectory(target); folders++; }
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = entry.Open();
                    await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await input.CopyToAsync(output, ct);
                    bytes += entry.Length;
                    files++;
                }
                return new ZipExtractResult(files, folders, bytes);
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { }
        }
    }

    public async Task ZipFolderAsync(string folder, Stream output, CancellationToken ct = default)
    {
        var dir = Resolve(folder, out _);
        if (!Directory.Exists(dir)) throw new InvalidOperationException("That folder does not exist.");

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var entryName = Path.GetRelativePath(dir, file).Replace('\\', '/');
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(file);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
            await using var entryStream = entry.Open();
            await input.CopyToAsync(entryStream, ct);
        }
        foreach (var empty in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                     .Where(d => !Directory.EnumerateFileSystemEntries(d).Any()))
            archive.CreateEntry(Path.GetRelativePath(dir, empty).Replace('\\', '/') + "/");
    }
}
