using Pulumi;
using Pulumi.AzureNative.Compute;
using Pulumi.AzureNative.Resources;
using ComputeInputs = Pulumi.AzureNative.Compute.Inputs;

namespace TixTalk.Infra.Infrastructure;

public record VirtualMachineArgs
{
    public required string Prefix { get; init; }
    public required ResourceGroup ResourceGroup { get; init; }
    public required NetworkResult Network { get; init; }
    public required string VmSize { get; init; }
    public required string SshPublicKey { get; init; }
    public required Output<string> CloudInitScript { get; init; }
    public string AdminUsername { get; init; } = "azureuser";
}

public record VirtualMachineResult(
    Pulumi.AzureNative.Compute.VirtualMachine Vm,
    Output<string> PublicIpAddress
);

public static class VirtualMachineStack
{
    public static VirtualMachineResult Create(VirtualMachineArgs args)
    {
        var vmName = $"{args.Prefix}-vm";

        // Base64-encode the cloud-init script for customData
        var customData = args.CloudInitScript.Apply(script =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script)));

        var vm = new Pulumi.AzureNative.Compute.VirtualMachine(vmName, new Pulumi.AzureNative.Compute.VirtualMachineArgs
        {
            VmName = vmName,
            ResourceGroupName = args.ResourceGroup.Name,
            HardwareProfile = new ComputeInputs.HardwareProfileArgs
            {
                VmSize = args.VmSize,
            },
            OsProfile = new ComputeInputs.OSProfileArgs
            {
                ComputerName = vmName,
                AdminUsername = args.AdminUsername,
                CustomData = customData,
                LinuxConfiguration = new ComputeInputs.LinuxConfigurationArgs
                {
                    DisablePasswordAuthentication = true,
                    Ssh = new ComputeInputs.SshConfigurationArgs
                    {
                        PublicKeys = new[]
                        {
                            new ComputeInputs.SshPublicKeyArgs
                            {
                                Path = $"/home/{args.AdminUsername}/.ssh/authorized_keys",
                                KeyData = args.SshPublicKey,
                            },
                        },
                    },
                },
            },
            StorageProfile = new ComputeInputs.StorageProfileArgs
            {
                ImageReference = new ComputeInputs.ImageReferenceArgs
                {
                    Publisher = "Canonical",
                    Offer = "ubuntu-24_04-lts",
                    Sku = "server",
                    Version = "latest",
                },
                OsDisk = new ComputeInputs.OSDiskArgs
                {
                    Name = $"{vmName}-osdisk",
                    CreateOption = DiskCreateOptionTypes.FromImage,
                    DiskSizeGB = 64,
                    ManagedDisk = new ComputeInputs.ManagedDiskParametersArgs
                    {
                        StorageAccountType = StorageAccountTypes.Standard_LRS,
                    },
                },
            },
            NetworkProfile = new ComputeInputs.NetworkProfileArgs
            {
                NetworkInterfaces = new[]
                {
                    new ComputeInputs.NetworkInterfaceReferenceArgs
                    {
                        Id = args.Network.Nic.Id,
                        Primary = true,
                    },
                },
            },
        });

        // Resolve the public IP address from the Public IP resource
        var publicIpAddress = args.Network.PublicIp.IpAddress.Apply(ip => ip ?? "");

        return new VirtualMachineResult(
            Vm: vm,
            PublicIpAddress: publicIpAddress
        );
    }
}
