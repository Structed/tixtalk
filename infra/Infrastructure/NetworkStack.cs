using Pulumi;
using Pulumi.AzureNative.Network;
using Pulumi.AzureNative.Resources;
using NetworkInputs = Pulumi.AzureNative.Network.Inputs;

namespace PreTalxTix.Infra.Infrastructure;

public record NetworkResult(
    VirtualNetwork Vnet,
    Output<string> SubnetId,
    PublicIPAddress PublicIp,
    NetworkInterface Nic
);

public static class NetworkStack
{
    public static NetworkResult Create(string prefix, ResourceGroup rg)
    {
        // Network Security Group — allow SSH, HTTP, HTTPS, HTTP/3
        var nsg = new NetworkSecurityGroup($"{prefix}-nsg", new NetworkSecurityGroupArgs
        {
            NetworkSecurityGroupName = $"{prefix}-nsg",
            ResourceGroupName = rg.Name,
            SecurityRules = new[]
            {
                new NetworkInputs.SecurityRuleArgs
                {
                    Name = "AllowSSH",
                    Priority = 100,
                    Direction = SecurityRuleDirection.Inbound,
                    Access = SecurityRuleAccess.Allow,
                    Protocol = SecurityRuleProtocol.Tcp,
                    SourceAddressPrefix = "*",
                    SourcePortRange = "*",
                    DestinationAddressPrefix = "*",
                    DestinationPortRange = "22",
                },
                new NetworkInputs.SecurityRuleArgs
                {
                    Name = "AllowHTTP",
                    Priority = 200,
                    Direction = SecurityRuleDirection.Inbound,
                    Access = SecurityRuleAccess.Allow,
                    Protocol = SecurityRuleProtocol.Tcp,
                    SourceAddressPrefix = "*",
                    SourcePortRange = "*",
                    DestinationAddressPrefix = "*",
                    DestinationPortRange = "80",
                },
                new NetworkInputs.SecurityRuleArgs
                {
                    Name = "AllowHTTPS",
                    Priority = 300,
                    Direction = SecurityRuleDirection.Inbound,
                    Access = SecurityRuleAccess.Allow,
                    Protocol = SecurityRuleProtocol.Tcp,
                    SourceAddressPrefix = "*",
                    SourcePortRange = "*",
                    DestinationAddressPrefix = "*",
                    DestinationPortRange = "443",
                },
                new NetworkInputs.SecurityRuleArgs
                {
                    Name = "AllowHTTP3",
                    Priority = 400,
                    Direction = SecurityRuleDirection.Inbound,
                    Access = SecurityRuleAccess.Allow,
                    Protocol = SecurityRuleProtocol.Udp,
                    SourceAddressPrefix = "*",
                    SourcePortRange = "*",
                    DestinationAddressPrefix = "*",
                    DestinationPortRange = "443",
                },
            },
        });

        // Virtual Network
        var vnet = new VirtualNetwork($"{prefix}-vnet", new VirtualNetworkArgs
        {
            VirtualNetworkName = $"{prefix}-vnet",
            ResourceGroupName = rg.Name,
            AddressSpace = new NetworkInputs.AddressSpaceArgs
            {
                AddressPrefixes = new[] { "10.0.0.0/16" },
            },
            Subnets = new[]
            {
                new NetworkInputs.SubnetArgs
                {
                    Name = "default",
                    AddressPrefix = "10.0.1.0/24",
                    NetworkSecurityGroup = new NetworkInputs.NetworkSecurityGroupArgs
                    {
                        Id = nsg.Id,
                    },
                },
            },
        });

        var subnetId = vnet.Subnets.Apply(s => s![0].Id!);

        // Static Public IP
        var publicIp = new PublicIPAddress($"{prefix}-ip", new PublicIPAddressArgs
        {
            PublicIpAddressName = $"{prefix}-ip",
            ResourceGroupName = rg.Name,
            PublicIPAllocationMethod = IPAllocationMethod.Static,
            Sku = new NetworkInputs.PublicIPAddressSkuArgs
            {
                Name = PublicIPAddressSkuName.Standard,
            },
        });

        // Network Interface
        var nic = new NetworkInterface($"{prefix}-nic", new NetworkInterfaceArgs
        {
            NetworkInterfaceName = $"{prefix}-nic",
            ResourceGroupName = rg.Name,
            IpConfigurations = new[]
            {
                new NetworkInputs.NetworkInterfaceIPConfigurationArgs
                {
                    Name = "primary",
                    Subnet = new NetworkInputs.SubnetArgs
                    {
                        Id = subnetId,
                    },
                    PrivateIPAllocationMethod = IPAllocationMethod.Dynamic,
                    PublicIPAddress = new NetworkInputs.PublicIPAddressArgs
                    {
                        Id = publicIp.Id,
                    },
                },
            },
        });

        return new NetworkResult(
            Vnet: vnet,
            SubnetId: subnetId,
            PublicIp: publicIp,
            Nic: nic
        );
    }
}
