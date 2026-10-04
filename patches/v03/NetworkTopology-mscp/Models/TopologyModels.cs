using System;
using System.Collections.Generic;

namespace NetworkTopology.Models
{
    public enum NodeState { Unknown, Online, Offline }

    public sealed class CameraDevice
    {
        public Guid CameraId { get; set; }
        public string CameraName { get; set; }
        public string HardwareName { get; set; }
        public string Address { get; set; }
        public string IpAddress { get; set; }
        public string Model { get; set; }
        public string RecordingServerName { get; set; }
        public string HardwarePath { get; set; }
        public string HardwareUserName { get; set; }
        public NodeState State { get; set; }
        public long PingMs { get; set; } = -1;
        public string Error { get; set; }
    }

    public sealed class SwitchNode
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string IpAddress { get; set; }
        public string ParentId { get; set; }
        public string UplinkPort { get; set; }
        public string Role { get; set; }
        public string ChassisId { get; set; }
        public string Description { get; set; }
        public string Source { get; set; }
        public bool AutoDiscovered { get; set; }
        public NodeState State { get; set; }
        public long PingMs { get; set; } = -1;
    }

    public sealed class CameraMapping
    {
        public string CameraIp { get; set; }
        public string CameraName { get; set; }
        public string SwitchId { get; set; }
        public string SwitchPort { get; set; }
    }

    public sealed class TopologyConfiguration
    {
        public List<SwitchNode> Switches { get; set; } = new List<SwitchNode>();
        public List<CameraMapping> CameraMappings { get; set; } = new List<CameraMapping>();
    }

    public sealed class BoardNode
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string DeviceKey { get; set; }
        public string DisplayName { get; set; }
        public string IpAddress { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class BoardLink
    {
        public string Id { get; set; }
        public string FromNodeId { get; set; }
        public string ToNodeId { get; set; }
        public string Label { get; set; }
    }

    public sealed class TopologyBoard
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<BoardNode> Nodes { get; set; } = new List<BoardNode>();
        public List<BoardLink> Links { get; set; } = new List<BoardLink>();
    }

    public sealed class WorkspaceState
    {
        public string ActiveBoardId { get; set; }
        public List<TopologyBoard> Boards { get; set; } = new List<TopologyBoard>();
        public List<SwitchNode> Switches { get; set; } = new List<SwitchNode>();
    }

    public sealed class SwitchDiscoveryResult
    {
        public bool Success { get; set; }
        public string SwitchName { get; set; }
        public string ManagementIp { get; set; }
        public string PortId { get; set; }
        public string ChassisId { get; set; }
        public string Description { get; set; }
        public string Protocol { get; set; }
        public string Error { get; set; }
    }
}
