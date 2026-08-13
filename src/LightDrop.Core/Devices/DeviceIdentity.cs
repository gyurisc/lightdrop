namespace LightDrop.Core.Devices;

/// <summary>
/// The stable identity of a LightDrop device.
/// </summary>
/// <param name="Id">
/// Generated once on first run and persisted thereafter. Peers pin trust to this value,
/// so it must never change unless the user explicitly resets it.
/// </param>
/// <param name="Name">The human-readable name peers address this device by.</param>
public sealed record DeviceIdentity(string Id, string Name);
