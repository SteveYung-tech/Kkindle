namespace Kkindle.Core;

/// <summary>
/// Encrypts small secrets (API keys, SMTP and Z-Library passwords) with a
/// key that belongs to the current OS user, so a copied settings file is
/// useless on another machine or account.
///
/// Protected values are inherently machine-bound: backups never carry them
/// (see <c>AppBackupService</c>), and a failed <see cref="Unprotect"/> is
/// treated as "no stored secret" rather than an error.
///
/// Windows implementation uses DPAPI. A Linux implementation should use
/// libsecret / Secret Service, macOS should use the Keychain.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="value"/>. Empty input returns empty.</summary>
    byte[] Protect(byte[] value);

    /// <summary>
    /// Decrypts a blob produced by <see cref="Protect"/> on this machine and
    /// user account. Throws when the blob was produced elsewhere.
    /// </summary>
    byte[] Unprotect(byte[] value);
}

/// <summary>
/// Raises an event whenever removable storage is attached or detached, so the
/// Kindle device list can refresh without polling.
///
/// This only signals "something changed" — enumeration stays in
/// <see cref="IKindleDeviceService.DetectDevicesAsync"/>. Callers must keep a
/// polling fallback: the notification is best-effort and platform specific.
///
/// Windows implementation subclasses the window procedure and listens for
/// WM_DEVICECHANGE. A Linux implementation should use udev, macOS IOKit.
/// </summary>
public interface IDeviceChangeNotifier : IDisposable
{
    event EventHandler? DeviceChanged;
}
