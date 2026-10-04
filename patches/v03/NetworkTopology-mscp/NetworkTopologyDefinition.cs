using System;
using System.Collections.Generic;
using System.Drawing;
using NetworkTopology.Client;
using VideoOS.Platform;
using VideoOS.Platform.Client;

namespace NetworkTopology
{
    public sealed class NetworkTopologyDefinition : PluginDefinition
    {
        internal static readonly Guid PluginId = new Guid("8C54C8E8-9F12-4A7D-8B43-070BA3D55401");
        internal static readonly Guid ViewItemPluginId = new Guid("8C54C8E8-9F12-4A7D-8B43-070BA3D55402");
        internal static readonly Guid WorkspacePluginId = new Guid("8C54C8E8-9F12-4A7D-8B43-070BA3D55403");

        private readonly List<ViewItemPlugin> _viewItems = new List<ViewItemPlugin>();
        private readonly List<WorkSpacePlugin> _workspaces = new List<WorkSpacePlugin>();

        public override Guid Id => PluginId;
        public override string Name => "Network Topology";
        public override string Manufacturer => "Megalcom";
        public override string VersionString => "0.3.0";
        public override Image Icon => VideoOS.Platform.UI.Util.ImageList.Images[VideoOS.Platform.UI.Util.PluginIx];

        public override void Init()
        {
            if (EnvironmentManager.Instance.EnvironmentType != EnvironmentType.SmartClient)
                return;

            _viewItems.Add(new NetworkTopologyViewItemPlugin());
            _workspaces.Add(new NetworkTopologyWorkspacePlugin());
        }

        public override void Close()
        {
            _viewItems.Clear();
            _workspaces.Clear();
        }

        public override List<ViewItemPlugin> ViewItemPlugins => _viewItems;
        public override List<WorkSpacePlugin> WorkSpacePlugins => _workspaces;
    }
}
