using DynCMS.Plugins.Models;
using DynCMS.Core.Security;

namespace DynCMS.Core.Api;

public sealed partial class CmsManagement
{
    public async Task<IReadOnlyList<MediaDto>> GetMediaChildrenAsync(Guid? folderId, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Media, CmsPermissions.Actions.Read);
        if (folderId is Guid f)
        {
            var folder = await media.GetAsync(f, ct) ?? throw CmsApiException.NotFound($"Media folder '{f}'");
            if (!folder.IsFolder) throw CmsApiException.BadRequest($"'{folder.Name}' is a file, not a folder.");
        }
        return (await media.GetChildrenAsync(folderId, ct)).Select(MapMedia).ToList();
    }

    public async Task<MediaDto> GetMediaAsync(Guid id, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Media, CmsPermissions.Actions.Read);
        var item = await media.GetAsync(id, ct) ?? throw CmsApiException.NotFound($"Media '{id}'");
        return MapMedia(item);
    }

    public async Task<MediaDto> CreateMediaFolderAsync(CreateMediaFolderRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Media, CmsPermissions.Actions.Create);
        if (string.IsNullOrWhiteSpace(request.Name)) throw CmsApiException.BadRequest("name is required.");
        return MapMedia(await Guard(() => media.CreateFolderAsync(request.ParentId, request.Name.Trim(), ct)));
    }

    public async Task<MediaDto> UploadMediaAsync(Guid? folderId, string fileName, string? mimeType, Stream stream, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Media, CmsPermissions.Actions.Create);
        if (string.IsNullOrWhiteSpace(fileName)) throw CmsApiException.BadRequest("fileName is required.");
        fileName = Path.GetFileName(fileName.Trim());
        if (folderId is Guid f)
        {
            var folder = await media.GetAsync(f, ct) ?? throw CmsApiException.NotFound($"Media folder '{f}'");
            if (!folder.IsFolder) throw CmsApiException.BadRequest($"'{folder.Name}' is a file, not a folder.");
        }
        return MapMedia(await Guard(() => media.UploadAsync(folderId, fileName, mimeType, stream, ct)));
    }

    public async Task<MediaDto> UploadMediaAsync(UploadMediaRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ContentBase64)) throw CmsApiException.BadRequest("contentBase64 is required.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.ContentBase64.Trim()); }
        catch (FormatException) { throw CmsApiException.BadRequest("contentBase64 is not valid base64."); }
        if (bytes.Length > options.Value.MaxUploadBytes) throw CmsApiException.BadRequest($"The file is larger than the allowed upload size ({options.Value.MaxUploadBytes} bytes).");

        await using var stream = new MemoryStream(bytes, writable: false);
        return await UploadMediaAsync(request.FolderId, request.FileName, request.MimeType, stream, ct);
    }

    public async Task<MediaDto> RenameMediaAsync(Guid id, RenameMediaRequest request, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Media, CmsPermissions.Actions.Update);
        if (string.IsNullOrWhiteSpace(request.Name)) throw CmsApiException.BadRequest("name is required.");
        var item = await Guard(() => media.RenameAsync(id, request.Name.Trim(), ct)) ?? throw CmsApiException.NotFound($"Media '{id}'");
        return MapMedia(item);
    }

    public async Task DeleteMediaAsync(Guid id, CancellationToken ct)
    {
        access.Require(CmsPermissions.Entities.Media, CmsPermissions.Actions.Delete);
        _ = await media.GetAsync(id, ct) ?? throw CmsApiException.NotFound($"Media '{id}'");
        await Guard(() => media.DeleteAsync(id, ct));
    }

    private MediaDto MapMedia(MediaItem m) => new(
        m.Id, m.ParentId, m.Name, m.IsFolder, m.FileName, m.Extension, m.MimeType, m.SizeBytes, m.IsImage,
        media.GetUrl(m), m.CreatedAt, m.UpdatedAt);
}
