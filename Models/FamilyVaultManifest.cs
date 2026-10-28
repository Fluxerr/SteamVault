using System;
using System.Collections.Generic;

namespace SteamVault.Models;

public class FamilyMember
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
    public string Name { get; set; } = "Vault Member";
    public bool IsHost { get; set; } = false;
    public DateTime JoinedDate { get; set; } = DateTime.Now;
}

public class FamilyGameItem
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public string HeaderImage { get; set; } = "";
    public string AddedBy { get; set; } = "Unknown";
    public DateTime AddedDate { get; set; } = DateTime.Now;
}

public class FamilyVaultManifest
{
    public string FamilyId { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpper();
    public string CloudRoomId { get; set; } = "";
    public bool IsDisbanded { get; set; } = false;
    public string FamilyName { get; set; } = "Family Vault";
    public string HostUsername { get; set; } = "VaultUser";
    public DateTime CreatedDate { get; set; } = DateTime.Now;

    public List<FamilyMember> Members { get; set; } = new();
    public List<FamilyGameItem> Games { get; set; } = new();
}
