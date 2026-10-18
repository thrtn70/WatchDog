namespace WatchDog.Core.Webcam;

public interface IWebcamDeviceEnumerator
{
    /// <summary>
    /// Returns validated physical-device metadata. This operation must not construct
    /// a capture source, select a default, or open a device.
    /// </summary>
    IReadOnlyList<WebcamDeviceInfo> GetPhysicalDevicesOrThrow();
}
