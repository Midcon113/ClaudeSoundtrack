using FoxRedbook;

namespace ClaudeSoundtrack.Core.Services;

/// <summary>An optical drive the user can rip from.</summary>
/// <param name="DevicePath">Path to open the drive with, e.g. "D:".</param>
/// <param name="DisplayName">Vendor and product string, e.g. "D: - HL-DT-ST BD-RE WH16NS40".</param>
public readonly record struct DriveDescriptor(string DevicePath, string DisplayName);

/// <summary>
/// Finds optical drives and reads what is currently in them.
///
/// Everything here is cheap: no audio is read, so it is safe to call while the
/// user is still deciding what to do.
/// </summary>
public sealed class OpticalDriveService
{
    /// <summary>
    /// Lists the optical drives Windows can see.
    ///
    /// The vendor string needs the drive opened, which fails when the drive is
    /// empty or busy. That is not an error worth surfacing - the drive still
    /// exists and can be selected - so it falls back to the bare letter.
    /// </summary>
    public IReadOnlyList<DriveDescriptor> GetDrives()
    {
        var drives = new List<DriveDescriptor>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.CDRom) continue;

            // "D:\" -> "D:", the form FoxRedbook expects.
            var devicePath = drive.Name.TrimEnd('\\', '/');
            var display = devicePath;

            try
            {
                using var handle = OpticalDrive.Open(devicePath) as IDisposable;
                if (handle is IOpticalDrive optical)
                {
                    var inquiry = optical.Inquiry;
                    var model = $"{inquiry.Vendor} {inquiry.Product}".Trim();
                    if (!string.IsNullOrWhiteSpace(model)) display = $"{devicePath} - {model}";
                }
            }
            catch
            {
                // Empty tray, drive busy, or no permission. The letter alone is fine.
            }

            drives.Add(new DriveDescriptor(devicePath, display));
        }

        return drives;
    }

    /// <summary>
    /// Reads the TOC, disc IDs and CD-Text from the disc in the given drive.
    ///
    /// Throws <see cref="MediaNotPresentException"/> when the tray is empty and
    /// <see cref="DriveNotReadyException"/> while the disc is still spinning up;
    /// the caller distinguishes these to tell the user which one happened.
    /// </summary>
    public async Task<DiscInfo> ReadDiscInfoAsync(string devicePath, CancellationToken cancellationToken = default)
    {
        var drive = OpticalDrive.Open(devicePath);
        try
        {
            return await drive.ReadDiscInfoAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (drive is IDisposable disposable) disposable.Dispose();
        }
    }
}
